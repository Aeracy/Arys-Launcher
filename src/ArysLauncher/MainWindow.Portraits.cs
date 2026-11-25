using System.Windows;
using System.Windows.Media;

namespace ArysLauncher;

public partial class MainWindow
{
    private void RefreshAccountPortrait(LauncherAccount? account)
    {
        var image = account != null && profileAccountId == account.Id &&
            profileScopeKey == PlayerProfileStore.ProfileKey(account) ? localProfileAvatar : null;
        AccountPortraitImage.Source = image;
        AccountPortraitImage.Visibility = image == null ? Visibility.Collapsed : Visibility.Visible;
        AccountPortraitImage.ToolTip = image == null ? null : LauncherText.Get("ProfileCustomAvatar");
        RenderOptions.SetBitmapScalingMode(AccountPortraitImage, BitmapScalingMode.HighQuality);
    }
}
