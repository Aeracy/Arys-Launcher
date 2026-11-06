using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ArysLauncher;

public partial class MainWindow : Window
{
    public static readonly DependencyProperty WorkProgressValueProperty = DependencyProperty.Register(nameof(WorkProgressValue),
        typeof(double), typeof(MainWindow), new PropertyMetadata(0.0));
    public static readonly DependencyProperty IsWorkIndeterminateProperty = DependencyProperty.Register(nameof(IsWorkIndeterminate),
        typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    private readonly LauncherStorage storage;
    private readonly JavaRuntimeCatalog javaCatalog;
    private readonly CancellationTokenSource windowLifetime = new();
    private readonly DispatcherTimer memoryRefresh = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<string> history = new();
    private CancellationTokenSource? operationCancellation;
    private PackPlan? importPlan;
    private GameInstallation? editingInstallation;
    private RunningGame? runningGame;
    private AcrylicWindowBackdrop? windowBackdrop;
    private bool initialized;
    private bool refreshing;
    private bool changingMemory;
    private bool closeAfterOperation;
    private bool closed;
    private string lastStage = "";
    private double displayedProgress;
    private long speedSampleTime;
    private long speedSampleBytes;
    private double measuredSpeed;
    private bool loadingSettings;
    private bool scanningJava;
    private string activePage = "";
    private IReadOnlyList<JavaRuntime> javaRuntimes = Array.Empty<JavaRuntime>();
    private PhysicalMemory? physicalMemory;

    public double WorkProgressValue => (double)GetValue(WorkProgressValueProperty);
    public bool IsWorkIndeterminate => (bool)GetValue(IsWorkIndeterminateProperty);

    private bool IsBusy
    {
        get
        {
            return operationCancellation != null;
        }
    }

    public MainWindow(LauncherStorage storage)
    {
        InitializeComponent();
        this.storage = storage;
        javaCatalog = new JavaRuntimeCatalog(storage);
        ConcurrencyCombo.ItemsSource = new[] { 1, 2, 4, 6, 8, 12, 16, 24, 32 };
        HostConcurrencyCombo.ItemsSource = new[] { 1, 2, 4, 6, 8, 12, 16 };
        initialized = true;
        LoadSettings();
        RefreshPresentation();
        ShowPage("home");
        AddHistory("启动器已就绪。");
        memoryRefresh.Tick += (_, _) => RefreshMemoryStatus();
        StateChanged += (_, _) => ApplyMotionPreference();
        SystemParameters.StaticPropertyChanged += SystemParameters_Changed;
        Closed += (_, _) =>
        {
            closed = true;
            memoryRefresh.Stop();
            SystemParameters.StaticPropertyChanged -= SystemParameters_Changed;
            windowLifetime.Cancel();
            windowLifetime.Dispose();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        windowBackdrop = AcrylicWindowBackdrop.Attach(this, WindowSurface);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        AnimatePage(HomePage);
        RefreshMemoryStatus();
        memoryRefresh.Start();
        await ScanJavaAsync(false);
        if (storage.Settings.CheckUpdatesAtStartup)
        {
            await CheckUpdatesAsync();
        }
    }

    private void Navigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initialized && Navigation.SelectedItem is ListBoxItem item && item.Tag is string page)
        {
            ShowPage(page);
        }
    }

    private void ShowPage(string page)
    {
        if (activePage == page)
        {
            return;
        }
        var previous = GetPage(activePage);
        activePage = page;
        HomePage.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
        InstallationPage.Visibility = page == "instances" ? Visibility.Visible : Visibility.Collapsed;
        AccountsPage.Visibility = page == "accounts" ? Visibility.Visible : Visibility.Collapsed;
        TasksPage.Visibility = page == "tasks" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        (PageTitle.Text, PageSubtitle.Text) = page switch
        {
            "instances" => ("整合包", "查看安装位置和游戏资源。"),
            "accounts" => ("账号", "选择用于启动游戏的账号。"),
            "tasks" => ("下载任务", "查看当前进度与操作记录。"),
            "settings" => ("设置", "按你的电脑与网络调整运行方式。"),
            _ => ("准备游戏", "整合包与启动状态")
        };
        var item = Navigation.Items.OfType<ListBoxItem>().FirstOrDefault(item => (string)item.Tag == page);
        if (item != null && Navigation.SelectedItem != item)
        {
            Navigation.SelectedItem = item;
        }
        LauncherMotion.Reset(previous);
        if (previous == HomePage)
        {
            LauncherMotion.Reset(HomeSidebar);
        }
        UpdateWorkStrip();
        AnimatePage(GetPage(page));
    }

    private FrameworkElement GetPage(string page)
    {
        return page switch
        {
            "instances" => InstallationPage,
            "accounts" => AccountsPage,
            "tasks" => TasksPage,
            "settings" => SettingsPage,
            _ => HomePage
        };
    }

    private void AnimatePage(FrameworkElement page)
    {
        if (!IsLoaded || !page.IsVisible)
        {
            return;
        }
        var reduced = LauncherMotion.GetReducedMotion(this);
        LauncherMotion.Enter(page, reduced);
        if (page == HomePage)
        {
            LauncherMotion.Enter(HomeSidebar, reduced, 45);
        }
    }

    private void ApplyMotionPreference()
    {
        var reduced = storage.Settings.ReducedMotion || !SystemParameters.ClientAreaAnimation ||
            WindowState == WindowState.Minimized;
        if (LauncherMotion.GetReducedMotion(this) == reduced)
        {
            return;
        }
        LauncherMotion.SetReducedMotion(this, reduced);
        Atmosphere.ReducedMotion = reduced;
        if (reduced)
        {
            LauncherMotion.Reset(GetPage(activePage));
            LauncherMotion.Reset(HomeSidebar);
            LauncherMotion.Reset(WorkStrip);
            LauncherMotion.Reset(ImportDialog);
            PhysicalMemoryMeter.CompleteTransition();
            PresentProgress((double)GetAnimationBaseValue(WorkProgressValueProperty), IsWorkIndeterminate, immediate: true);
        }
    }

