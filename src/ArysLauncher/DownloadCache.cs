using System.IO;

namespace ArysLauncher;

internal static class DownloadCache
{
    private const long MaximumBytes = 1024L * 1024 * 1024;

    public static void Trim(LauncherStorage storage)
    {
        var root = UpdatePaths.Target(storage.Root, "cache");
        var objects = Path.Combine(root, "objects");
        if (!Directory.Exists(objects))
        {
            return;
        }
        var files = new DirectoryInfo(objects).EnumerateFiles("*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true
        }).OrderBy(file => file.LastWriteTimeUtc).ToArray();
        var total = files.Sum(file => file.Length);
        foreach (var file in files)
        {
            if (total <= MaximumBytes)
            {
                break;
            }
            try
            {
                var size = file.Length;
                file.Delete();
                total -= size;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public static void Clear(LauncherStorage storage)
    {
        var root = UpdatePaths.Target(storage.Root, "cache");
        LauncherStorage.RemoveOwnedStaging(storage.Root, root);
        Directory.CreateDirectory(root);
    }
}
