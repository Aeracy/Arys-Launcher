using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.FileExtractors;
using CmlLib.Core.Installer.Forge;
using CmlLib.Core.Installer.Forge.Installers;
using CmlLib.Core.ProcessBuilder;

namespace ArysLauncher;

public sealed class GameRuntime
{
    private readonly LauncherStorage storage;
    private readonly JavaRuntimeCatalog javaCatalog;

    public GameRuntime(LauncherStorage storage, JavaRuntimeCatalog javaCatalog)
    {
        this.storage = storage;
        this.javaCatalog = javaCatalog;
    }

    public async Task<string> PrepareAsync(GameInstallation instance, IProgress<WorkSnapshot> progress, CancellationToken cancellationToken)
    {
        storage.Settings.Validate();
        var java = await javaCatalog.ResolveAsync(storage.Settings, cancellationToken);
        using var client = NetworkClients.Create(storage.Settings, download: true);
        var launcher = CreateLauncher(instance, client, java?.ExecutablePath);
        using var installer = (GameFileInstaller)launcher.GameInstaller;
        return await PrepareAsync(instance, launcher, java?.ExecutablePath, progress, cancellationToken);
    }

    public async Task<RunningGame> LaunchAsync(GameInstallation instance, LauncherAccount account,
        IProgress<WorkSnapshot> progress, CancellationToken cancellationToken)
    {
        storage.Settings.Validate();
        var selectedJava = await javaCatalog.ResolveAsync(storage.Settings, cancellationToken);
        var maximumMemory = MemoryAllocation.MaximumMb(storage.Settings, MemoryAllocation.ReadPhysicalMemory());
        if (selectedJava is { Is64Bit: false } && maximumMemory > 1536)
        {
            throw new InvalidOperationException(LauncherText.Get("Java32BitMemoryLimit"));
        }
        if (instance.ModCount > 0 && string.IsNullOrWhiteSpace(instance.ForgeVersion) && string.IsNullOrWhiteSpace(instance.LaunchVersion))
        {
            throw new InvalidOperationException(LauncherText.Get("ForgeVersionRequired"));
        }
        if (!Directory.Exists(instance.DirectoryPath))
        {
            throw new DirectoryNotFoundException(LauncherText.Get("GameDirectoryMissing"));
        }
        using var accounts = new AccountService(storage);
        var session = await accounts.GetSessionAsync(account,
            message => progress.Report(new WorkSnapshot(LauncherText.Get("SigningIn"), message, 0, 0, 0, 0, 0, 0, true)), cancellationToken);
        using var client = NetworkClients.Create(storage.Settings, download: true);
        var launcher = CreateLauncher(instance, client, selectedJava?.ExecutablePath);
        using var installer = (GameFileInstaller)launcher.GameInstaller;
        var version = await PrepareAsync(instance, launcher, selectedJava?.ExecutablePath, progress, cancellationToken);
        var settings = storage.Settings;
        var option = new MLaunchOption
        {
            MaximumRamMb = maximumMemory,
            MinimumRamMb = Math.Min(MemoryAllocation.InitialHeapMb, maximumMemory),
            JavaPath = selectedJava?.ExecutablePath,
            GameLauncherName = "ArysLauncher",
            GameLauncherVersion = App.LauncherVersion,
            ClientId = session.Account.Kind == AccountKind.Microsoft ? session.Account.MicrosoftClientId : null,
            Session = new MSession
            {
                Username = session.Account.PlayerName,
                UUID = session.Account.PlayerUuid,
                AccessToken = session.Tokens.AccessToken,
                ClientToken = session.Tokens.ClientToken,
                UserType = session.Account.Kind == AccountKind.Microsoft ? "msa" : "mojang",
                Xuid = session.Tokens.Xuid
            }
        };
        var joinServer = AuthenticationServer.ParseGameAddress(settings.JoinServerAddress);
        if (joinServer is { } server)
        {
            option.ServerIp = server.Host;
            option.ServerPort = server.Port;
        }
        var gameArguments = MArgument.FromCommandLine(settings.GameArguments).Values;
        var argumentNames = gameArguments.Select(argument => argument.Split('=', 2)[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--accessToken", "--uuid", "--username", "--userType", "--clientId", "--xuid", "--gameDir", "--assetsDir", "--version",
            "--server", "--port", "--quickPlayMultiplayer"
        };
        if (reserved.Overlaps(argumentNames))
        {
            throw new InvalidOperationException(LauncherText.Get("ProtectedGameArguments"));
        }
        if (argumentNames.Overlaps(new[] { "--width", "--height", "--fullscreen", "--fullscreenWidth", "--fullscreenHeight" }))
        {
            throw new InvalidOperationException(LauncherText.Get("DisplayArgumentsManagedByGame"));
        }
        option.ExtraJvmArguments = JvmArgumentPolicy.Build(settings);
        option.ExtraGameArguments = gameArguments.Select(argument => new MArgument(argument)).ToArray();
        if (session.Account.Kind == AccountKind.External)
        {
            var injector = await GetAuthlibInjectorAsync(client, progress, cancellationToken);
            option.ExtraJvmArguments = option.ExtraJvmArguments.Concat(new[]
            {
                new MArgument($"-javaagent:{injector}={session.Account.ServerAddress}"),
                new MArgument("-Dauthlibinjector.side=client")
            }).ToArray();
        }
        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(new WorkSnapshot(LauncherText.Get("StartingGame"), LauncherText.Get("PreparingGameProcess"), 0, 0, 0, 0, 0, 0, true));
        if (instance.NeedsFirstLaunchCleanup)
        {
            PackContentPolicy.PrepareFirstLaunch(instance.DirectoryPath);
            instance.NeedsFirstLaunchCleanup = false;
            try
            {
                storage.SaveInstallation(instance);
            }
            catch
            {
                instance.NeedsFirstLaunchCleanup = true;
                throw;
            }
        }
        option.FullScreen = await GameDisplaySettings.InitializeAsync(instance.DirectoryPath, cancellationToken);
        var process = await launcher.BuildProcessAsync(version, option, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.WorkingDirectory = instance.DirectoryPath;
        var logPath = Path.Combine(storage.Logs, $"game-{DateTime.Now:yyyyMMdd-HHmmss}-{instance.Id[..8]}.log");
        return RunningGame.Start(process, logPath, session.Tokens.AccessToken, session.Tokens.RefreshToken);
    }

    private MinecraftLauncher CreateLauncher(GameInstallation instance, HttpClient client, string? selectedJavaPath)
    {
        var path = new MinecraftPath(instance.DirectoryPath);
        path.Runtime = Path.Combine(instance.DirectoryPath, "runtime");
        var parameters = MinecraftLauncherParameters.CreateDefault(path, client);
        parameters.GameInstaller = new GameFileInstaller(storage, client);
        if (!string.IsNullOrWhiteSpace(selectedJavaPath))
        {
            var extractors = parameters.FileExtractors ?? throw new InvalidOperationException(LauncherText.Get("GameInstallerUnavailable"));
            foreach (var extractor in extractors.Where(extractor => extractor is JavaFileExtractor or LegacyJavaFileExtractor).ToArray())
            {
                extractors.Remove(extractor);
            }
        }
        return new MinecraftLauncher(parameters);
    }

    private async Task<string> PrepareAsync(GameInstallation instance, MinecraftLauncher launcher,
        string? selectedJavaPath, IProgress<WorkSnapshot> progress, CancellationToken cancellationToken)
    {
        var gameInstaller = (GameFileInstaller)launcher.GameInstaller;
        var version = instance.LaunchVersion;
        if (string.IsNullOrWhiteSpace(version))
        {
            version = instance.MinecraftVersion;
            if (!string.IsNullOrWhiteSpace(instance.ForgeVersion))
            {
                var vanillaProgress = new GameInstallProgress(progress, gameInstaller,
                    LauncherText.Get("InstallingMinecraft"), LauncherText.Get("CheckingMinecraftFiles"));
                var vanilla = await launcher.GetVersionAsync(instance.MinecraftVersion, cancellationToken);
                await launcher.InstallAsync(vanilla, vanillaProgress.Files, vanillaProgress.Bytes, cancellationToken);
                var javaPath = selectedJavaPath ?? launcher.GetJavaPath(vanilla) ?? launcher.GetDefaultJavaPath();
                if (string.IsNullOrEmpty(javaPath) || !File.Exists(javaPath))
                {
                    throw new InvalidOperationException(LauncherText.Get("JavaRuntimeMissing"));
                }
                version = $"{instance.MinecraftVersion}-forge-{instance.ForgeVersion}";
                var artifactCoordinate = $"{instance.MinecraftVersion}-{instance.ForgeVersion}";
                var artifact = new ForgeV12VersionArtifact(instance.MinecraftVersion, instance.ForgeVersion, version,
                    $"https://maven.minecraftforge.net/net/minecraftforge/forge/{artifactCoordinate}/forge-{artifactCoordinate}-installer.jar",
                    $"maven/net/minecraftforge/forge/{artifactCoordinate}/forge-{artifactCoordinate}.jar");
                var forgeProgress = new GameInstallProgress(progress, gameInstaller,
                    LauncherText.Get("InstallingForge"), LauncherText.Get("PreparingForge"), hasSingleBatch: false);
                var installer = new ForgeV12Installer(artifact);
                await installer.Install(launcher.MinecraftPath, launcher.GameInstaller, new ForgeInstallOptions
                {
                    JavaPath = javaPath,
                    ByteProgress = forgeProgress.Bytes,
                    CancellationToken = cancellationToken
                });
                await launcher.GetAllVersionsAsync(cancellationToken);
            }
        }
        var resourceProgress = new GameInstallProgress(progress, gameInstaller,
            LauncherText.Get("VerifyingGameResources"), LauncherText.Get("CheckingGameResources"));
        await launcher.InstallAsync(version, resourceProgress.Files, resourceProgress.Bytes, cancellationToken);
        instance.LaunchVersion = version;
        return version;
    }

    private async Task<string> GetAuthlibInjectorAsync(HttpClient client, IProgress<WorkSnapshot> progress,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(storage.Settings.AuthlibInjectorPath))
        {
            if (!File.Exists(storage.Settings.AuthlibInjectorPath))
            {
                throw new FileNotFoundException(LauncherText.Get("AuthlibJarMissing"));
            }
            return Path.GetFullPath(storage.Settings.AuthlibInjectorPath);
        }
        var metadataPath = Path.Combine(storage.Components, "authlib-injector.json");
        ContentRequest request;
        if (File.Exists(metadataPath))
        {
            var artifact = JsonSerializer.Deserialize<AuthlibArtifact>(await File.ReadAllTextAsync(metadataPath, cancellationToken),
                LauncherStorage.JsonOptions) ?? throw new InvalidDataException(LauncherText.Get("AuthlibCacheInvalid"));
            request = new ContentRequest(artifact.Size, artifact.Sha256, HashAlgorithmName.SHA256, new[] { artifact.Source });
        }
        else
        {
            progress.Report(new WorkSnapshot(LauncherText.Get("PreparingExternalAuth"), LauncherText.Get("DownloadingAuthlib"), 0, 0, 0, 0, 0, 0, true));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            using var metadataResponse = await client.GetAsync("https://authlib-injector.yushi.moe/artifact/latest.json",
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            metadataResponse.EnsureSuccessStatusCode();
            await metadataResponse.Content.LoadIntoBufferAsync(1024 * 1024, timeout.Token);
            using var metadata = await JsonDocument.ParseAsync(await metadataResponse.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            var source = new Uri(metadata.RootElement.GetProperty("download_url").GetString()!);
            if (source.Scheme != "https" || source.Host != "authlib-injector.yushi.moe")
            {
                throw new InvalidDataException(LauncherText.Get("AuthlibAddressUnexpected"));
            }
            var sha256 = metadata.RootElement.GetProperty("checksums").GetProperty("sha256").GetString()!;
            using var head = new HttpRequestMessage(HttpMethod.Head, source);
            using var headResponse = await client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            headResponse.EnsureSuccessStatusCode();
            var size = headResponse.Content.Headers.ContentLength ?? throw new IOException(LauncherText.Get("AuthlibSizeMissing"));
            request = new ContentRequest(size, sha256, HashAlgorithmName.SHA256, new[] { source });
        }
        using var downloader = new ContentDownloader(storage, client);
        var reporter = new WorkProgress(progress, 1, request.Size);
        reporter.Stage(LauncherText.Get("PreparingExternalAuth"), LauncherText.Get("VerifyingAuthlib"));
        var cached = await downloader.FetchAsync(request, 0, reporter, cancellationToken);
        reporter.FileDone(0);
        if (!File.Exists(metadataPath))
        {
            var artifact = new AuthlibArtifact(request.Size, request.Hash, request.Sources[0]);
            var temporary = metadataPath + ".new";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(artifact, LauncherStorage.JsonOptions), cancellationToken);
            File.Move(temporary, metadataPath, true);
        }
        return cached;
    }
}

public sealed record AuthlibArtifact(long Size, string Sha256, Uri Source);

public sealed class RunningGame
{
    public Process Process { get; }
    public string LogPath { get; }
    public Task<int> Completion { get; }

    private RunningGame(Process process, string logPath, Task<int> completion)
    {
        Process = process;
        LogPath = logPath;
        Completion = completion;
    }

    public static RunningGame Start(Process process, string logPath, params string[] secrets)
    {
        var lines = Channel.CreateBounded<string>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        var redactions = secrets.Where(secret => !string.IsNullOrEmpty(secret)).Distinct().ToArray();
        void Collect(object sender, DataReceivedEventArgs args)
        {
            if (args.Data == null)
            {
                return;
            }
            var line = args.Data;
            foreach (var secret in redactions)
            {
                line = line.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            }
            lines.Writer.TryWrite(line.Length > 32768 ? line[..32768] : line);
        }
        process.OutputDataReceived += Collect;
        process.ErrorDataReceived += Collect;
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException(LauncherText.Get("GameStartFailed"));
            }
        }
        catch
        {
            process.Dispose();
            throw;
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        async Task<int> MonitorAsync()
        {
            var writerTask = Task.Run(async () =>
            {
                await using var writer = new StreamWriter(logPath, false);
                long characters = 0;
                await foreach (var line in lines.Reader.ReadAllAsync())
                {
                    if (characters < 32 * 1024 * 1024)
                    {
                        await writer.WriteLineAsync(line);
                        characters += line.Length;
                    }
                }
            });
            try
            {
                await process.WaitForExitAsync();
                lines.Writer.TryComplete();
                await writerTask;
                return process.ExitCode;
            }
            finally
            {
                lines.Writer.TryComplete();
                process.OutputDataReceived -= Collect;
                process.ErrorDataReceived -= Collect;
                process.Dispose();
            }
        }
        return new RunningGame(process, logPath, MonitorAsync());
    }
}
