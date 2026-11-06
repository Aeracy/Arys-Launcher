using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace ArysLauncher;

internal static class UpdateBootstrap
{
    public static async Task ApplyAsync(string[] arguments)
    {
        if (arguments.Length != 5 || !int.TryParse(arguments[2], NumberStyles.None, CultureInfo.InvariantCulture, out var parentId) ||
            !long.TryParse(arguments[3], NumberStyles.None, CultureInfo.InvariantCulture, out var parentStart))
        {
            throw new InvalidDataException(LauncherText.Get("UpdaterArgumentsInvalid"));
        }
        var applicationRoot = Path.GetFullPath(arguments[4]);
        var storage = new LauncherStorage(applicationRoot);
        var transaction = FileUpdateTransaction.Load(storage, arguments[1], applicationRoot);
        if (transaction.Journal.Kind != UpdateKind.Launcher ||
            !Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar).Equals(
                Path.Combine(transaction.DirectoryPath, "helper"), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(LauncherText.Get("UpdaterLocationMismatch"));
        }
        var executable = UpdatePaths.Target(applicationRoot, "ArysLauncher.exe");
        try
        {
            using var parent = Process.GetProcessById(parentId);
            if (parent.StartTime.ToUniversalTime().Ticks != parentStart ||
                !string.Equals(parent.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(LauncherText.Get("UpdaterProcessMismatch"));
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await parent.WaitForExitAsync(timeout.Token);
        }
        catch (ArgumentException)
        {
        }
        await transaction.ApplyAsync(CancellationToken.None);
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = applicationRoot };
        using var restarted = Process.Start(info) ?? throw new IOException(LauncherText.Get("UpdateCompleteReopen"));
    }
}
