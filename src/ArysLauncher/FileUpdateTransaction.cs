using System.IO;
using System.Text.Json;

namespace ArysLauncher;

internal sealed class PendingFileChange
{
    public string Path { get; set; } = "";
    public ManagedFile? Original { get; set; }
    public ManagedFile? Replacement { get; set; }
}

internal sealed class UpdateJournal
{
    public UpdateKind Kind { get; set; }
    public string TargetRoot { get; set; } = "";
    public string State { get; set; } = "prepared";
    public UpdateManifest Release { get; set; } = new();
    public InstalledFiles Installed { get; set; } = new();
    public List<PendingFileChange> Changes { get; set; } = new();
}

internal sealed class FileUpdateTransaction
{
    private readonly LauncherStorage storage;

    public string DirectoryPath { get; }
    public UpdateJournal Journal { get; }

    private FileUpdateTransaction(LauncherStorage storage, string directoryPath, UpdateJournal journal)
    {
        this.storage = storage;
        DirectoryPath = directoryPath;
        Journal = journal;
    }

    public static FileUpdateTransaction Create(LauncherStorage storage, UpdateJournal journal)
    {
        var directory = Path.Combine(storage.Updates, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var transaction = new FileUpdateTransaction(storage, directory, journal);
        transaction.Save();
        return transaction;
    }

    public static FileUpdateTransaction Load(LauncherStorage storage, string directory, string launcherRoot)
    {
        var fullPath = Path.GetFullPath(directory);
        var updatesRoot = Path.GetFullPath(storage.Updates) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(updatesRoot, StringComparison.OrdinalIgnoreCase) ||
            Path.GetDirectoryName(fullPath) != Path.GetFullPath(storage.Updates) ||
            !Guid.TryParseExact(Path.GetFileName(fullPath), "N", out _))
        {
            throw new IOException("更新工作目录无效。");
        }
        UpdatePaths.Target(fullPath, "journal.json");
        var journalPath = Path.Combine(fullPath, "journal.json");
        if (new FileInfo(journalPath).Length > 8 * 1024 * 1024)
        {
            throw new InvalidDataException("更新恢复记录过大。");
        }
        var journal = JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(journalPath), LauncherStorage.JsonOptions)
            ?? throw new InvalidDataException("更新恢复记录为空。");
        var expectedRoot = journal.Kind == UpdateKind.Launcher ? launcherRoot : storage.GameDirectory;
        if (!Path.GetFullPath(journal.TargetRoot).Equals(Path.GetFullPath(expectedRoot), StringComparison.OrdinalIgnoreCase) ||
            journal.State is not ("prepared" or "applying" or "committed") || journal.Changes.Count > 30000)
        {
            throw new InvalidDataException("更新恢复记录与安装位置不符。");
        }
        journal.Release.Validate(journal.Kind);
        foreach (var change in journal.Changes)
        {
            UpdatePaths.ValidateFile(new ManagedFile { Path = change.Path, Size = 0, Sha256 = new string('0', 64) }, journal.Kind);
            if (change.Original == null && change.Replacement == null)
            {
                throw new InvalidDataException("更新恢复记录缺少文件信息。");
            }
            foreach (var file in new[] { change.Original, change.Replacement }.OfType<ManagedFile>())
            {
                UpdatePaths.ValidateFile(file, journal.Kind);
                if (!file.Path.Equals(change.Path, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("更新恢复记录存在不同的文件路径。");
                }
            }
        }
        return new FileUpdateTransaction(storage, fullPath, journal);
    }