    private void SystemParameters_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast))
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (!closed)
                {
                    if (e.PropertyName == nameof(SystemParameters.HighContrast))
                    {
                        windowBackdrop?.Refresh();
                    }
                    else
                    {
                        ApplyMotionPreference();
                    }
                }
            });
        }
    }

    private void UpdateWorkStrip()
    {
        var visibility = IsBusy && activePage != "tasks" ? Visibility.Visible : Visibility.Collapsed;
        if (WorkStrip.Visibility == visibility)
        {
            return;
        }
        WorkStrip.Visibility = visibility;
        if (visibility == Visibility.Visible)
        {
            LauncherMotion.Enter(WorkStrip, LauncherMotion.GetReducedMotion(this));
        }
        else
        {
            LauncherMotion.Reset(WorkStrip);
        }
    }

    private void RefreshPresentation()
    {
        refreshing = true;
        try
        {
            var selectedAccount = storage.Accounts.FirstOrDefault(account => account.Id == storage.Settings.SelectedAccountId)
                ?? storage.Accounts.FirstOrDefault();
            AccountList.ItemsSource = storage.Accounts.ToArray();
            AccountList.SelectedItem = selectedAccount;
            HomeAccountName.Text = selectedAccount?.PlayerName ?? "尚未登录";
            HomeAccountKind.Text = selectedAccount?.Description ?? "登录后启动游戏";
            HomeAccountButton.Content = selectedAccount == null ? "添加账号  ↗" : "管理账号  ↗";
            AccountInitial.Text = selectedAccount == null ? "◇" : StringInfo.GetNextTextElement(selectedAccount.PlayerName);
            HomeMemory.Text = $"{(physicalMemory == null ? storage.Settings.MaximumMemoryMb : MemoryAllocation.MaximumMb(storage.Settings, physicalMemory)) / 1024.0:0.0} GiB";
            var selectedJava = SelectedJavaRuntime(storage.Settings.AutomaticJavaSelection, storage.Settings.JavaPath);
            HomeJava.Text = selectedJava == null ? "自动获取" : "Java " + selectedJava.MajorVersion;
            ApplyMotionPreference();
            UpdateWorkStrip();
            var installation = storage.Installation;
            HomeInstanceHeading.Text = installation == null ? "安装整合包" : "已安装";
            HomeInstanceDescription.Text = installation?.VersionLabel ?? "将整合包文件拖到窗口，或从本地选择。";
            HomeInstanceDetails.Text = installation == null ? "支持 .mrpack 和整合包 ZIP" :
                $"{installation.ModCount} 个 Mod  ·  {installation.Name}";
            HomeInstanceButton.Content = installation == null ? "浏览文件  →" : "查看整合包  →";
            InstallationHeading.Text = installation?.Name ?? "专属整合包";
            InstallationSummary.Text = installation == null ? "尚未导入整合包" :
                $"{installation.VersionLabel}  ·  {installation.ModCount} 个 Mod";
            InstallationPath.Text = installation?.DirectoryPath ?? storage.GameDirectory;
            FooterDetail.Text = installation == null ? "导入整合包后开始游戏" :
                selectedAccount == null ? "整合包已导入，请先登录账号" : $"{selectedAccount.PlayerName}  ·  {installation.MinecraftVersion}";
            HeaderImportButton.Content = installation == null ? "导入整合包" : "打开游戏目录  ↗";
            LaunchButton.Content = runningGame != null ? "游戏运行中" : installation == null ? "导入整合包" :
                selectedAccount == null ? "登录账号" : "开始游戏";
            LaunchButton.IsEnabled = !IsBusy && runningGame == null;
            HeaderImportButton.IsEnabled = !IsBusy;
            PrepareButton.IsEnabled = !IsBusy && runningGame == null && installation != null;
            MicrosoftLoginButton.IsEnabled = !IsBusy;
            ExternalLoginButton.IsEnabled = !IsBusy;
            SaveSettingsButton.IsEnabled = !IsBusy && !scanningJava;
            AccountList.IsEnabled = !IsBusy;
            ChooseArchiveButton.IsEnabled = !IsBusy;
            CommitImportButton.IsEnabled = !IsBusy && (importPlan != null || editingInstallation != null);
            TaskCancelButton.Visibility = IsBusy ? Visibility.Visible : Visibility.Collapsed;
            TaskCancelButton.IsEnabled = StripCancelButton.IsEnabled = operationCancellation?.IsCancellationRequested == false;
            RescanJavaButton.IsEnabled = !IsBusy && !scanningJava;
            ChooseJavaButton.IsEnabled = !IsBusy && !scanningJava;
            JavaRuntimeCombo.IsEnabled = !IsBusy && !scanningJava;
            RefreshUpdateButtons();
        }
        finally
        {
            refreshing = false;
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (storage.Installation != null)
        {
            OpenDirectory(storage.Installation.DirectoryPath);
            return;
        }
        ShowImport();
    }

    private void ShowImport()
    {
        if (IsBusy)
        {
            return;
        }
        if (storage.Installation != null)
        {
            ShowPage("instances");
            SetStatus("本整合包已导入。现有游戏目录会保留。");
            return;
        }
        importPlan = null;
        editingInstallation = null;
        ImportTitle.Text = "导入整合包";
        ImportSourcePanel.Visibility = Visibility.Visible;
        ImportSummary.Text = "选择文件后显示整合包信息。文件会安装到启动器下的 .minecraft 目录。";
        ArchivePathText.Clear();
        ImportNameText.Text = "少女の永恒地牢";
        ImportMinecraftText.Text = "1.20.1";
        ImportForgeText.Text = "47.4.16";
        ImportErrorText.Visibility = Visibility.Collapsed;
        CommitImportButton.Content = "开始导入";
        CommitImportButton.IsEnabled = false;
        OpenImportDialog();
        ChooseArchiveButton.Focus();
    }

    private async void ChooseArchive_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择整合包文件",
            Filter = "Minecraft 整合包 (*.mrpack;*.zip)|*.mrpack;*.zip",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            await InspectArchiveAsync(dialog.FileName);
        }
    }

    private async Task InspectArchiveAsync(string path)
    {
        importPlan = null;
        ImportErrorText.Visibility = Visibility.Collapsed;
        ArchivePathText.Text = path;
        ImportSummary.Text = "正在读取整合包信息。";
        await RunOperationAsync("读取整合包", async (token, _) =>
        {
            var importer = new PackImporter(storage);
            var plan = await Task.Run(() => importer.Inspect(path, storage.Settings.IncludeOptionalMods, token), token);
            importPlan = plan;
            ImportNameText.Text = plan.Name;
            ImportMinecraftText.Text = plan.MinecraftVersion;
            ImportForgeText.Text = string.IsNullOrEmpty(plan.ForgeVersion) ? "47.4.16" : plan.ForgeVersion;
            ImportSummary.Text = $"{plan.Format}  ·  {plan.Entries.Count + plan.Downloads.Count} 个文件\n" +
                $"本地提取 {FormatBytes(plan.ExtractedBytes)}  ·  依赖文件 {FormatBytes(plan.DownloadBytes)}" +
                (plan.OptionalFiles > 0 ? $"  ·  可选文件 {plan.OptionalFiles} 个" : "");
        }, false);
    }

    private async void CommitImport_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
        {
            return;
        }
        ImportErrorText.Visibility = Visibility.Collapsed;
        if (editingInstallation != null)
        {
            try
            {
                var minecraft = ImportMinecraftText.Text.Trim();
                var forge = ImportForgeText.Text.Trim();
                ValidateVersionFields(minecraft, forge);
                var edited = JsonSerializer.Deserialize<GameInstallation>(JsonSerializer.Serialize(editingInstallation, LauncherStorage.JsonOptions), LauncherStorage.JsonOptions)!;
                edited.Name = ImportNameText.Text.Trim();
                if (string.IsNullOrWhiteSpace(edited.Name))
                {
                    throw new InvalidOperationException("请填写整合包名称。");
                }
                if (edited.MinecraftVersion != minecraft || edited.ForgeVersion != forge)
                {
                    edited.LaunchVersion = "";
                }
                edited.MinecraftVersion = minecraft;
                edited.ForgeVersion = forge;
                storage.SaveInstallation(edited);
                HideImportDialog();
                editingInstallation = null;
                RefreshPresentation();
                SetStatus("整合包配置已保存。");
            }
            catch (Exception exception)
            {
                ImportErrorText.Text = exception.Message;
                ImportErrorText.Visibility = Visibility.Visible;
            }
            return;
        }
        var plan = importPlan;
        if (plan == null)
        {
            return;
        }
        var name = ImportNameText.Text.Trim();
        var minecraftVersion = ImportMinecraftText.Text.Trim();
        var forgeVersion = ImportForgeText.Text.Trim();
        HideImportDialog();
        await RunOperationAsync("导入整合包", async (token, progress) =>
        {
            var importer = new PackImporter(storage);
            var installation = await Task.Run(() => importer.ImportAsync(plan, name, minecraftVersion, forgeVersion, progress, token), token);
            storage.SaveInstallation(installation);
            importPlan = null;
            LoadSettings();
            RefreshPresentation();
        });
    }

    private void EditInstance_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy || runningGame != null)
        {
            SetStatus("请在当前任务和游戏结束后修改整合包配置。");
            return;
        }
        if (storage.Installation is not { } installation)
        {
            ShowImport();
            return;
        }
        importPlan = null;
        editingInstallation = installation;
        ImportTitle.Text = "整合包配置";
        ImportSourcePanel.Visibility = Visibility.Collapsed;
        ImportSummary.Text = "请保持版本与本整合包要求一致。更改版本后，下一次启动会重新准备对应环境。";
        ImportNameText.Text = installation.Name;
        ImportMinecraftText.Text = installation.MinecraftVersion;
        ImportForgeText.Text = installation.ForgeVersion;
        ImportErrorText.Visibility = Visibility.Collapsed;
        CommitImportButton.Content = "保存配置";
        CommitImportButton.IsEnabled = true;
        OpenImportDialog();
        ImportNameText.Focus();
    }

    private async void MicrosoftLogin_Click(object sender, RoutedEventArgs e)
    {
        await OpenMicrosoftLoginAsync();
    }

    private async Task OpenMicrosoftLoginAsync()
    {
        await RunOperationAsync("Microsoft 正版登录", async (token, _) =>
        {
            var window = new MicrosoftLoginWindow(storage, token) { Owner = this };
            await window.SignInAsync();
            RefreshPresentation();
        }, false);
    }

    private async void ExternalLogin_Click(object sender, RoutedEventArgs e)
    {
        var server = ExternalServerText.Text.Trim();
        var username = ExternalUsernameText.Text.Trim();
        var password = ExternalPasswordBox.Password;
        ExternalPasswordBox.Clear();
        await RunOperationAsync("外置认证登录", async (token, _) =>
        {
            SaveAuthenticationSettings();
            using var service = new AccountService(storage);
            await service.LoginExternalAsync(server, username, password, storage.Settings.AuthenticationServerName, ChooseProfileAsync, token);
            RefreshPresentation();
        }, false);
    }

    private Task<GameProfile> ChooseProfileAsync(IReadOnlyList<GameProfile> profiles)
    {
        var dialog = new Window
        {
            Owner = this,
            Title = "选择游戏角色",
            Width = 400,
            Height = 225,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)FindResource("BackgroundBrush"),
            Foreground = (Brush)FindResource("TextBrush")
        };
        var panel = new StackPanel { Margin = new Thickness(25) };
        panel.Children.Add(new TextBlock { Text = "选择此账号下的游戏角色", FontSize = 16, Margin = new Thickness(0, 0, 0, 17) });
        var choices = new ComboBox { ItemsSource = profiles, DisplayMemberPath = "Name", SelectedIndex = 0 };
        panel.Children.Add(choices);
        var confirm = new Button { Content = "使用此角色", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        confirm.Click += (_, _) => dialog.DialogResult = true;
        panel.Children.Add(confirm);
        dialog.Content = panel;
        if (dialog.ShowDialog() != true || choices.SelectedItem is not GameProfile profile)
        {
            throw new OperationCanceledException("已取消角色选择。");
        }
        return Task.FromResult(profile);
    }

    private async void RemoveAccount_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: LauncherAccount account } || IsBusy)
        {
            return;
        }
        await RunOperationAsync("移除账号", async (token, _) =>
        {
            using var service = new AccountService(storage);
            await service.RemoveAccountAsync(account, token);
            RefreshPresentation();
        }, false);
    }

    private void AccountList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || refreshing || IsBusy || AccountList.SelectedItem is not LauncherAccount account)
        {
            return;
        }
        try
        {
            storage.Settings.SelectedAccountId = account.Id;
            storage.SaveSettings(storage.Settings);
            RefreshPresentation();
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, true);
        }
    }

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy || runningGame != null)
        {
            return;
        }
        if (storage.Installation is not { } installation)
        {
            ShowImport();
            return;
        }
        var account = storage.Accounts.FirstOrDefault(account => account.Id == storage.Settings.SelectedAccountId)
            ?? storage.Accounts.FirstOrDefault();
        if (account == null)
        {
            ShowPage("accounts");
            await OpenMicrosoftLoginAsync();
            return;
        }
        await RunOperationAsync("启动游戏", async (token, progress) =>
        {
            var runtime = new GameRuntime(storage, javaCatalog);
            var game = await Task.Run(() => runtime.LaunchAsync(installation, account, progress, token), token);
            runningGame = game;
            storage.SaveInstallation(installation);
            RefreshPresentation();
            AddHistory($"游戏进程已启动。日志：{game.LogPath}");
            _ = ObserveGameAsync(game);
            if (storage.Settings.MinimizeOnLaunch)
            {
                WindowState = WindowState.Minimized;
            }
        });
    }

    private async Task ObserveGameAsync(RunningGame game)
    {
        try
        {
            var exitCode = await game.Completion;
            if (!closed)
            {
                AddHistory($"游戏已退出，退出码 {exitCode}。日志：{game.LogPath}");
                SetStatus(exitCode == 0 ? "游戏已退出。" : $"游戏退出码为 {exitCode}，可从下载任务页打开日志目录。", exitCode != 0);
            }
        }
        catch (Exception exception)
        {
            if (!closed)
            {
                SetStatus("游戏日志采集已结束：" + exception.Message, true);
            }
        }
        finally
        {
            if (!closed && runningGame == game)
            {
                runningGame = null;
                RefreshPresentation();
                if (storage.Settings.MinimizeOnLaunch && WindowState == WindowState.Minimized)
                {
                    WindowState = WindowState.Normal;
                }
            }
        }
    }

    private async void Prepare_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy || runningGame != null)
        {
            return;
        }
        if (storage.Installation is not { } installation)
        {
            ShowImport();
            return;
        }
        await RunOperationAsync("补全游戏环境", async (token, progress) =>
        {
            var runtime = new GameRuntime(storage, javaCatalog);
            await Task.Run(() => runtime.PrepareAsync(installation, progress, token), token);
            storage.SaveInstallation(installation);
            RefreshPresentation();
        });
    }

    private async Task RunOperationAsync(string title, Func<CancellationToken, IProgress<WorkSnapshot>, Task> operation, bool showTasks = true)
    {
        if (IsBusy)
        {
            return;
        }
        using var cancellation = new CancellationTokenSource();
        operationCancellation = cancellation;
        lastStage = "";
        displayedProgress = 0;
        speedSampleTime = Stopwatch.GetTimestamp();
        speedSampleBytes = 0;
        measuredSpeed = 0;
        TaskTitle.Text = title;
        TaskDetail.Text = "正在准备";
        PresentProgress(0, indeterminate: true, immediate: true);
        WorkStripTitle.Text = title;
        TaskStatistics.Text = "正在准备";
        AddHistory(title);
        if (showTasks)
        {
            ShowPage("tasks");
        }
        RefreshPresentation();
        WorkSnapshot? latest = null;
        var progress = new CallbackProgress<WorkSnapshot>(snapshot => Volatile.Write(ref latest, snapshot));
        void RefreshTaskProgress()
        {
            if (Volatile.Read(ref latest) is { } snapshot)
            {
                UpdateProgress(snapshot);
            }
        }
        var progressRefresh = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        progressRefresh.Tick += (_, _) => RefreshTaskProgress();
        progressRefresh.Start();
        try
        {
            await operation(cancellation.Token, progress);
            RefreshTaskProgress();
            TaskTitle.Text = title + "完成";
            TaskDetail.Text = "任务已完成。";
            if (latest == null)
            {
                TaskStatistics.Text = "已完成";
            }
            PresentProgress(100, indeterminate: false);
            AddHistory(title + "完成。");
            SetStatus(title + "完成。");
        }
        catch (OperationCanceledException)
        {
            RefreshTaskProgress();
            TaskTitle.Text = "任务已取消";
            TaskDetail.Text = "当前操作已取消。";
            AddHistory(title + "已取消。");
            SetStatus("任务已取消。");
        }
        catch (Exception exception)
        {
            RefreshTaskProgress();
            var message = exception is JsonException or KeyNotFoundException ? "整合包或服务响应缺少必要字段，请检查文件格式与认证配置。" : exception.Message;
            TaskTitle.Text = title + "未完成";
            TaskDetail.Text = message;
            AddHistory(message);
            SetStatus(message, true);
            if (ImportOverlay.Visibility == Visibility.Visible)
            {
                ImportErrorText.Text = message;
                ImportErrorText.Visibility = Visibility.Visible;
                ImportSummary.Text = "文件未能读取，请重新选择。";
            }
        }
        finally
        {
            progressRefresh.Stop();
            try
            {
                await Task.Run(() => DownloadCache.Trim(storage));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
            operationCancellation = null;
            if (IsWorkIndeterminate)
            {
                PresentProgress(displayedProgress, indeterminate: false, immediate: true);
            }
            RefreshPresentation();
            if (closeAfterOperation)
            {
                Close();
            }
        }
    }

    private void UpdateProgress(WorkSnapshot snapshot)
    {
        var stageChanged = lastStage != snapshot.Stage;
        if (stageChanged)
        {
            lastStage = snapshot.Stage;
            displayedProgress = 0;
            TaskTitle.Text = snapshot.Stage;
            WorkStripTitle.Text = snapshot.Stage;
            AddHistory(snapshot.Stage);
        }
        if (TaskDetail.Text != snapshot.Detail)
        {
            TaskDetail.Text = snapshot.Detail;
        }
        var ratio = snapshot.TotalBytes > 0 ? (double)snapshot.CompletedBytes / snapshot.TotalBytes :
            snapshot.TotalFiles > 0 ? (double)snapshot.CompletedFiles / snapshot.TotalFiles : 0;
        if (!snapshot.Indeterminate)
        {
            displayedProgress = Math.Max(displayedProgress, Math.Clamp(ratio * 100, 0, 99));
        }
        PresentProgress(displayedProgress, snapshot.Indeterminate, immediate: stageChanged && displayedProgress < WorkProgressValue);
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(speedSampleTime, now).TotalSeconds;
        if (snapshot.NetworkBytes < speedSampleBytes)
        {
            measuredSpeed = 0;
            speedSampleBytes = snapshot.NetworkBytes;
            speedSampleTime = now;
        }
        else if (elapsed >= 0.75)
        {
            measuredSpeed = (snapshot.NetworkBytes - speedSampleBytes) / elapsed;
            speedSampleBytes = snapshot.NetworkBytes;
            speedSampleTime = now;
        }
        var bytes = snapshot.TotalBytes > 0 ? $"{FormatBytes(snapshot.CompletedBytes)} / {FormatBytes(snapshot.TotalBytes)}" : "";
        var files = snapshot.TotalFiles > 0 ? $"{snapshot.CompletedFiles} / {snapshot.TotalFiles} 文件" : "";
        var speed = measuredSpeed > 0 ? FormatBytes((long)measuredSpeed) + "/s" : "";
        var cache = snapshot.CacheHits > 0 ? $"缓存命中 {snapshot.CacheHits}" : "";
        var statistics = string.Join("  ·  ", new[] { bytes, files, speed, cache }.Where(value => value.Length > 0));
        if (TaskStatistics.Text != statistics)
        {
            TaskStatistics.Text = statistics;
        }
    }

    private void PresentProgress(double value, bool indeterminate, bool immediate = false)
    {
        if (IsWorkIndeterminate != indeterminate)
        {
            SetCurrentValue(IsWorkIndeterminateProperty, indeterminate);
        }
        if (!immediate && (double)GetAnimationBaseValue(WorkProgressValueProperty) == value)
        {
            return;
        }
        var current = WorkProgressValue;
        SetCurrentValue(WorkProgressValueProperty, value);
        var animate = !immediate && !indeterminate && IsVisible && !LauncherMotion.GetReducedMotion(this) && value > current;
        BeginAnimation(WorkProgressValueProperty, animate ? new DoubleAnimation(current, value, TimeSpan.FromMilliseconds(180))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        } : null);
    }

    private void LoadSettings()
    {
        loadingSettings = true;
        var settings = storage.Settings;
        JavaPathText.Text = settings.JavaPath;
        JavaRuntimeCombo.ItemsSource = new[] { new JavaChoice(null) }.Concat(javaRuntimes.Select(runtime => new JavaChoice(runtime))).ToArray();
        JavaRuntimeCombo.SelectedIndex = settings.AutomaticJavaSelection ? 0 :
            Math.Max(0, javaRuntimes.ToList().FindIndex(runtime => JavaRuntimeCatalog.SameExecutable(runtime.ExecutablePath, settings.JavaPath)) + 1);
        MaximumMemoryText.Text = (settings.MaximumMemoryMb / 1024.0).ToString("0.0", CultureInfo.InvariantCulture);
        AutomaticMemoryRadio.IsChecked = settings.AutomaticMemoryAllocation;
        CustomMemoryRadio.IsChecked = !settings.AutomaticMemoryAllocation;
        WindowWidthText.Text = settings.WindowWidth.ToString(CultureInfo.InvariantCulture);
        WindowHeightText.Text = settings.WindowHeight.ToString(CultureInfo.InvariantCulture);
        FullscreenCheck.IsChecked = settings.Fullscreen;
        MinimizeOnLaunchCheck.IsChecked = settings.MinimizeOnLaunch;
        JvmArgumentsText.Text = JvmArgumentPolicy.GetEditableText(settings);
        GameArgumentsText.Text = settings.GameArguments;
        ConcurrencyCombo.SelectedItem = settings.DownloadConcurrency;
        HostConcurrencyCombo.SelectedItem = settings.PerHostConnections;
        ManualDownloadConnectionsCheck.IsChecked = !settings.AutomaticDownloadConnections;
        ManualDownloadConnectionsPanel.IsEnabled = !settings.AutomaticDownloadConnections;
        DownloadLimitText.Text = settings.DownloadLimitMiBPerSecond.ToString("0.###", CultureInfo.InvariantCulture);
        ProxyText.Text = settings.ProxyAddress;
        InjectorPathText.Text = settings.AuthlibInjectorPath;
        GameDirectoryText.Text = storage.GameDirectory;
        ReducedMotionCheck.IsChecked = settings.ReducedMotion;
        OptionalModsCheck.IsChecked = settings.IncludeOptionalMods;
        AutomaticUpdatesCheck.IsChecked = settings.CheckUpdatesAtStartup;
        ExternalServerText.Text = settings.AuthenticationServerAddress;
        ExternalServerNameText.Text = settings.AuthenticationServerName;
        ExternalRegistrationText.Text = settings.AuthenticationRegistrationUrl;
        JoinServerText.Text = settings.JoinServerAddress;
        AuthenticationPresetCombo.SelectedIndex = AuthenticationServer.IsLittleSkin(settings.AuthenticationServerAddress) ? 0 : 1;
        loadingSettings = false;
        UpdateJavaDescription();
        RefreshMemoryStatus();
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
        {
            return;
        }
        try
        {
            var settings = JsonSerializer.Deserialize<LauncherSettings>(JsonSerializer.Serialize(storage.Settings, LauncherStorage.JsonOptions), LauncherStorage.JsonOptions)!;
            settings.JavaPath = JavaPathText.Text.Trim().Trim('"');
            settings.AutomaticJavaSelection = JavaRuntimeCombo.SelectedItem is not JavaChoice { Runtime: not null };
            settings.AutomaticMemoryAllocation = AutomaticMemoryRadio.IsChecked == true;
            settings.MaximumMemoryMb = ParseMemoryGiB(MaximumMemoryText.Text);
            settings.WindowWidth = ParseInteger(WindowWidthText.Text, "窗口宽度");
            settings.WindowHeight = ParseInteger(WindowHeightText.Text, "窗口高度");
            settings.Fullscreen = FullscreenCheck.IsChecked == true;
            settings.MinimizeOnLaunch = MinimizeOnLaunchCheck.IsChecked == true;
            settings.JvmArguments = JvmArgumentsText.Text.Trim();
            settings.JvmArgumentsAreComplete = true;
            settings.GameArguments = GameArgumentsText.Text.Trim();
            JvmArgumentPolicy.Build(settings);
            settings.AutomaticDownloadConnections = ManualDownloadConnectionsCheck.IsChecked != true;
            if (!double.TryParse(DownloadLimitText.Text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var downloadLimit))
            {
                throw new InvalidOperationException("下载总限速请填写 MiB/s 数值，0 表示不限速。");
            }
            settings.DownloadLimitMiBPerSecond = downloadLimit;
            settings.DownloadConcurrency = ConcurrencyCombo.SelectedItem is int concurrency ? concurrency : 8;
            settings.PerHostConnections = HostConcurrencyCombo.SelectedItem is int hostConcurrency ? hostConcurrency : 4;
            settings.ProxyAddress = ProxyText.Text.Trim();
            settings.AuthlibInjectorPath = InjectorPathText.Text.Trim().Trim('"');
            settings.IncludeOptionalMods = OptionalModsCheck.IsChecked == true;
            settings.ReducedMotion = ReducedMotionCheck.IsChecked == true;
            settings.CheckUpdatesAtStartup = AutomaticUpdatesCheck.IsChecked == true;
            storage.SaveSettings(settings);
            RefreshPresentation();
            SetStatus("设置已保存，将用于下一次任务。");
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, true);
        }
    }

    private void ResetJvmArguments_Click(object sender, RoutedEventArgs e)
    {
        JvmArgumentsText.Text = JvmArgumentPolicy.RecommendedText;
    }

    private void ResetWindowSettings_Click(object sender, RoutedEventArgs e)
    {
        WindowWidthText.Text = LauncherSettings.DefaultWindowWidth.ToString(CultureInfo.InvariantCulture);
        WindowHeightText.Text = LauncherSettings.DefaultWindowHeight.ToString(CultureInfo.InvariantCulture);
        FullscreenCheck.IsChecked = LauncherSettings.DefaultFullscreen;
        SetStatus("已恢复窗口默认值，保存后生效。");
    }

    private void DownloadConnections_Changed(object sender, RoutedEventArgs e)
    {
        if (!initialized || loadingSettings)
        {
            return;
        }
        ManualDownloadConnectionsPanel.IsEnabled = ManualDownloadConnectionsCheck.IsChecked == true;
    }

    private void MaximumMemory_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!initialized || changingMemory || loadingSettings || !TryReadMemoryGiB(out var value))
        {
            return;
        }
        changingMemory = true;
        MemorySlider.Value = Math.Clamp(value, MemorySlider.Minimum, MemorySlider.Maximum);
        changingMemory = false;
        UpdateMemoryPresentation();
    }

    private void MemorySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!initialized || changingMemory || loadingSettings)
        {
            return;
        }
        changingMemory = true;
        MaximumMemoryText.Text = e.NewValue.ToString("0.0", CultureInfo.InvariantCulture);
        changingMemory = false;
        UpdateMemoryPresentation();
    }

    private async void ChooseJava_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择 Java 程序", Filter = "Java 程序 (javaw.exe;java.exe)|javaw.exe;java.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true)
        {
            JavaPathText.Text = dialog.FileName;
            await ScanJavaAsync(true, dialog.FileName);
        }
    }

    private void MemoryMode_Checked(object sender, RoutedEventArgs e)
    {
        if (initialized && !loadingSettings)
        {
            RefreshMemoryStatus();
        }
    }

    private void MemoryPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string value })
        {
            changingMemory = true;
            CustomMemoryRadio.IsChecked = true;
            MaximumMemoryText.Text = double.Parse(value, CultureInfo.InvariantCulture).ToString("0.0", CultureInfo.InvariantCulture);
            changingMemory = false;
            UpdateMemoryPresentation();
        }
    }

    private void RefreshMemoryStatus()
    {
        if (!initialized || closed || WindowState == WindowState.Minimized)
        {
            return;
        }
        try
        {
            physicalMemory = MemoryAllocation.ReadPhysicalMemory();
            HomeMemory.Text = $"{MemoryAllocation.MaximumMb(storage.Settings, physicalMemory) / 1024.0:0.0} GiB";
            UpdateMemoryPresentation();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            PhysicalMemoryText.Text = "暂时无法读取系统内存";
        }
    }

    private void UpdateMemoryPresentation()
    {
        if (physicalMemory == null || changingMemory || loadingSettings)
        {
            return;
        }
        var automatic = AutomaticMemoryRadio.IsChecked == true;
        var assigned = automatic ? MemoryAllocation.RecommendedMaximumMb(physicalMemory) :
            TryReadMemoryGiB(out var value) ? (int)Math.Round(value * 1024) : storage.Settings.MaximumMemoryMb;
        changingMemory = true;
        var sliderMaximum = Math.Max(8, Math.Min(128, Math.Max(Math.Floor(physicalMemory.TotalGiB * 2) / 2, assigned / 1024.0)));
        if (MemorySlider.Maximum != sliderMaximum)
        {
            MemorySlider.Maximum = sliderMaximum;
        }
        MemorySlider.IsEnabled = !automatic;
        MaximumMemoryText.IsReadOnly = automatic;
        if (automatic)
        {
            var memoryText = (assigned / 1024.0).ToString("0.0", CultureInfo.InvariantCulture);
            if (MaximumMemoryText.Text != memoryText)
            {
                MaximumMemoryText.Text = memoryText;
            }
        }
        var sliderValue = Math.Clamp(assigned / 1024.0, MemorySlider.Minimum, MemorySlider.Maximum);
        if (MemorySlider.Value != sliderValue)
        {
            MemorySlider.Value = sliderValue;
        }
        changingMemory = false;
        var budget = new MemoryBudget(physicalMemory, assigned);
        PhysicalMemoryText.Text = $"{physicalMemory.UsedGiB:0.0} / {physicalMemory.TotalGiB:0.0} GiB";
        GameBudgetText.Text = $"{budget.GameGiB:0.0} GiB";
        MemoryReserveText.Text = $"{budget.RemainingGiB:0.0} GiB";
        MemoryHint.Text = budget.Description;
        PhysicalMemoryMeter.Update(budget, LauncherMotion.GetReducedMotion(this));
    }

    private bool TryReadMemoryGiB(out double value)
    {
        return double.TryParse(MaximumMemoryText.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) &&
            double.IsFinite(value) && value >= 1 && value <= 128;
    }

    private static int ParseMemoryGiB(string value)
    {
        if (!double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var gib) ||
            !double.IsFinite(gib) || gib < 1 || gib > 128)
        {
            throw new InvalidOperationException("游戏内存上限应为 1–128 GiB。");
        }
        return (int)Math.Round(gib * 1024);
    }

    private async Task ScanJavaAsync(bool refresh, string? explicitPath = null)
    {
        if (closed || scanningJava)
        {
            return;
        }
        scanningJava = true;
        RefreshPresentation();
        RescanJavaButton.IsEnabled = false;
        JavaStatusText.Text = "正在扫描本机 Java…";
        var previousChoice = JavaRuntimeCombo.SelectedItem as JavaChoice;
        var requestedPath = explicitPath ?? previousChoice?.Runtime?.ExecutablePath ?? storage.Settings.JavaPath;
        try
        {
            var runtimes = await javaCatalog.DiscoverAsync(requestedPath, refresh, windowLifetime.Token);
            if (closed)
            {
                return;
            }
            javaRuntimes = runtimes;
            var automatic = explicitPath == null && (previousChoice == null ? storage.Settings.AutomaticJavaSelection : previousChoice.Runtime == null);
            loadingSettings = true;
            var choices = new[] { new JavaChoice(null) }.Concat(runtimes.Select(runtime => new JavaChoice(runtime))).ToArray();
            JavaRuntimeCombo.ItemsSource = choices;
            JavaRuntimeCombo.SelectedItem = automatic ? choices[0] : choices.FirstOrDefault(choice => choice.Runtime != null &&
                JavaRuntimeCatalog.SameExecutable(choice.Runtime.ExecutablePath, requestedPath)) ?? choices[0];
            loadingSettings = false;
            UpdateJavaDescription();
            RefreshPresentation();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!closed)
            {
                JavaStatusText.Text = "Java 扫描未完成，可以手动选择 Java 程序。";
            }
        }
        finally
        {
            scanningJava = false;
            if (!closed)
            {
                RefreshPresentation();
            }
        }
    }

    private JavaRuntime? SelectedJavaRuntime(bool automatic, string path)
    {
        return automatic ? javaRuntimes.FirstOrDefault() :
            javaRuntimes.FirstOrDefault(runtime => JavaRuntimeCatalog.SameExecutable(runtime.ExecutablePath, path));
    }

    private void UpdateJavaDescription()
    {
        var choice = JavaRuntimeCombo.SelectedItem as JavaChoice;
        var runtime = choice?.Runtime ?? javaRuntimes.FirstOrDefault();
        JavaPathText.Text = runtime?.ExecutablePath ?? "";
        JavaStatusText.Foreground = (Brush)FindResource("MutedBrush");
        if (runtime == null)
        {
            JavaStatusText.Text = "未发现本机 Java，补全环境时会获取游戏所需的 Java 17，也可手动选择。";
        }
        else if (runtime.MajorVersion != 17)
        {
            JavaStatusText.Text = $"{(javaRuntimes.All(item => item.MajorVersion != 17) ? "未找到 Java 17，已选用" : "当前选用")} Java {runtime.MajorVersion}。本整合包推荐 Java 17，遇到兼容问题时请切换。";
            JavaStatusText.Foreground = new SolidColorBrush(Color.FromRgb(229, 179, 129));
        }
        else
        {
            JavaStatusText.Text = $"已选用 Java 17 · {(runtime.Is64Bit ? "64 位" : "32 位")} · {runtime.Vendor}。共发现 {javaRuntimes.Count} 个本机 Java。";
        }
        if (runtime is { Is64Bit: false })
        {
            JavaStatusText.Text += " 当前为 32 位 Java，请安装 64 位 Java 17 以使用较大内存。";
            JavaStatusText.Foreground = new SolidColorBrush(Color.FromRgb(229, 179, 129));
        }
    }

    private void JavaRuntime_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initialized && !loadingSettings)
        {
            UpdateJavaDescription();
        }
    }

    private async void RescanJava_Click(object sender, RoutedEventArgs e)
    {
        await ScanJavaAsync(true);
    }

    private void AuthenticationPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || loadingSettings)
        {
            return;
        }
        if (AuthenticationPresetCombo.SelectedIndex == 0)
        {
            ApplyLittleSkin();
        }
        else if (AuthenticationServer.IsLittleSkin(ExternalServerText.Text))
        {
            ExternalServerText.Clear();
            ExternalServerNameText.Clear();
            ExternalRegistrationText.Clear();
        }
    }

    private void ApplyLittleSkin()
    {
        ExternalServerText.Text = AuthenticationServer.LittleSkinAddress;
        ExternalServerNameText.Text = "LittleSkin";
        ExternalRegistrationText.Text = AuthenticationServer.LittleSkinRegistration;
    }

    private void ExternalAddress_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (initialized && !loadingSettings)
        {
            ExternalPasswordBox.Clear();
        }
    }

    private void LittleSkin_Click(object sender, RoutedEventArgs e)
    {
        AuthenticationPresetCombo.SelectedIndex = 0;
        ApplyLittleSkin();
    }

    private void SaveAuthenticationSettings()
    {
        var settings = JsonSerializer.Deserialize<LauncherSettings>(JsonSerializer.Serialize(storage.Settings, LauncherStorage.JsonOptions), LauncherStorage.JsonOptions)!;
        settings.AuthenticationServerAddress = AccountService.NormalizeServer(ExternalServerText.Text).AbsoluteUri;
        settings.AuthenticationServerName = ExternalServerNameText.Text.Trim();
        settings.AuthenticationRegistrationUrl = ExternalRegistrationText.Text.Trim();
        settings.JoinServerAddress = JoinServerText.Text.Trim();
        storage.SaveSettings(settings);
    }

    private void SaveAuthentication_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
        {
            return;
        }
        try
        {
            SaveAuthenticationSettings();
            SetStatus("认证服务器设置已保存。");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            SetStatus(exception.Message, true);
        }
    }

    private void RegisterExternal_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var registration = AuthenticationServer.ValidateRegistrationUrl(ExternalRegistrationText.Text);
            if (registration == null)
            {
                SetStatus("请先填写认证站提供的注册链接。");
                return;
            }
            Process.Start(new ProcessStartInfo(registration.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            SetStatus(exception.Message, true);
        }
    }

    private void ChooseInjector_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择 authlib-injector", Filter = "Java 组件 (*.jar)|*.jar", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true)
        {
            InjectorPathText.Text = dialog.FileName;
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !IsBusy && storage.Installation == null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (IsBusy || storage.Installation != null || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } files)
        {
            return;
        }
        if (!new[] { ".zip", ".mrpack" }.Contains(Path.GetExtension(files[0]), StringComparer.OrdinalIgnoreCase))
        {
            SetStatus("请选择 .mrpack 或整合包 ZIP 文件。", true);
            return;
        }
        ShowImport();
        await InspectArchiveAsync(files[0]);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ImportOverlay.Visibility == Visibility.Visible && !IsBusy)
        {
            HideImportDialog();
            e.Handled = true;
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (IsBusy)
        {
            e.Cancel = true;
            closeAfterOperation = true;
            operationCancellation?.Cancel();
            SetStatus("正在结束任务并保存可恢复的下载数据。");
        }
    }

    private void AddHistory(string message)
    {
        history.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        if (history.Count > 200)
        {
            history.RemoveAt(0);
        }
        TaskHistory.Text = string.Join(Environment.NewLine, history);
        TaskHistory.ScrollToEnd();
    }

    private void SetStatus(string message, bool error = false)
    {
        StatusText.Text = message;
        StatusText.ToolTip = message;
        StatusText.Foreground = new SolidColorBrush(error ? Color.FromRgb(239, 166, 189) : Color.FromRgb(167, 146, 182));
    }

    private void OpenDirectory(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                SetStatus("目录尚未创建，导入整合包后即可打开。");
                return;
            }
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, true);
        }
    }

    private static int ParseInteger(string value, string label)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number :
            throw new InvalidOperationException(label + "应为整数。");
    }

    private static void ValidateVersionFields(string minecraft, string forge)
    {
        if (!Regex.IsMatch(minecraft, "^[A-Za-z0-9_.-]{1,100}$") ||
            !string.IsNullOrEmpty(forge) && !Regex.IsMatch(forge, "^[0-9]+(\\.[0-9]+){1,5}$"))
        {
            throw new InvalidOperationException("请填写有效的 Minecraft 与 Forge 版本号。");
        }
    }

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.00} GiB",
            >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MiB",
            >= 1024 => $"{bytes / 1024.0:0.0} KiB",
            _ => $"{bytes} B"
        };
    }

    private void CloseImport_Click(object sender, RoutedEventArgs e)
    {
        if (!IsBusy)
        {
            HideImportDialog();
        }
    }

    private void OpenImportDialog()
    {
        ImportOverlay.Visibility = Visibility.Visible;
        LauncherMotion.Enter(ImportDialog, LauncherMotion.GetReducedMotion(this));
    }

    private void HideImportDialog()
    {
        ImportOverlay.Visibility = Visibility.Collapsed;
        LauncherMotion.Reset(ImportDialog);
    }

    private void CancelTask_Click(object sender, RoutedEventArgs e)
    {
        if (operationCancellation is { IsCancellationRequested: false } cancellation)
        {
            TaskCancelButton.IsEnabled = StripCancelButton.IsEnabled = false;
            SetStatus("正在取消任务。");
            cancellation.Cancel();
        }
    }

    private void HomeInstance_Click(object sender, RoutedEventArgs e)
    {
        if (storage.Installation == null)
        {
            ShowImport();
        }
        else
        {
            ShowPage("instances");
        }
    }

    private async void Accounts_Click(object sender, RoutedEventArgs e)
    {
        ShowPage("accounts");
        if (storage.Accounts.Count == 0)
        {
            await OpenMicrosoftLoginAsync();
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        ShowPage("settings");
    }

    private void OpenInstance_Click(object sender, RoutedEventArgs e)
    {
        OpenDirectory(storage.Installation?.DirectoryPath ?? storage.GameDirectory);
    }

    private void OpenLauncherDirectory_Click(object sender, RoutedEventArgs e)
    {
        OpenDirectory(Path.GetDirectoryName(storage.Root)!);
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        OpenDirectory(storage.Logs);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
