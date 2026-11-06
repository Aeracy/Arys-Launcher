using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Web;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using XboxAuthNet.OAuth.CodeFlow;

namespace ArysLauncher;

public partial class MicrosoftLoginWindow : Window, IWebUI
{
    private static readonly TimeSpan BrowserInitializationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FirstDocumentTimeout = TimeSpan.FromSeconds(45);
    private readonly LauncherStorage storage;
    private readonly CancellationTokenSource lifetime;
    private CancellationTokenRegistration cancellationRegistration;
    private TaskCompletionSource<CodeFlowAuthorizationResult>? authorizationCompletion;
    private TaskCompletionSource? navigationCompletion;
    private ICodeFlowUrlChecker? callbackChecker;
    private LauncherAccount? authenticatedAccount;
    private string expectedState = "";
    private bool windowClosed;
    private bool signingIn;
    private bool browserAttempted;
    private bool browserDisposed;
    private int loginAttempt;
    private ulong activeNavigationId;

    public MicrosoftLoginWindow(LauncherStorage storage, CancellationToken cancellationToken)
    {
        InitializeComponent();
        this.storage = storage;
        ApplyMotionPreference();
        StateChanged += (_, _) => ApplyMotionPreference();
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    public Task<LauncherAccount> SignInAsync()
    {
        lifetime.Token.ThrowIfCancellationRequested();
        cancellationRegistration = lifetime.Token.Register(() => Dispatcher.BeginInvoke(() =>
        {
            if (!windowClosed)
            {
                Close();
            }
        }));
        if (ShowDialog() == true && authenticatedAccount != null)
        {
            return Task.FromResult(authenticatedAccount);
        }
        throw new OperationCanceledException(LauncherText.Get("MicrosoftWindowClosed"));
    }

    public Task<CodeFlowAuthorizationResult> DisplayDialogAndInterceptUri(Uri uri, ICodeFlowUrlChecker uriChecker,
        CancellationToken cancellationToken)
    {
        return Dispatcher.InvokeAsync(() => AuthorizeInsideWindowAsync(uri, uriChecker, cancellationToken),
            DispatcherPriority.Normal, cancellationToken).Task.Unwrap();
    }

    public Task DisplayDialogAndNavigateUri(Uri uri, CancellationToken cancellationToken)
    {
        return Dispatcher.InvokeAsync(() => NavigateInsideWindowAsync(uri, cancellationToken),
            DispatcherPriority.Normal, cancellationToken).Task.Unwrap();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        LauncherMotion.Enter(LoginMessage, LauncherMotion.GetReducedMotion(this));
        await SignInCoreAsync();
    }

    private async Task SignInCoreAsync()
    {
        if (signingIn || windowClosed)
        {
            return;
        }
        RetryButton.Visibility = Visibility.Collapsed;
        LoginActivity.Visibility = Visibility.Visible;
        signingIn = true;
        var attempt = ++loginAttempt;
        RecordDiagnostic("login.started");
        try
        {
            if (browserAttempted || browserDisposed)
            {
                DisposeBrowser();
                BrowserHost.Children.Remove(Browser);
                Browser = new WebView2CompositionControl
                {
                    DefaultBackgroundColor = System.Drawing.Color.White,
                    IsHitTestVisible = false
                };
                BrowserHost.Children.Insert(0, Browser);
                browserDisposed = false;
                browserAttempted = false;
            }
            IProgress<string> progress = new Progress<string>(message =>
            {
                if (!windowClosed && attempt == loginAttempt)
                {
                    LoginStatusText.Text = message;
                    if (authorizationCompletion?.Task.IsCompletedSuccessfully == true)
                    {
                        ShowMessage(LauncherText.Get("VerifyingGameAccount"), message);
                    }
                }
            });
            using var accounts = new AccountService(storage);
            authenticatedAccount = await accounts.LoginMicrosoftAsync(this, progress.Report, lifetime.Token);
            if (!windowClosed)
            {
                DialogResult = true;
            }
        }
        catch (OperationCanceledException)
        {
            RecordDiagnostic("login.cancelled");
            if (!windowClosed && !lifetime.IsCancellationRequested)
            {
                ShowMessage(LauncherText.Get("MicrosoftAuthorizationEnded"), LauncherText.Get("MicrosoftSignInTimedOut"));
                RetryButton.Visibility = Visibility.Visible;
            }
        }
        catch (WebView2RuntimeNotFoundException)
        {
            RecordDiagnostic("browser.runtime-missing");
            if (!windowClosed)
            {
                ShowMessage(LauncherText.Get("SignInComponentMissing"), LauncherText.Get("SignInComponentRepair"));
                RetryButton.Visibility = Visibility.Visible;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or
            System.Net.Http.HttpRequestException or JsonException or COMException or UnauthorizedAccessException or
            KeyNotFoundException or FormatException or NotSupportedException or TimeoutException)
        {
            RecordDiagnostic($"login.failed {exception.GetType().Name} HRESULT=0x{exception.HResult:X8}");
            if (!windowClosed)
            {
                ShowMessage(LauncherText.Get("MicrosoftSignInIncomplete"), exception is JsonException or KeyNotFoundException or FormatException ?
                    LauncherText.Get("MicrosoftResponseIncomplete") : exception.Message);
                RetryButton.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            LoginActivity.Visibility = Visibility.Collapsed;
            if (!windowClosed && authenticatedAccount == null)
            {
                DisposeBrowser();
            }
            signingIn = false;
            authorizationCompletion = null;
            navigationCompletion = null;
            callbackChecker = null;
            expectedState = "";
        }
    }

    private void ApplyMotionPreference()
    {
        LauncherMotion.SetReducedMotion(this, storage.Settings.ReducedMotion || !SystemParameters.ClientAreaAnimation ||
            WindowState == WindowState.Minimized);
    }

    private async Task<CodeFlowAuthorizationResult> AuthorizeInsideWindowAsync(Uri authorizationUri,
        ICodeFlowUrlChecker uriChecker, CancellationToken cancellationToken)
    {
        var request = HttpUtility.ParseQueryString(authorizationUri.Query);
        var redirect = request["redirect_uri"];
        var state = request["state"];
        if (!IsSamePage(authorizationUri, new Uri(CodeFlowLiveApiClient.OAuthAuthorize)) ||
            !Uri.TryCreate(redirect, UriKind.Absolute, out var callback) ||
            !IsSamePage(callback, new Uri(CodeFlowLiveApiClient.OAuthDesktop)) || string.IsNullOrEmpty(state) || state.Length != 64)
        {
            throw new InvalidDataException(LauncherText.Get("MicrosoftRequestUnexpected"));
        }
        ShowMessage(LauncherText.Get("OpeningMicrosoftSignIn"), LauncherText.Get("MicrosoftConsentPageHelp"));
        callbackChecker = uriChecker;
        expectedState = state;
        authorizationCompletion = new TaskCompletionSource<CodeFlowAuthorizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await EnsureBrowserAsync(cancellationToken);
        await NavigateAndWaitForDocumentAsync(authorizationUri, cancellationToken);
        return await authorizationCompletion.Task.WaitAsync(cancellationToken);
    }

    private async Task NavigateInsideWindowAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidDataException(LauncherText.Get("MicrosoftRequiresHttps"));
        }
        ShowMessage(LauncherText.Get("LoadingMicrosoftPage"), LauncherText.Get("PleaseWait"));
        await EnsureBrowserAsync(cancellationToken);
        await NavigateAndWaitForDocumentAsync(uri, cancellationToken);
    }

    private async Task EnsureBrowserAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Browser.CoreWebView2 != null)
        {
            return;
        }
        browserAttempted = true;
        ShowMessage(LauncherText.Get("PreparingSignInWindow"), LauncherText.Get("StartingEmbeddedBrowser"));
        RecordDiagnostic("browser.layout");
        Browser.Visibility = Visibility.Visible;
        Browser.ApplyTemplate();
        BrowserHost.UpdateLayout();
        await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Loaded);
        cancellationToken.ThrowIfCancellationRequested();
        var options = new CoreWebView2EnvironmentOptions { Language = LauncherText.Culture.Name };
        if (!string.IsNullOrWhiteSpace(storage.Settings.ProxyAddress))
        {
            options.AdditionalBrowserArguments = "--proxy-server=" + new Uri(storage.Settings.ProxyAddress).GetLeftPart(UriPartial.Authority);
        }
        var runtimePath = await BrowserRuntime.ResolveAsync(storage, cancellationToken);
        if (runtimePath != null)
        {
            PrepareSandboxAccess(runtimePath);
        }
        using var initialization = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        initialization.CancelAfter(BrowserInitializationTimeout);
        try
        {
            RecordDiagnostic(runtimePath == null ? "browser.environment system" : "browser.environment bundled");
            var environment = await CoreWebView2Environment.CreateAsync(runtimePath,
                Path.Combine(storage.Root, "browser", "microsoft"), options).WaitAsync(initialization.Token);
            initialization.Token.ThrowIfCancellationRequested();
            RecordDiagnostic("browser.controller");
            await Browser.EnsureCoreWebView2Async(environment).WaitAsync(initialization.Token);
            initialization.Token.ThrowIfCancellationRequested();
            ConfigureBrowser();
            RecordDiagnostic($"browser.ready {environment.BrowserVersionString}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            RecordDiagnostic("browser.initialization-timeout");
            throw new TimeoutException(LauncherText.Get("SignInBrowserTimedOut"));
        }
    }

    private async Task NavigateAndWaitForDocumentAsync(Uri uri, CancellationToken cancellationToken)
    {
        ShowMessage(LauncherText.Get("LoadingMicrosoftSignIn"), LauncherText.Get("WaitingForMicrosoftPage"));
        navigationCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        activeNavigationId = 0;
        RecordDiagnostic("page.requested");
        Browser.CoreWebView2.Navigate(uri.AbsoluteUri);
        try
        {
            Task completed = navigationCompletion.Task;
            if (authorizationCompletion != null)
            {
                completed = await Task.WhenAny(navigationCompletion.Task, authorizationCompletion.Task)
                    .WaitAsync(FirstDocumentTimeout, cancellationToken);
            }
            await completed.WaitAsync(FirstDocumentTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            RecordDiagnostic("page.first-document-timeout");
            throw new TimeoutException(LauncherText.Get("MicrosoftPageTimedOut"));
        }
    }

    private void ConfigureBrowser()
    {
        var core = Browser.CoreWebView2;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsStatusBarEnabled = false;
        core.NavigationStarting += NavigationStarting;
        core.NavigationCompleted += NavigationCompleted;
        core.DOMContentLoaded += DocumentContentLoaded;
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            if (!windowClosed && !browserDisposed && ReferenceEquals(core, Browser.CoreWebView2) &&
                Uri.TryCreate(args.Uri, UriKind.Absolute, out var target) && target.Scheme == "https" && string.IsNullOrEmpty(target.UserInfo))
            {
                core.Navigate(target.AbsoluteUri);
            }
        };
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        core.ServerCertificateErrorDetected += (_, args) => args.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
        core.ProcessFailed += (_, _) =>
        {
            if (!windowClosed && !browserDisposed && ReferenceEquals(core, Browser.CoreWebView2))
            {
                RecordDiagnostic("browser.process-failed");
                var failure = new InvalidOperationException(LauncherText.Get("MicrosoftPageInterrupted"));
                authorizationCompletion?.TrySetException(failure);
                navigationCompletion?.TrySetException(failure);
            }
        };
    }

    private void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (windowClosed || browserDisposed || !ReferenceEquals(sender, Browser.CoreWebView2))
        {
            return;
        }
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) || target.Scheme != "https" || !string.IsNullOrEmpty(target.UserInfo))
        {
            e.Cancel = true;
            LoginStatusText.Text = LauncherText.Get("MicrosoftPageRequiresHttps");
            return;
        }
        if (IsSamePage(target, new Uri(CodeFlowLiveApiClient.OAuthDesktop)))
        {
            e.Cancel = true;
            if (callbackChecker != null && authorizationCompletion?.Task.IsCompleted == false)
            {
                var result = callbackChecker.GetAuthCodeResult(target);
                var receivedState = Encoding.UTF8.GetBytes(result.State ?? "");
                var state = Encoding.UTF8.GetBytes(expectedState);
                if (!CryptographicOperations.FixedTimeEquals(state, receivedState))
                {
                    authorizationCompletion.TrySetException(new InvalidDataException(LauncherText.Get("MicrosoftSignInStateInvalid")));
                }
                else
                {
                    RecordDiagnostic("authorization.callback-validated");
                    authorizationCompletion.TrySetResult(result);
                }
            }
            return;
        }
        activeNavigationId = e.NavigationId;
        OriginText.Text = target.GetLeftPart(UriPartial.Authority);
        LoginStatusText.Text = LauncherText.Get("LoadingMicrosoftConsent");
        RecordDiagnostic("page.navigation " + target.GetLeftPart(UriPartial.Authority));
    }

    private void DocumentContentLoaded(object? sender, CoreWebView2DOMContentLoadedEventArgs e)
    {
        if (windowClosed || browserDisposed || !signingIn || !ReferenceEquals(sender, Browser.CoreWebView2) ||
            e.NavigationId != activeNavigationId || authorizationCompletion?.Task.IsCompleted == true)
        {
            return;
        }
        ShowBrowserDocument();
        RecordDiagnostic("page.dom-ready");
    }

    private void NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (windowClosed || browserDisposed || !signingIn || !ReferenceEquals(sender, Browser.CoreWebView2) ||
            e.NavigationId != activeNavigationId || authorizationCompletion?.Task.IsCompleted == true)
        {
            return;
        }
        if (e.IsSuccess)
        {
            ShowBrowserDocument();
        }
        else if (e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
        {
            RecordDiagnostic("page.failed " + e.WebErrorStatus);
            var failure = new IOException(LauncherText.Get("MicrosoftPageLoadFailed", e.WebErrorStatus));
            authorizationCompletion?.TrySetException(failure);
            navigationCompletion?.TrySetException(failure);
        }
    }

    private void ShowBrowserDocument()
    {
        Browser.IsHitTestVisible = true;
        LoadingPanel.Visibility = Visibility.Collapsed;
        LoginStatusText.Text = LauncherText.Get("MicrosoftSignInPageHelp");
        navigationCompletion?.TrySetResult();
    }

    private static bool IsSamePage(Uri actual, Uri expected)
    {
        return actual.Scheme == expected.Scheme && actual.Host.Equals(expected.Host, StringComparison.OrdinalIgnoreCase) &&
            actual.Port == expected.Port && actual.AbsolutePath.Equals(expected.AbsolutePath, StringComparison.Ordinal) &&
            string.IsNullOrEmpty(actual.UserInfo);
    }

    private void ShowMessage(string title, string detail)
    {
        Browser.IsHitTestVisible = false;
        LoadingTitle.Text = title;
        LoadingDetail.Text = detail;
        LoadingPanel.Visibility = Visibility.Visible;
        LoginStatusText.Text = title;
    }

    private void DisposeBrowser()
    {
        if (!browserDisposed)
        {
            browserDisposed = true;
            Browser.Dispose();
        }
    }

    private void RecordDiagnostic(string stage)
    {
        try
        {
            File.AppendAllText(Path.Combine(storage.Logs, "microsoft-login.log"), $"{DateTimeOffset.UtcNow:O} {stage}{Environment.NewLine}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        await SignInCoreAsync();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        windowClosed = true;
        lifetime.Cancel();
        authorizationCompletion?.TrySetCanceled();
        navigationCompletion?.TrySetCanceled();
        cancellationRegistration.Dispose();
        DisposeBrowser();
        lifetime.Dispose();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static void PrepareSandboxAccess(string runtimePath)
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }
        var directory = new DirectoryInfo(runtimePath);
        var permissions = directory.GetAccessControl();
        foreach (var sid in new[] { "S-1-15-2-1", "S-1-15-2-2" })
        {
            permissions.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        directory.SetAccessControl(permissions);
    }
}
