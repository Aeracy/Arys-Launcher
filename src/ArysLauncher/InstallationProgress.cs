namespace ArysLauncher;

internal sealed class InstallationProgress
{
    private readonly IProgress<WorkSnapshot> target;
    private readonly object gate = new();
    private double overall;
    private long networkBytes;

    public InstallationProgress(IProgress<WorkSnapshot> target)
    {
        this.target = target;
    }

    public IProgress<WorkSnapshot> Phase(double start, double end)
    {
        var networkOffset = networkBytes;
        return new CallbackProgress<WorkSnapshot>(snapshot =>
        {
            lock (gate)
            {
                var fraction = snapshot.OverallProgress ?? (snapshot.TotalBytes > 0 ?
                    (double)snapshot.CompletedBytes / snapshot.TotalBytes : snapshot.TotalFiles > 0 ?
                    (double)snapshot.CompletedFiles / snapshot.TotalFiles : 0);
                overall = Math.Max(overall, start + Math.Clamp(fraction, 0, 1) * (end - start));
                networkBytes = Math.Max(networkBytes, networkOffset + snapshot.NetworkBytes);
                target.Report(snapshot with { OverallProgress = overall, NetworkBytes = networkBytes, Indeterminate = false });
            }
        });
    }
}
