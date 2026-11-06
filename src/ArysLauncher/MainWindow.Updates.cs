using System.IO;
using System.Windows;

namespace ArysLauncher;

public partial class MainWindow
{
    private LauncherUpdates? updates;
    private UpdateManifest? launcherRelease;
    private UpdateManifest? packRelease;
    private bool checkingUpdates;

    private async Task CheckUpdatesAsync(UpdateKind? requestedKind = null, bool applyPack = false)
    {
        if (checkingUpdates || IsBusy || closed)
        {
            return;
        }
        checkingUpdates = true;
        if (requestedKind != UpdateKind.Launcher)
        {
            packRelease = null;
        }
        RefreshUpdateButtons();
        try
        {
            updates = new LauncherUpdates(storage);
            var kinds = requestedKind.HasValue ? new[] { requestedKind.Value } : Enum.GetValues<UpdateKind>();
            foreach (var kind in kinds)
            {
                var status = kind == UpdateKind.Launcher ? LauncherUpdateStatus : PackUpdateStatus;
                status.Text = kind == UpdateKind.Launcher ? LauncherText.Get("CheckingLauncherVersion") : LauncherText.Get("ComparingPackFiles");
                if (kind == UpdateKind.Pack)
                {
                    InstallationUpdateStatus.Text = status.Text;
                }
                try
                {
                    var token = windowLifetime.Token;
                    var release = await Task.Run(() => updates.CheckAsync(kind, token), token);
                    if (kind == UpdateKind.Launcher)
                    {
                        launcherRelease = release;
                    }
                    else
                    {
                        packRelease = release;
                    }
                    var source = updates.Channel.ManifestAddress(kind);
                    status.Text = source == null ? LauncherText.Get("UpdateSourceUnconfigured") : release == null ?
                        kind == UpdateKind.Pack && storage.Installation == null ? LauncherText.Get("InstallBeforeChecking") : LauncherText.Get("UpToDate") :
                        LauncherText.Get("UpdateAvailable", release.Version);
                    status.ToolTip = release?.ReleaseNotes;
                    if (kind == UpdateKind.Pack)
                    {
                        InstallationUpdateStatus.Text = status.Text;
                        InstallationUpdateStatus.ToolTip = status.ToolTip;
                    }
                }
                catch (OperationCanceledException) when (windowLifetime.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException or
                    System.Security.Cryptography.CryptographicException or OperationCanceledException or System.Text.Json.JsonException)
                {
                    if (kind == UpdateKind.Launcher)
                    {
                        launcherRelease = null;
                    }
                    else
                    {
                        packRelease = null;
                    }
                    status.Text = LauncherText.Get("UpdateCheckFailed");
                    status.ToolTip = exception.Message;
                    if (kind == UpdateKind.Pack)
                    {
                        InstallationUpdateStatus.Text = status.Text;
                        InstallationUpdateStatus.ToolTip = exception.Message;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
        {
            LauncherUpdateStatus.Text = PackUpdateStatus.Text = exception.Message;
            InstallationUpdateStatus.Text = exception.Message;
        }
        finally
        {
            checkingUpdates = false;
            RefreshUpdateButtons();
        }
        if (applyPack && packRelease != null && !closed)
        {
            await ApplyUpdateAsync(UpdateKind.Pack, packRelease);
        }
    }

    private void RefreshUpdateButtons()
    {
        if (!initialized)
        {
            return;
        }
        var available = !IsBusy && !checkingUpdates && runningGame == null;
        CheckUpdatesButton.IsEnabled = !IsBusy && !checkingUpdates;
        ApplyLauncherUpdateButton.IsEnabled = available && launcherRelease != null;
        ApplyPackUpdateButton.IsEnabled = available && packRelease != null && storage.Installation != null;
        CheckPackUpdatesButton.IsEnabled = available && storage.Installation != null;
        CheckPackUpdatesButton.Content = checkingUpdates ? LauncherText.Get("CheckingUpdates") : LauncherText.Get("CheckForUpdates");
        ClearCacheButton.IsEnabled = !IsBusy;
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        await CheckUpdatesAsync();
    }

    private async void CheckPackUpdates_Click(object sender, RoutedEventArgs e)
    {
        await CheckUpdatesAsync(UpdateKind.Pack, applyPack: true);
    }

    private async void ApplyLauncherUpdate_Click(object sender, RoutedEventArgs e)
    {
        await ApplyUpdateAsync(UpdateKind.Launcher);
    }

    private async void ApplyPackUpdate_Click(object sender, RoutedEventArgs e)
    {
        await ApplyUpdateAsync(UpdateKind.Pack);
    }

    private async Task ApplyUpdateAsync(UpdateKind kind, UpdateManifest? checkedRelease = null)
    {
        if (IsBusy || checkingUpdates || runningGame != null || updates == null)
        {
            return;
        }
        var completed = false;
        await RunOperationAsync(kind == UpdateKind.Launcher ? LauncherText.Get("UpdateLauncher") : LauncherText.Get("UpdateModpack"), async (token, progress) =>
        {
            var release = checkedRelease ?? await Task.Run(() => updates.CheckAsync(kind, token), token) ??
                throw new InvalidOperationException(LauncherText.Get("UpdateStatusChanged"));
            var transaction = await Task.Run(() => updates.PrepareAsync(release, progress, token), token);
            if (kind == UpdateKind.Launcher)
            {
                try
                {
                    updates.StartLauncherReplacement(transaction);
                    closeAfterOperation = true;
                }
                catch
                {
                    transaction.Cleanup();
                    throw;
                }
            }
            else
            {
                progress.Report(new WorkSnapshot(LauncherText.Get("ApplyingUpdate"), LauncherText.Get("ReplacingPackFiles"), 0, 0, 0, 0, 0, 0, true));
                await Task.Run(() => transaction.ApplyAsync(token), CancellationToken.None);
                packRelease = null;
                RefreshPresentation();
            }
            completed = true;
        });
        if (completed && !closeAfterOperation)
        {
            await CheckUpdatesAsync();
        }
    }

    private async void ClearDownloadCache_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(LauncherText.Get("ClearDownloadCache"), (token, _) => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            DownloadCache.Clear(storage);
        }, token), showTasks: false);
    }
}