    public string StagedPath(string path)
    {
        return UpdatePaths.Target(DirectoryPath, "new/" + path);
    }

    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        foreach (var change in Journal.Changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = UpdatePaths.Target(Journal.TargetRoot, change.Path);
            if (change.Original == null ? File.Exists(target) : !await UpdatePaths.MatchesAsync(target, change.Original, cancellationToken))
            {
                throw new IOException($"准备更新时文件发生了变化，请重新检查。{change.Path}");
            }
            if (change.Replacement != null && !await UpdatePaths.MatchesAsync(StagedPath(change.Path), change.Replacement, cancellationToken))
            {
                throw new IOException($"更新文件校验失败。{change.Path}");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        Journal.State = "applying";
        Save();
        try
        {
            foreach (var change in Journal.Changes)
            {
                var target = UpdatePaths.Target(Journal.TargetRoot, change.Path);
                if (change.Original != null)
                {
                    var backup = BackupPath(change.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Move(target, backup);
                }
                if (change.Replacement != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(StagedPath(change.Path), target);
                }
            }
            Journal.State = "committed";
            Save();
        }
        catch
        {
            await RollbackAsync();
            throw;
        }
        CommitMetadata();
        Cleanup();
    }

    public async Task RecoverAsync()
    {
        if (Journal.State == "committed")
        {
            CommitMetadata();
        }
        else if (Journal.State == "applying")
        {
            await RollbackAsync();
        }
        Cleanup();
    }

    public void Cleanup()
    {
        try
        {
            var helper = Path.Combine(DirectoryPath, "helper", "ArysLauncher.exe");
            if (File.Exists(helper))
            {
                using var access = new FileStream(helper, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            LauncherStorage.RemoveOwnedStaging(storage.Updates, DirectoryPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task RollbackAsync()
    {
        foreach (var change in Journal.Changes.AsEnumerable().Reverse())
        {
            var target = UpdatePaths.Target(Journal.TargetRoot, change.Path);
            var backup = BackupPath(change.Path);
            if (File.Exists(backup))
            {
                if (change.Original == null || !await UpdatePaths.MatchesAsync(backup, change.Original, CancellationToken.None))
                {
                    throw new IOException($"更新备份校验失败。{change.Path}");
                }
                if (File.Exists(target))
                {
                    if (change.Replacement == null || !await UpdatePaths.MatchesAsync(target, change.Replacement, CancellationToken.None))
                    {
                        throw new IOException($"恢复更新时遇到已修改的文件。{change.Path}");
                    }
                    File.Delete(target);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(backup, target);
            }
            else if (change.Original == null && change.Replacement != null && File.Exists(target))
            {
                if (!await UpdatePaths.MatchesAsync(target, change.Replacement, CancellationToken.None))
                {
                    throw new IOException($"恢复更新时遇到已修改的文件。{change.Path}");
                }
                File.Delete(target);
            }
        }
        Journal.State = "prepared";
        Save();
    }

    private string BackupPath(string path)
    {
        return UpdatePaths.Target(DirectoryPath, "backup/" + path);
    }

    private void CommitMetadata()
    {
        if (Journal.Kind == UpdateKind.Pack)
        {
            var instance = storage.Installation ?? throw new InvalidDataException("整合包安装记录不存在。");
            var release = Journal.Release.Pack!;
            if (instance.MinecraftVersion != release.MinecraftVersion || instance.ForgeVersion != release.ForgeVersion)
            {
                instance.LaunchVersion = "";
            }
            instance.PackVersion = Journal.Release.Version;
            instance.Name = release.Name;
            instance.MinecraftVersion = release.MinecraftVersion;
            instance.ForgeVersion = release.ForgeVersion;
            instance.ModCount = Directory.Exists(Path.Combine(storage.GameDirectory, "mods")) ?
                Directory.EnumerateFiles(Path.Combine(storage.GameDirectory, "mods"), "*.jar").Count() : 0;
            storage.SaveInstallation(instance);
        }
        storage.Write(StateFile(Journal.Kind), Journal.Installed);
    }

    internal static string StateFile(UpdateKind kind)
    {
        return kind == UpdateKind.Launcher ? "launcher-files.json" : "pack-files.json";
    }

    private void Save()
    {
        var path = Path.Combine(DirectoryPath, "journal.json");
        using (var stream = new FileStream(path + ".new", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, Journal, LauncherStorage.JsonOptions);
            stream.Flush(true);
        }
        File.Move(path + ".new", path, true);
    }
}
