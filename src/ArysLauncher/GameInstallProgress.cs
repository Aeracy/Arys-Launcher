using System.Diagnostics;
using CmlLib.Core;
using CmlLib.Core.Installers;

namespace ArysLauncher;

internal sealed class GameInstallProgress
{
    private readonly object gate = new();
    private readonly IProgress<WorkSnapshot> progress;
    private readonly GameFileInstaller installer;
    private readonly string stage;
    private readonly string detail;
    private readonly bool hasSingleBatch;
    private ByteProgress bytes;
    private int completedFiles;
    private int totalFiles;
    private long lastReport;

    public IProgress<InstallerProgressChangedEventArgs>? Files { get; }
    public IProgress<ByteProgress> Bytes { get; }

    public GameInstallProgress(IProgress<WorkSnapshot> progress, GameFileInstaller installer,
        string stage, string detail, bool hasSingleBatch = true)
    {
        this.progress = progress;
        this.installer = installer;
        this.stage = stage;
        this.detail = detail;
        this.hasSingleBatch = hasSingleBatch;
        if (hasSingleBatch)
        {
            Files = new CallbackProgress<InstallerProgressChangedEventArgs>(OnFiles);
        }
        Bytes = new CallbackProgress<ByteProgress>(OnBytes);
        Publish();
    }

    private void OnFiles(InstallerProgressChangedEventArgs files)
    {
        lock (gate)
        {
            completedFiles = Math.Max(completedFiles, files.ProgressedTasks);
            totalFiles = Math.Max(totalFiles, files.TotalTasks);
            if (Stopwatch.GetElapsedTime(lastReport).TotalMilliseconds >= 100)
            {
                Publish();
            }
        }
    }

    private void OnBytes(ByteProgress value)
    {
        lock (gate)
        {
            if (value.TotalBytes > 0)
            {
                bytes = value;
            }
            Publish();
        }
    }

    private void Publish()
    {
        lastReport = Stopwatch.GetTimestamp();
        var total = hasSingleBatch ? Math.Max(0, bytes.TotalBytes) : 0;
        progress.Report(new WorkSnapshot(stage, detail, Math.Clamp(bytes.ProgressedBytes, 0, total), total,
            completedFiles, totalFiles, installer.NetworkBytes, 0, total == 0));
    }
}
