using System.Diagnostics;

namespace ArysLauncher;

public sealed class WorkProgress
{
    private readonly IProgress<WorkSnapshot> progress;
    private readonly object reportGate = new();
    private readonly long[] fileProgress;
    private readonly int[] finished;
    private readonly long totalBytes;
    private long completedBytes;
    private long networkBytes;
    private long lastReport;
    private int completedFiles;
    private int cacheHits;
    private string stage = LauncherText.Get("PreparingImport");
    private string detail = LauncherText.Get("ReadingPack");

    public WorkProgress(IProgress<WorkSnapshot> progress, int fileCount, long totalBytes)
    {
        this.progress = progress;
        fileProgress = new long[fileCount];
        finished = new int[fileCount];
        this.totalBytes = totalBytes;
    }

    public void Stage(string value, string description)
    {
        lock (reportGate)
        {
            stage = value;
            detail = description;
            Publish(Stopwatch.GetTimestamp());
        }
    }

    public void FileBytes(int index, long value)
    {
        var previous = Interlocked.Exchange(ref fileProgress[index], value);
        Interlocked.Add(ref completedBytes, value - previous);
        Report();
    }

    public void NetworkBytes(int value)
    {
        Interlocked.Add(ref networkBytes, value);
    }

    public void CacheHit()
    {
        Interlocked.Increment(ref cacheHits);
    }

    public void FileDone(int index)
    {
        if (Interlocked.Exchange(ref finished[index], 1) == 0)
        {
            Interlocked.Increment(ref completedFiles);
        }
        Report();
    }

    public void Report(bool force = false)
    {
        if (!force && Stopwatch.GetElapsedTime(Volatile.Read(ref lastReport)).TotalMilliseconds < 100)
        {
            return;
        }
        lock (reportGate)
        {
            var now = Stopwatch.GetTimestamp();
            if (force || Stopwatch.GetElapsedTime(lastReport, now).TotalMilliseconds >= 100)
            {
                Publish(now);
            }
        }
    }

    private void Publish(long now)
    {
        Volatile.Write(ref lastReport, now);
        progress.Report(new WorkSnapshot(stage, detail, Interlocked.Read(ref completedBytes), totalBytes,
            Volatile.Read(ref completedFiles), fileProgress.Length, Interlocked.Read(ref networkBytes), Volatile.Read(ref cacheHits)));
    }
}
