using System.IO;
using System.Windows;

namespace ArysLauncher;

public partial class MainWindow
{
    private LauncherUpdates? updates;
    private UpdateManifest? launcherRelease;
    private UpdateManifest? packRelease;
    private bool checkingUpdates;

    private async Task CheckUpdatesAsync()
    {
        if (checkingUpdates || IsBusy || closed)
        {
            return;
        }
        checkingUpdates = true;
        RefreshUpdateButtons();
        try
        {
            updates = new LauncherUpdates(storage);
            LauncherUpdateStatus.Text = "正在检查启动器版本。";
            PackUpdateStatus.Text = "正在检查整合包版本。";
            foreach (var kind in Enum.GetValues<UpdateKind>())
            {
                var status = kind == UpdateKind.Launcher ? LauncherUpdateStatus : PackUpdateStatus;
                try
                {
                    var release = await updates.CheckAsync(kind, windowLifetime.Token);
                    if (kind == UpdateKind.Launcher)
                    {
                        launcherRelease = release;
                    }
                    else
                    {
                        packRelease = release;
                    }
                    var source = updates.Channel.ManifestAddress(kind);
                    status.Text = source == null ? "发行者尚未配置更新来源。" : release == null ?
                        kind == UpdateKind.Pack && storage.Installation == null ? "导入整合包后可检查更新。" : "当前已是最新版本。" :
                        $"发现 {release.Version}，可以更新。";
                    status.ToolTip = release?.ReleaseNotes;
                }
                catch (OperationCanceledException) when (windowLifetime.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception) when (exception is IOException or System.Net.Http.HttpRequestException or
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
                    status.Text = "暂时无法检查更新，可稍后重试。";
                    status.ToolTip = exception.Message;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
        {
            LauncherUpdateStatus.Text = PackUpdateStatus.Text = exception.Message;
        }
        finally
        {
            checkingUpdates = false;
            RefreshUpdateButtons();
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
        ClearCacheButton.IsEnabled = !IsBusy;
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        await CheckUpdatesAsync();
    }

    private async void ApplyLauncherUpdate_Click(object sender, RoutedEventArgs e)
    {
        await ApplyUpdateAsync(UpdateKind.Launcher);
    }

    private async void ApplyPackUpdate_Click(object sender, RoutedEventArgs e)
    {
        await ApplyUpdateAsync(UpdateKind.Pack);
    }

    private async Task ApplyUpdateAsync(UpdateKind kind)
    {
        if (IsBusy || checkingUpdates || runningGame != null || updates == null)
        {
            return;
        }
        var completed = false;
        await RunOperationAsync(kind == UpdateKind.Launcher ? "更新启动器" : "更新整合包", async (token, progress) =>
        {
            var release = await updates.CheckAsync(kind, token) ?? throw new InvalidOperationException("更新状态已变化，请重新检查。");
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
                progress.Report(new WorkSnapshot("正在应用更新", "保存当前文件并应用新版本。", 0, 0, 0, 0, 0, 0, true));
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
        await RunOperationAsync("清理下载缓存", (token, _) => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            DownloadCache.Clear(storage);
        }, token), showTasks: false);
    }
}
