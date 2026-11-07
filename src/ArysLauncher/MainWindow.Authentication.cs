namespace ArysLauncher;

public partial class MainWindow
{
    private async Task RunAuthenticationAsync(string title, Func<CancellationToken, Task> authenticate)
    {
        if (authenticationCancellation != null || closed)
        {
            return;
        }
        CancelPortraitLoad();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(windowLifetime.Token);
        authenticationCancellation = cancellation;
        RefreshPresentation();
        SetStatus(title);
        try
        {
            await authenticate(cancellation.Token);
            SetStatus(title + LauncherText.Get("Done"));
        }
        catch (OperationCanceledException)
        {
            if (!closed)
            {
                SetStatus(LauncherText.Get("AuthenticationCancelled"));
            }
        }
        catch (Exception exception)
        {
            if (!closed)
            {
                SetStatus(exception.Message, true);
            }
        }
        finally
        {
            authenticationCancellation = null;
            if (!closed)
            {
                RefreshPresentation();
            }
        }
    }
}
