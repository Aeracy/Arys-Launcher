using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ArysLauncher;

public sealed record JavaRuntime(string ExecutablePath, string Version, int MajorVersion, bool Is64Bit, string Vendor)
{
    public string Label
    {
        get
        {
            return $"Java {Version} · {(Is64Bit ? LauncherText.Get("Java64Bit") : LauncherText.Get("Java32Bit"))} · {Vendor}";
        }
    }
}

public sealed record JavaChoice(JavaRuntime? Runtime)
{
    public string Label
    {
        get
        {
            return Runtime?.Label ?? LauncherText.Get("JavaAutomaticChoice");
        }
    }
}

public sealed class JavaRuntimeCatalog
{
    private readonly LauncherStorage storage;
    private readonly SemaphoreSlim scanGate = new(1, 1);
    private IReadOnlyList<JavaRuntime>? cached;

    public JavaRuntimeCatalog(LauncherStorage storage)
    {
        this.storage = storage;
    }

    public async Task<IReadOnlyList<JavaRuntime>> DiscoverAsync(string explicitPath, bool refresh, CancellationToken cancellationToken)
    {
        await scanGate.WaitAsync(cancellationToken);
        try
        {
            if (!refresh && cached != null &&
                (string.IsNullOrWhiteSpace(explicitPath) || cached.Any(runtime => SameExecutable(runtime.ExecutablePath, explicitPath))))
            {
                return cached;
            }
            var candidates = await Task.Run(() => FindCandidates(explicitPath, cancellationToken), cancellationToken);
            var runtimes = new ConcurrentBag<JavaRuntime>();
            await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
                async (path, token) =>
                {
                    var runtime = await InspectAsync(path, token);
                    if (runtime != null)
                    {
                        runtimes.Add(runtime);
                    }
                });
            cached = runtimes.GroupBy(runtime => Path.GetDirectoryName(runtime.ExecutablePath), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderByDescending(runtime => runtime.MajorVersion == 17 && runtime.Is64Bit)
                .ThenByDescending(runtime => runtime.MajorVersion == 17)
                .ThenByDescending(runtime => runtime.Is64Bit)
                .ThenBy(runtime => Math.Abs(runtime.MajorVersion - 17))
                .ThenByDescending(runtime => runtime.Version, StringComparer.OrdinalIgnoreCase).ToArray();
            return cached;
        }
        finally
        {
            scanGate.Release();
        }
    }

    public async Task<JavaRuntime?> ResolveAsync(LauncherSettings settings, CancellationToken cancellationToken)
    {
        var runtimes = await DiscoverAsync(settings.JavaPath, false, cancellationToken);
        if (settings.AutomaticJavaSelection)
        {
            return runtimes.FirstOrDefault(runtime => File.Exists(runtime.ExecutablePath));
        }
        if (string.IsNullOrWhiteSpace(settings.JavaPath))
        {
            throw new InvalidOperationException(LauncherText.Get("JavaSelectionRequired"));
        }
        return runtimes.FirstOrDefault(runtime => SameExecutable(runtime.ExecutablePath, settings.JavaPath) && File.Exists(runtime.ExecutablePath))
            ?? throw new InvalidOperationException(LauncherText.Get("JavaSelectionUnavailable"));
    }

    public static bool SameExecutable(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return false;
        }
        return string.Equals(Path.GetDirectoryName(Path.GetFullPath(first)), Path.GetDirectoryName(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);
    }

