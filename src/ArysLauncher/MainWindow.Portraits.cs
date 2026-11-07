using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ArysLauncher;

public partial class MainWindow
{
    private void RefreshAccountPortrait(LauncherAccount? account)
    {
        if (IsBusy || authenticationCancellation != null || account != null && portraitAccountId == account.Id)
        {
            return;
        }
        CancelPortraitLoad();
        portraitAccountId = account?.Id ?? "";
        AccountPortraitImage.Source = null;
        AccountPortraitImage.Visibility = Visibility.Collapsed;
        AccountInitial.Visibility = Visibility.Visible;
        if (account == null)
        {
            return;
        }
        portraitCancellation = CancellationTokenSource.CreateLinkedTokenSource(windowLifetime.Token);
        portraitCancellation.CancelAfter(TimeSpan.FromSeconds(25));
        _ = LoadAccountPortraitAsync(account, portraitCancellation.Token);
    }

    private async Task LoadAccountPortraitAsync(LauncherAccount account, CancellationToken token)
    {
        try
        {
            await Task.Run(() => portraits.LoadAsync(account, portrait =>
                Dispatcher.InvokeAsync(() => PresentAccountPortrait(account, portrait, token), DispatcherPriority.Background, token), token), token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or InvalidOperationException or
            JsonException or FormatException or NotSupportedException or ArgumentException or UnauthorizedAccessException or
            CryptographicException or KeyNotFoundException)
        {
        }
    }

    private void PresentAccountPortrait(LauncherAccount account, AccountPortrait portrait, CancellationToken token)
    {
        if (closed || token.IsCancellationRequested || portraitAccountId != account.Id)
        {
            return;
        }
        var initial = AccountPortraitImage.Source == null;
        AccountPortraitImage.Source = portrait.Image;
        RenderOptions.SetBitmapScalingMode(AccountPortraitImage,
            portrait.Pixelated ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        AccountPortraitImage.ToolTip = portrait.Pixelated ? LauncherText.Get("SkinAvatar") : LauncherText.Get("XboxAvatar");
        AccountPortraitImage.Visibility = Visibility.Visible;
        AccountInitial.Visibility = Visibility.Collapsed;
        if (!ReferenceEquals(storage.Accounts.FirstOrDefault(current => current.Id == account.Id), account))
        {
            RefreshPresentation();
        }
        if (initial && !LauncherMotion.GetReducedMotion(this))
        {
            AccountPortraitImage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240))
            {
                FillBehavior = FillBehavior.Stop,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }

    private void CancelPortraitLoad()
    {
        portraitCancellation?.Cancel();
        portraitCancellation?.Dispose();
        portraitCancellation = null;
        portraitAccountId = "";
    }
}
