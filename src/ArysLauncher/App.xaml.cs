using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows;

namespace ArysLauncher;

public partial class App : Application
{
    public static string LauncherVersion { get; } = typeof(App).Assembly.GetName().Version!.ToString(3);

    private Mutex? instanceMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.FirstOrDefault() == "--apply-update")
        {
            try
            {
                await UpdateBootstrap.ApplyAsync(e.Args);
            }
            catch (Exception exception)
            {
                MessageBox.Show(exception.Message, LauncherText.Get("LauncherUpdateIncomplete"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            Shutdown();
            return;
        }
        SetCurrentProcessExplicitAppUserModelID("Arys.EternalDungeon.Launcher");
        var identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        instanceMutex = new Mutex(true, "Local\\ArysLauncher." + identity, out var created);
        if (!created)
        {
            MessageBox.Show(LauncherText.Get("LauncherAlreadyRunning"), "Arys Launcher", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            var logDirectory = Path.Combine(AppContext.BaseDirectory, "startup-logs");
            try
            {
                Directory.CreateDirectory(logDirectory);
                File.WriteAllText(Path.Combine(logDirectory, "startup-error.txt"), args.Exception.GetType().Name + Environment.NewLine + args.Exception.Message);
            }
            catch (IOException)
            {
            }
            MessageBox.Show(args.Exception.Message, LauncherText.Get("LauncherErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        base.OnStartup(e);
        try
        {
            var storage = new LauncherStorage();
            await LauncherUpdates.RecoverAsync(storage, AppContext.BaseDirectory);
            await LauncherUpdates.RecordLauncherFilesAsync(storage);
            new MainWindow(storage).Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, LauncherText.Get("LauncherRecoveryRequired"), MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instanceMutex?.Dispose();
        base.OnExit(e);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