    private HashSet<string> FindCandidates(string explicitPath, CancellationToken cancellationToken)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddExecutable(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }
            try
            {
                if (Directory.Exists(path))
                {
                    foreach (var name in new[] { "javaw.exe", "java.exe" })
                    {
                        var executable = Path.Combine(path, name);
                        if (File.Exists(executable))
                        {
                            var resolved = new FileInfo(executable).ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath(executable);
                            candidates.Add(resolved);
                            return;
                        }
                    }
                }
                else if (File.Exists(path))
                {
                    var resolved = new FileInfo(path).ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath(path);
                    var preferred = Path.Combine(Path.GetDirectoryName(resolved)!, "javaw.exe");
                    candidates.Add(File.Exists(preferred) ? preferred : resolved);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }
        AddExecutable(explicitPath);
        foreach (var variable in new[] { "JAVA_HOME", "JDK_HOME", "JRE_HOME" })
        {
            var home = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(home))
            {
                roots.Add(home);
                AddExecutable(Path.Combine(home, "bin"));
            }
        }
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            AddExecutable(directory.Trim('"'));
        }
        foreach (var programFiles in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Distinct())
        {
            foreach (var vendor in new[] { "Java", "Eclipse Adoptium", "Microsoft", "Zulu", "BellSoft", "Amazon Corretto", "Semeru", "SapMachine", "OpenJDK", "ojdkbuild" })
            {
                roots.Add(Path.Combine(programFiles, vendor));
            }
        }
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var relative in new[] { ".jdks", ".jabba/jdk", ".sdkman/candidates/java", "scoop/apps/temurin17-jdk", "scoop/apps/openjdk17" })
        {
            roots.Add(Path.Combine(profile, relative));
        }
        roots.Add(Path.Combine(storage.GameDirectory, "runtime"));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "runtime"));
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var registry = RegistryKey.OpenBaseKey(hive, view);
                    foreach (var prefix in new[] { "SOFTWARE\\JavaSoft\\JDK", "SOFTWARE\\JavaSoft\\Java Runtime Environment",
                        "SOFTWARE\\JavaSoft\\Java Development Kit", "SOFTWARE\\Eclipse Adoptium\\JDK", "SOFTWARE\\AdoptOpenJDK\\JDK", "SOFTWARE\\Azul Systems\\Zulu" })
                    {
                        using var key = registry.OpenSubKey(prefix);
                        if (key != null)
                        {
                            ReadRegistryHomes(key, roots, 0);
                        }
                    }
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                }
            }
        }
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var queue = new Queue<(string Path, int Depth)>();
            queue.Enqueue((root, 0));
            var visited = 0;
            while (queue.Count > 0 && visited++ < 256)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = queue.Dequeue();
                try
                {
                    if (!Directory.Exists(current.Path))
                    {
                        continue;
                    }
                    AddExecutable(Path.Combine(current.Path, "bin"));
                    if (current.Depth < 4)
                    {
                        foreach (var child in Directory.EnumerateDirectories(current.Path).Take(128))
                        {
                            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                            {
                                queue.Enqueue((child, current.Depth + 1));
                            }
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        return candidates;
    }

    private static void ReadRegistryHomes(RegistryKey key, HashSet<string> roots, int depth)
    {
        foreach (var name in new[] { "JavaHome", "Path", "InstallationPath" })
        {
            if (key.GetValue(name) is string path && Directory.Exists(path))
            {
                roots.Add(path);
            }
        }
        if (depth >= 3)
        {
            return;
        }
        foreach (var name in key.GetSubKeyNames().Take(64))
        {
            using var child = key.OpenSubKey(name);
            if (child != null)
            {
                ReadRegistryHomes(child, roots, depth + 1);
            }
        }
    }

    private static async Task<JavaRuntime?> InspectAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var home = Path.GetDirectoryName(Path.GetDirectoryName(path));
            var releasePath = home == null ? "" : Path.Combine(home, "release");
            if (File.Exists(releasePath) && new FileInfo(releasePath).Length < 128 * 1024)
            {
                var release = await File.ReadAllTextAsync(releasePath, cancellationToken);
                var version = ReadReleaseValue(release, "JAVA_VERSION");
                var architecture = ReadReleaseValue(release, "OS_ARCH");
                var vendor = ReadReleaseValue(release, "IMPLEMENTOR");
                if (version.Length > 0 && architecture.Length > 0)
                {
                    return CreateRuntime(path, version, architecture, vendor);
                }
            }
            var java = Path.Combine(Path.GetDirectoryName(path)!, "java.exe");
            if (!File.Exists(java))
            {
                java = path;
            }
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(java)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                }
            };
            process.StartInfo.ArgumentList.Add("-XshowSettings:properties");
            process.StartInfo.ArgumentList.Add("-version");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }
            var text = await error + "\n" + await output;
            return process.ExitCode == 0 ? CreateRuntime(path,
                Regex.Match(text, @"java\.version\s*=\s*([^\s]+)").Groups[1].Value,
                Regex.Match(text, @"os\.arch\s*=\s*([^\s]+)").Groups[1].Value,
                Regex.Match(text, @"java\.vendor\s*=\s*([^\r\n]+)").Groups[1].Value.Trim()) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static string ReadReleaseValue(string content, string name)
    {
        return Regex.Match(content, "(?m)^" + name + "=\"([^\"]+)\"").Groups[1].Value;
    }

    private static JavaRuntime? CreateRuntime(string path, string version, string architecture, string vendor)
    {
        var match = Regex.Match(version, @"^(?:1\.)?(\d+)");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major))
        {
            return null;
        }
        return new JavaRuntime(path, version, major, architecture.Contains("64", StringComparison.OrdinalIgnoreCase),
            string.IsNullOrEmpty(vendor) ? LauncherText.Get("JavaLocalRuntime") : vendor);
    }
}
