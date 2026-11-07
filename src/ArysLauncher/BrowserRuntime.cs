using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Web.WebView2.Core;

namespace ArysLauncher;

internal static class BrowserRuntime
{
    private const string Version = "156.0.4314.58";
    private const long ArchiveSize = 310195262;
    private const string ArchiveHash = "857DCFD6F7035508A29645FA2978C3A6A841F220DF77CC366D318894E19F4792";

    public static async Task<string?> ResolveAsync(LauncherStorage storage, Action<string> report, CancellationToken token)
    {
        try
        {
            CoreWebView2Environment.GetAvailableBrowserVersionString();
            return null;
        }
        catch (WebView2RuntimeNotFoundException)
        {
        }
        var legacy = Path.Combine(AppContext.BaseDirectory, "WebView2Runtime");
        if (File.Exists(Path.Combine(legacy, "msedgewebview2.exe")))
        {
            return legacy;
        }
        var components = Path.Combine(storage.Components, "webview2");
        var installed = Path.Combine(components, Version);
        var expected = new ManagedFile { Size = ArchiveSize, Sha256 = ArchiveHash };
        if (File.Exists(Path.Combine(installed, "runtime.ready")) &&
            File.ReadAllText(Path.Combine(installed, "runtime.ready")) == ArchiveHash &&
            File.Exists(Path.Combine(installed, "msedgewebview2.exe")))
        {
            return installed;
        }
        var archive = Path.Combine(AppContext.BaseDirectory, "BrowserRuntime.cab");
        if (!await UpdatePaths.MatchesAsync(archive, expected, token))
        {
            archive = Path.Combine(components, Version + ".cab");
            if (!await UpdatePaths.MatchesAsync(archive, expected, token))
            {
                report(LauncherText.Get("DownloadingSignInComponent"));
                using var client = NetworkClients.Create(storage.Settings);
                using var downloader = new ContentDownloader(storage, client);
                var progress = new WorkProgress(new CallbackProgress<WorkSnapshot>(snapshot =>
                {
                    var percentage = (int)Math.Clamp(100.0 * snapshot.CompletedBytes / ArchiveSize, 0, 100);
                    report(LauncherText.Get("DownloadingSignInComponentProgress", percentage));
                }), 1, ArchiveSize);
                var source = new Uri("https://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/b7919cc9-680d-4253-bf32-732988e68aa6/Microsoft.WebView2.FixedVersionRuntime.156.0.4314.58.x64.cab");
                var cached = await downloader.FetchAsync(new ContentRequest(ArchiveSize, ArchiveHash,
                    HashAlgorithmName.SHA256, new[] { source }), 0, progress, token);
                Directory.CreateDirectory(components);
                File.Move(cached, archive, true);
            }
        }
        report(LauncherText.Get("PreparingSignInComponent"));
        var stagingRoot = Path.Combine(components, "staging");
        var staging = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "expand.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[] { archive, "-F:*", staging })
            {
                info.ArgumentList.Add(argument);
            }
            using var process = Process.Start(info) ?? throw new IOException(LauncherText.Get("OfflineBrowserExtractionFailed"));
            var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, token);
            var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromMinutes(3));
                await process.WaitForExitAsync(timeout.Token);
                await Task.WhenAll(output, error);
            }
            catch
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    await process.WaitForExitAsync();
                }
                throw;
            }
            var runtime = Path.Combine(staging, $"Microsoft.WebView2.FixedVersionRuntime.{Version}.x64");
            if (process.ExitCode != 0 || !File.Exists(Path.Combine(runtime, "msedgewebview2.exe")))
            {
                throw new IOException(LauncherText.Get("OfflineBrowserExtractionIncomplete"));
            }
            File.WriteAllText(Path.Combine(runtime, "runtime.ready"), ArchiveHash);
            if (Directory.Exists(installed))
            {
                LauncherStorage.RemoveOwnedStaging(components, installed);
            }
            Directory.Move(runtime, installed);
            try
            {
                File.Delete(archive);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
            return installed;
        }
        finally
        {
            LauncherStorage.RemoveOwnedStaging(stagingRoot, staging);
        }
    }
}
