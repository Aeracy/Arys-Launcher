namespace ArysLauncher;

public static class DownloadPolicy
{
    public static int FileConcurrency(LauncherSettings settings)
    {
        return settings.AutomaticDownloadConnections ? 8 : settings.DownloadConcurrency;
    }

    public static int HostConnections(LauncherSettings settings)
    {
        return Math.Min(FileConcurrency(settings), settings.AutomaticDownloadConnections ? 4 : settings.PerHostConnections);
    }
}
