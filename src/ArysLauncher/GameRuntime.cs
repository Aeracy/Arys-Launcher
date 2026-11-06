using System.Diagnostics;
using System.Globalization;
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
            throw new InvalidOperationException("当前选中的 Java 为 32 位，无法使用设定的较大内存。请在设置中改选 64 位 Java，推荐 Java 17。");
        }
        if (instance.ModCount > 0 && string.IsNullOrWhiteSpace(instance.ForgeVersion) && string.IsNullOrWhiteSpace(instance.LaunchVersion))
        {
            throw new InvalidOperationException("整合包包含 Mod，但尚未指定加载器。请在整合包配置中填写要求的 Forge 版本。");
        }
        if (!Directory.Exists(instance.DirectoryPath))
        {
            throw new DirectoryNotFoundException("整合包的游戏目录不存在，请检查安装位置。");
        }
        using var accounts = new AccountService(storage);
        var session = await accounts.GetSessionAsync(account,
            message => progress.Report(new WorkSnapshot("正在登录", message, 0, 0, 0, 0, 0, 0, true)), cancellationToken);
        using var client = NetworkClients.Create(storage.Settings, download: true);
        var launcher = CreateLauncher(instance, client, selectedJava?.ExecutablePath);
        using var installer = (GameFileInstaller)launcher.GameInstaller;
        var version = await PrepareAsync(instance, launcher, selectedJava?.ExecutablePath, progress, cancellationToken);
        var settings = storage.Settings;
        var option = new MLaunchOption
        {
            MaximumRamMb = maximumMemory,
            MinimumRamMb = Math.Min(MemoryAllocation.InitialHeapMb, maximumMemory),
            ScreenWidth = settings.WindowWidth,
            ScreenHeight = settings.WindowHeight,
            FullScreen = settings.Fullscreen,
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
            throw new InvalidOperationException("附加游戏参数不能覆盖账号身份、版本或游戏目录。");
        }
        option.ExtraJvmArguments = JvmArgumentPolicy.Build(settings);
        var extraArguments = gameArguments.Select(argument => new MArgument(argument)).ToList();
        if (settings.Fullscreen && instance.MinecraftVersion == "1.20.1")
        {
            if (!argumentNames.Contains("--fullscreenWidth"))
            {
                extraArguments.Add(new MArgument(new[] { "--fullscreenWidth", settings.WindowWidth.ToString(CultureInfo.InvariantCulture) }));
            }
            if (!argumentNames.Contains("--fullscreenHeight"))
            {
                extraArguments.Add(new MArgument(new[] { "--fullscreenHeight", settings.WindowHeight.ToString(CultureInfo.InvariantCulture) }));
            }
        }
        option.ExtraGameArguments = extraArguments;
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
        progress.Report(new WorkSnapshot("正在启动", "正在准备游戏进程", 0, 0, 0, 0, 0, 0, true));
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
            var extractors = parameters.FileExtractors ?? throw new InvalidOperationException("游戏文件安装入口未就绪。");
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
                    "正在安装 Minecraft", "检查游戏文件，补全缺失的资源。");
                var vanilla = await launcher.GetVersionAsync(instance.MinecraftVersion, cancellationToken);
                await launcher.InstallAsync(vanilla, vanillaProgress.Files, vanillaProgress.Bytes, cancellationToken);
                var javaPath = selectedJavaPath ?? launcher.GetJavaPath(vanilla) ?? launcher.GetDefaultJavaPath();
                if (string.IsNullOrEmpty(javaPath) || !File.Exists(javaPath))
                {
                    throw new InvalidOperationException("没有找到可用的 Java。请在设置中选择与整合包兼容的 Java，Minecraft 1.20.1 建议使用 64 位 Java 17。");
                }
                version = $"{instance.MinecraftVersion}-forge-{instance.ForgeVersion}";
                var artifactCoordinate = $"{instance.MinecraftVersion}-{instance.ForgeVersion}";
                var artifact = new ForgeV12VersionArtifact(instance.MinecraftVersion, instance.ForgeVersion, version,
                    $"https://maven.minecraftforge.net/net/minecraftforge/forge/{artifactCoordinate}/forge-{artifactCoordinate}-installer.jar",
                    $"maven/net/minecraftforge/forge/{artifactCoordinate}/forge-{artifactCoordinate}.jar");
                var forgeProgress = new GameInstallProgress(progress, gameInstaller,
                    "正在安装 Forge", "获取 Forge 依赖并准备加载器。", hasSingleBatch: false);
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
            "正在校验游戏资源", "复用已校验的文件，补全整合包所需资源。");
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
                throw new FileNotFoundException("指定的 authlib-injector JAR 不存在。");
            }
            return Path.GetFullPath(storage.Settings.AuthlibInjectorPath);
        }
        var metadataPath = Path.Combine(storage.Components, "authlib-injector.json");
        ContentRequest request;
        if (File.Exists(metadataPath))
        {
            var artifact = JsonSerializer.Deserialize<AuthlibArtifact>(await File.ReadAllTextAsync(metadataPath, cancellationToken),
                LauncherStorage.JsonOptions) ?? throw new InvalidDataException("外置认证组件缓存无效，请在设置中指定 authlib-injector 文件。");
            request = new ContentRequest(artifact.Size, artifact.Sha256, HashAlgorithmName.SHA256, new[] { artifact.Source });
        }
        else
        {
            progress.Report(new WorkSnapshot("正在准备外置认证", "正在获取 authlib-injector 组件", 0, 0, 0, 0, 0, 0, true));
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
                throw new InvalidDataException("外置认证组件返回了非预期的下载地址。");
            }
            var sha256 = metadata.RootElement.GetProperty("checksums").GetProperty("sha256").GetString()!;
            using var head = new HttpRequestMessage(HttpMethod.Head, source);
            using var headResponse = await client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            headResponse.EnsureSuccessStatusCode();
            var size = headResponse.Content.Headers.ContentLength ?? throw new IOException("外置认证组件的下载源未提供文件大小，请在设置中指定组件文件。");
            request = new ContentRequest(size, sha256, HashAlgorithmName.SHA256, new[] { source });
        }
        using var downloader = new ContentDownloader(storage, client);
        var reporter = new WorkProgress(progress, 1, request.Size);
        reporter.Stage("正在准备外置认证", "正在校验 authlib-injector 组件");
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
                throw new InvalidOperationException("游戏进程未能启动。");
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
