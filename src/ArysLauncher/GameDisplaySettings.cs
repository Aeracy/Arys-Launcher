using System.IO;
using System.Text;

namespace ArysLauncher;

internal static class GameDisplaySettings
{
    public static async Task<bool> InitializeAsync(string gameDirectory, CancellationToken cancellationToken)
    {
        var path = UpdatePaths.Target(gameDirectory, "options.txt");
        if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
        {
            throw new IOException(LauncherText.Get("GameOptionsTooLarge"));
        }
        var lines = File.Exists(path) ? await File.ReadAllLinesAsync(path, cancellationToken) : Array.Empty<string>();
        if (lines.Any(line => line.StartsWith("fullscreen:", StringComparison.Ordinal)))
        {
            return false;
        }
        var options = lines.Where(line => !line.StartsWith("fullscreenResolution:", StringComparison.Ordinal)).ToList();
        options.Add("fullscreen:true");
        var temporary = UpdatePaths.Target(gameDirectory, "options.arys-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllLinesAsync(temporary, options, new UTF8Encoding(false), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
            return true;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
