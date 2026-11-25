using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ArysLauncher;

public partial class MainWindow
{
    private PlayerProfileStore? playerProfiles;
    private FileSystemWatcher? profileWatcher;
    private DispatcherTimer? profileDebounce;
    private CancellationTokenSource? profileLoading;
    private string profileAccountId = "\0";
    private string profileScopeKey = "";
    private BitmapSource? localProfileAvatar;
    private bool editingProfile;

    private LauncherAccount? ProfileAccount => storage.Accounts.FirstOrDefault(account => account.Id == storage.Settings.SelectedAccountId)
        ?? storage.Accounts.FirstOrDefault();

    private void RefreshPlayerProfile(LauncherAccount? account)
    {
        var enabled = account != null && !editingProfile && authenticationCancellation == null;
        ChooseProfileAvatarButton.IsEnabled = ChooseProfileBannerButton.IsEnabled = enabled;
        ResetProfileAvatarButton.IsEnabled = ResetProfileBannerButton.IsEnabled = enabled;
        ProfilePlayerName.Text = account?.PlayerName ?? LauncherText.Get("SignedOut");
        ProfileAccountKind.Text = account?.Description ?? "";
        var scope = account == null ? "" : PlayerProfileStore.ProfileKey(account);
        if (profileAccountId == (account?.Id ?? "") && profileScopeKey == scope)
        {
            return;
        }
        DisposeProfileWatcher();
        profileAccountId = account?.Id ?? "";
        profileScopeKey = scope;
        localProfileAvatar = null;
        ProfileAvatarImage.Source = ProfileBannerImage.Source = null;
        RefreshAccountPortrait(account);
        if (account == null)
        {
            ProfileSyncStatus.Text = LauncherText.Get("PlayerProfileSignIn");
            return;
        }
        playerProfiles ??= new PlayerProfileStore(storage);
        try
        {
            Directory.CreateDirectory(playerProfiles.DirectoryPath);
        }
        catch (Exception exception) when (IsProfileFailure(exception))
        {
            ProfileSyncStatus.Text = LauncherText.Get("PlayerProfileReadFailed");
            ProfileSyncStatus.ToolTip = exception.Message;
            return;
        }
        profileDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        profileDebounce.Tick += (_, _) =>
        {
            profileDebounce?.Stop();
            BeginProfileLoad(account);
        };
        try
        {
            profileWatcher = new FileSystemWatcher(playerProfiles.DirectoryPath, PlayerProfileStore.ProfileKey(account) + ".json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            FileSystemEventHandler changed = (_, _) => QueueProfileReload();
            profileWatcher.Changed += changed;
            profileWatcher.Created += changed;
            profileWatcher.Deleted += changed;
            profileWatcher.Renamed += (_, _) => QueueProfileReload();
            profileWatcher.Error += (_, _) => QueueProfileReload();
            profileWatcher.EnableRaisingEvents = true;
        }
        catch (Exception exception) when (IsProfileFailure(exception))
        {
            DisposeProfileWatcher();
            ProfileSyncStatus.Text = LauncherText.Get("PlayerProfileReadFailed");
            ProfileSyncStatus.ToolTip = exception.Message;
            return;
        }
        BeginProfileLoad(account);
    }

    private void QueueProfileReload()
    {
        if (!closed)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!closed)
                {
                    profileDebounce?.Stop();
                    profileDebounce?.Start();
                }
            });
        }
    }

    private void BeginProfileLoad(LauncherAccount account)
    {
        profileLoading?.Cancel();
        profileLoading?.Dispose();
        profileLoading = CancellationTokenSource.CreateLinkedTokenSource(windowLifetime.Token);
        _ = LoadPlayerProfileAsync(account, profileLoading.Token);
    }

    private async Task LoadPlayerProfileAsync(LauncherAccount account, CancellationToken token)
    {
        var expectedScope = PlayerProfileStore.ProfileKey(account);
        try
        {
            var result = await Task.Run(async () =>
            {
                await playerProfiles!.EnsureAsync(account, token);
                var document = await playerProfiles.ReadAsync(account, token);
                var avatar = await playerProfiles.ReadPreviewAsync(document.AvatarUrl, token);
                var banner = await playerProfiles.ReadPreviewAsync(document.BannerUrl, token);
                return (document, avatar, banner);
            }, token);
            if (closed || token.IsCancellationRequested || profileAccountId != account.Id || profileScopeKey != expectedScope)
            {
                return;
            }
            localProfileAvatar = result.avatar;
            ProfileAvatarImage.Source = result.avatar;
            ProfileBannerImage.Source = result.banner;
            ProfileSyncStatus.Text = LauncherText.Get("PlayerProfileSyncReady");
            ProfileSyncStatus.ToolTip = null;
            RefreshAccountPortrait(account);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (IsProfileFailure(exception))
        {
            if (!closed && profileAccountId == account.Id && profileScopeKey == expectedScope)
            {
                localProfileAvatar = null;
                ProfileAvatarImage.Source = ProfileBannerImage.Source = null;
                RefreshAccountPortrait(account);
                ProfileSyncStatus.Text = LauncherText.Get("PlayerProfileReadFailed");
                ProfileSyncStatus.ToolTip = exception.Message;
            }
        }
    }

    private async Task EditPlayerProfileAsync(bool avatar, bool reset)
    {
        var account = ProfileAccount;
        if (account == null || editingProfile)
        {
            return;
        }
        var dialog = new OpenFileDialog { Filter = "PNG / JPEG|*.png;*.jpg;*.jpeg", CheckFileExists = true,
            Title = LauncherText.Get(avatar ? "ProfileEditAvatar" : "ProfileEditBanner") };
        if (!reset && dialog.ShowDialog(this) != true)
        {
            return;
        }
        editingProfile = true;
        RefreshPlayerProfile(account);
        try
        {
            ProfileImageSource? image = null;
            PlayerProfileImage? crop = null;
            if (!reset)
            {
                ProfileSyncStatus.Text = LauncherText.Get("ProfileReadingImage");
                image = await PlayerProfileImage.ReadAsync(dialog.FileName, windowLifetime.Token);
                var editor = new ProfileImageEditorWindow(image, avatar) { Owner = this };
                if (editor.ShowDialog() != true)
                {
                    ProfileSyncStatus.Text = LauncherText.Get("PlayerProfileSyncReady");
                    return;
                }
                crop = editor.Selection;
            }
            ProfileSyncStatus.Text = LauncherText.Get("ProfileSavingImage");
            playerProfiles ??= new PlayerProfileStore(storage);
            await playerProfiles.SaveAsync(account, avatar, image, crop, windowLifetime.Token);
            BeginProfileLoad(account);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (IsProfileFailure(exception))
        {
            ProfileSyncStatus.Text = exception.Message;
        }
        finally
        {
            editingProfile = false;
            if (!closed)
            {
                RefreshPlayerProfile(ProfileAccount);
            }
        }
    }

    private static bool IsProfileFailure(Exception exception)
    {
        return exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or
            NotSupportedException or ArgumentException or FormatException or System.Security.Cryptography.CryptographicException;
    }

    private void ProfilePreview_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = ProfilePreviewPanel.ActualWidth;
        if (width <= 0)
        {
            return;
        }
        var height = width / 4;
        if (!double.IsFinite(ProfilePreviewPanel.Height) || Math.Abs(ProfilePreviewPanel.Height - height) > 0.25)
        {
            ProfilePreviewPanel.Height = height;
        }
        var clip = new RectangleGeometry(new Rect(0, 0, width, height), 13, 13);
        clip.Freeze();
        ProfilePreviewPanel.Clip = clip;
    }

    private async void ChooseProfileAvatar_Click(object sender, RoutedEventArgs e)
    {
        await EditPlayerProfileAsync(avatar: true, reset: false);
    }

    private async void ChooseProfileBanner_Click(object sender, RoutedEventArgs e)
    {
        await EditPlayerProfileAsync(avatar: false, reset: false);
    }

    private async void ResetProfileAvatar_Click(object sender, RoutedEventArgs e)
    {
        await EditPlayerProfileAsync(avatar: true, reset: true);
    }

    private async void ResetProfileBanner_Click(object sender, RoutedEventArgs e)
    {
        await EditPlayerProfileAsync(avatar: false, reset: true);
    }

    private void DisposeProfileWatcher()
    {
        profileWatcher?.Dispose();
        profileWatcher = null;
        profileDebounce?.Stop();
        profileDebounce = null;
        profileLoading?.Cancel();
        profileLoading?.Dispose();
        profileLoading = null;
    }
}
