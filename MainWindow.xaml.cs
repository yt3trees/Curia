using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Curia.Models;
using Curia.Services;
using Curia.ViewModels;
using Curia.Views;
using Curia.Views.Pages;
using System.Windows.Input;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfModifierKeys = System.Windows.Input.ModifierKeys;
using WpfKey = System.Windows.Input.Key;
using WpfKeyboard = System.Windows.Input.Keyboard;
using WpfApplication = System.Windows.Application;

namespace Curia;

public partial class MainWindow : FluentWindow
{
    private readonly IServiceProvider _serviceProvider;
    private readonly HotkeyService _hotkeyService;
    private readonly TrayService _trayService;
    private readonly MainWindowViewModel _viewModel;
    private readonly IPageService _pageService;
    private readonly IContentDialogService _contentDialogService;
    private readonly ConfigService _configService;
    private readonly CaptureService _captureService;
    private readonly StartupPreloadService _startupPreloadService;
    private readonly CancellationTokenSource _startupPreloadCancellation = new();
    private IntPtr _hwnd;
    private CaptureWindow? _activeCaptureWindow;
    private CommandPaletteWindow? _activeCommandPaletteWindow;

    // ウィンドウが画面上に表示されているかのフラグ (Hide() を使わない方式)
    private bool _isShownOnScreen = false;
    // 非表示にした時点のウィンドウ最大化状態
    private bool _wasMaximized = false;
    // DWMWA_CLOAK が設定中かどうか (StateChanged との競合を防ぐ)
    private bool _isCloaked = false;
    // OnFlashBlockerCollapseAndUncloak が待機すべき残りフレーム数
    private int _pendingUncloakFrames = 0;
    private bool _startupPreloadScheduled;

    public MainWindow(
        IServiceProvider serviceProvider,
        HotkeyService hotkeyService,
        TrayService trayService,
        MainWindowViewModel viewModel,
        IPageService pageService,
        IContentDialogService contentDialogService,
        ConfigService configService,
        CaptureService captureService,
        StartupPreloadService startupPreloadService)
    {
        _serviceProvider = serviceProvider;
        _hotkeyService = hotkeyService;
        _trayService = trayService;
        _viewModel = viewModel;
        _pageService = pageService;
        _contentDialogService = contentDialogService;
        _configService = configService;
        _captureService = captureService;
        _startupPreloadService = startupPreloadService;

        DataContext = _viewModel;
        InitializeComponent();

        Loaded += OnLoaded;
        Closing += OnClosing;
        KeyDown += OnKeyDown;

        // Proposal Inbox の件数変化をトレイバッジとバルーンに反映する
        WeakReferenceMessenger.Default.Register<ProposalInboxChangedMessage>(this, (_, msg) =>
        {
            Dispatcher.Invoke(() =>
            {
                _trayService.UpdateBadge(msg.PendingCount);
                if (msg.AddedItem is { } added && _configService.LoadSettings().ProposalBalloonEnabled)
                    _trayService.ShowBalloonTip("New AI proposal", $"{added.ProjectName}: {added.SummaryFirstLine}");
            });
        });
        // 最大化ボタン (wpf-ui TitleBar) を横取りして CloakAndMaximize 経由にする
        CommandBindings.Add(new CommandBinding(
            SystemCommands.MaximizeWindowCommand,
            (_, _) => { if (!_isCloaked) CloakAndMaximize(); }));
    }

    // ── Win32: DWM 補助設定 ───────────────────────────────────────────────
    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
    private static extern IntPtr SetClassLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetClassLongW")]
    private static extern uint SetClassLong32(IntPtr hWnd, int nIndex, uint dwNewLong);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    private const int GCLP_HBRBACKGROUND = -10;
    private const int WM_ERASEBKGND = 0x0014;
    private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    private const int DWMWA_BORDER_COLOR = 34;  // Windows 11 22000+
    private const int DWMWA_CLOAK = 13;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(_hwnd);
        if (source?.CompositionTarget != null)
            source.CompositionTarget.BackgroundColor = System.Windows.Media.Color.FromRgb(0x0d, 0x11, 0x17);
        source?.AddHook(WndProc);
        if (IntPtr.Size == 8)
            SetClassLongPtr64(_hwnd, GCLP_HBRBACKGROUND, IntPtr.Zero);
        else
            SetClassLong32(_hwnd, GCLP_HBRBACKGROUND, 0);
        var disabled = 1;
        DwmSetWindowAttribute(_hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref disabled, sizeof(int));
        // ウィンドウ枠にうっすらグレー (#30363D) を設定する (Windows 11+)
        // 背景が暗い場合に境界線をわかりやすくするため
        // COLORREF 形式: 0x00BBGGRR = B=0x3D, G=0x36, R=0x30
        var borderColor = 0x003D3630;
        DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
    }

    private const int WM_SYSCOMMAND  = 0x0112;
    private const int SC_MAXIMIZE    = 0xF030;
    private const int WM_SIZE        = 0x0005;
    private const int SIZE_MAXIMIZED = 2;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_ERASEBKGND)
        {
            handled = true;
            return new IntPtr(1);
        }

        // キーボード・システムメニュー経由の最大化を横取りする。
        // handled=true でデフォルト処理をブロックし、CloakAndMaximize で自分で最大化する。
        if (msg == WM_SYSCOMMAND && ((int)wParam & 0xFFF0) == SC_MAXIMIZE && !_isCloaked)
        {
            handled = true;
            CloakAndMaximize();
        }
        // フォールバック: 上記で捕捉できなかったパス (Aero Snap 等)
        else if (msg == WM_SIZE && (int)wParam == SIZE_MAXIMIZED && !_isCloaked)
        {
            _isCloaked = true;
            var cloak = 1;
            DwmSetWindowAttribute(_hwnd, DWMWA_CLOAK, ref cloak, sizeof(int));
            _pendingUncloakFrames = 2;
            System.Windows.Media.CompositionTarget.Rendering -= OnFlashBlockerCollapseAndUncloak;
            System.Windows.Media.CompositionTarget.Rendering += OnFlashBlockerCollapseAndUncloak;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// DWM クロークを確実に設定してから最大化する。
    /// DwmFlush() で「クローク済み」が DWM に届いてからリサイズすることで
    /// 最大化トランジション中の 1 フレームちらつきを防ぐ。
    /// </summary>
    private void CloakAndMaximize()
    {
        _isCloaked = true;
        var cloak = 1;
        DwmSetWindowAttribute(_hwnd, DWMWA_CLOAK, ref cloak, sizeof(int));
        DwmFlush(); // リサイズ前に DWM がクロークを処理するまで同期待機
        WindowState = WindowState.Maximized;
        _pendingUncloakFrames = 2;
        System.Windows.Media.CompositionTarget.Rendering -= OnFlashBlockerCollapseAndUncloak;
        System.Windows.Media.CompositionTarget.Rendering += OnFlashBlockerCollapseAndUncloak;
    }

    // ── フラッシュ防止: 画面外移動方式 ───────────────────────────────────
    //
    // 方針:
    //   - Hide()/Show() を一切使わない。Hide() は DWM のサーフェスを再初期化するため。
    //   - 代わりにウィンドウを画面外 (-32000, -32000) に移動して「隠す」。
    //   - サーフェスは常に生き続けるので Show() 時の DWM 再初期化フラッシュが起きない。
    //   - FlashBlocker (WPF Grid) でコンテンツを覆い、ウィンドウ移動中の描画を隠す。
    //   - 起動時は App.xaml.cs で画面外位置を設定し、OnLoaded 後に画面中央へ移動する。

    private const double OffScreenX = -32000;
    private const double OffScreenY = -32000;

    /// <summary>
    /// DWMWA_CLOAK でウィンドウを DWM レベルで不可視にし、状態・位置を変更してから画面外に置く。
    /// DWM サーフェスは維持されるため、次回表示時に再初期化フラッシュが起きない。
    /// </summary>
    private void MoveOffScreen()
    {
        // 最大化状態を記憶しておき、次回表示時に復元する
        _wasMaximized = WindowState == WindowState.Maximized;

        // 最大化状態では Left/Top が最大化位置 (通常 0,0) になるため保存しない
        if (_isShownOnScreen && WindowState == WindowState.Normal)
        {
            _configService.SaveWindowPlacement(new Models.WindowPlacement
            {
                Left = Left,
                Top = Top,
                Width = 0,   // サイズは保存しない (再起動後は固定値に戻す)
                Height = 0,
            });
        }

        // クローク: DWM レベルで不可視にする (DWM サーフェスは維持)
        _isCloaked = true;
        var cloak = 1;
        DwmSetWindowAttribute(_hwnd, DWMWA_CLOAK, ref cloak, sizeof(int));

        _isShownOnScreen = false;
        if (WindowState != WindowState.Normal)
            WindowState = WindowState.Normal;
        Left = OffScreenX;
        Top = OffScreenY;

        // アンクローク: ウィンドウは画面外なので視覚的影響なし
        _isCloaked = false;
        var uncloak = 0;
        DwmSetWindowAttribute(_hwnd, DWMWA_CLOAK, ref uncloak, sizeof(int));
    }

    /// <summary>
    /// ウィンドウを画面に表示する。DWMWA_CLOAK で遷移中のちらつきを防ぐ。
    /// </summary>
    private void MoveOnScreen()
    {
        // クローク: 位置・状態変更の途中経過がユーザーに見えないようにする
        _isCloaked = true;
        var cloak = 1;
        DwmSetWindowAttribute(_hwnd, DWMWA_CLOAK, ref cloak, sizeof(int));

        _isShownOnScreen = true;

        if (_wasMaximized)
        {
            WindowState = WindowState.Maximized;
            // 最大化時は DWM が新しいサイズでの描画を完了するまで 2 フレーム待つ
            _pendingUncloakFrames = 2;
        }
        else
        {
            var workArea = SystemParameters.WorkArea;
            var saved = _configService.LoadWindowPlacement();

            // サイズは再起動後も固定値に戻す (XAML の Width/Height をそのまま使う)
            // ウィンドウが作業領域より大きい場合はサイズを縮小する (MinWidth/MinHeight は守る)
            if (Width > workArea.Width)
                Width = Math.Max(MinWidth, workArea.Width);
            if (Height > workArea.Height)
                Height = Math.Max(MinHeight, workArea.Height);

            if (saved != null)
            {
                // 保存された位置が作業領域内に収まるよう調整する
                Left = Math.Max(workArea.Left, Math.Min(saved.Left, workArea.Right - Width));
                Top = Math.Max(workArea.Top, Math.Min(saved.Top, workArea.Bottom - Height));
            }
            else
            {
                Left = workArea.Left + (workArea.Width - Width) / 2;
                Top = workArea.Top + (workArea.Height - Height) / 2;
            }
            WindowState = WindowState.Normal;
            _pendingUncloakFrames = 1;
        }

        Activate();
        Focus();

        // WPF レイアウト完了後にアンクロークする
        System.Windows.Media.CompositionTarget.Rendering -= OnFlashBlockerCollapseAndUncloak;
        System.Windows.Media.CompositionTarget.Rendering += OnFlashBlockerCollapseAndUncloak;
    }

    private void OnFlashBlockerCollapseAndUncloak(object? sender, EventArgs e)
    {
        if (--_pendingUncloakFrames > 0) return;
        System.Windows.Media.CompositionTarget.Rendering -= OnFlashBlockerCollapseAndUncloak;
        FlashBlocker.Visibility = Visibility.Collapsed;
        _isCloaked = false;
        var uncloak = 0;
        DwmSetWindowAttribute(_hwnd, DWMWA_CLOAK, ref uncloak, sizeof(int));
    }

    // ──────────────────────────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // wpf-ui ContentDialogService に親要素を設定
        _contentDialogService.SetDialogHost(RootContentDialogPresenter);

        // wpf-ui NavigationView に PageService を設定 (DI対応ページ生成)
        RootNavigation.SetPageService(_pageService);

        // ページ遷移時にステータスバーの表示/非表示を切り替える
        RootNavigation.Navigated += (_, args) =>
        {
            _viewModel.IsEditorActive = args.Page is EditorPage;
        };

        // ホットキー登録
        _hotkeyService.OnActivated = ToggleVisibility;
        _hotkeyService.OnCaptureActivated = ShowCaptureWindow;
        _hotkeyService.OnCommandPaletteActivated = ShowCommandPaletteWindow;
        _hotkeyService.Register(this);

        // トレイアイコン初期化
        _trayService.OnActivated = ToggleVisibility;
        _trayService.OnCaptureActivated = ShowCaptureWindow;
        _trayService.OnProposalsActivated = () =>
        {
            BringToFront();
            RootNavigation.Navigate(typeof(DashboardPage));
        };
        _trayService.Initialize(this);
        _trayService.UpdateBadge(_serviceProvider.GetRequiredService<ProposalInboxService>().PendingCount);

        // ウィンドウアイコンをトレイアイコン (ダイヤモンド) と統一
        if (_trayService.DiamondBitmapSource != null)
        {
            this.Icon = _trayService.DiamondBitmapSource;
            RootTitleBar.Icon = new Wpf.Ui.Controls.ImageIcon
            {
                Source = _trayService.DiamondBitmapSource,
                Width = 14,
                Height = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -2, 0, 0)
            };
        }
        _trayService.UpdateHotkeyDisplay(_hotkeyService.HotkeyDisplayText);

        // デフォルトページを DashboardPage に設定
        RootNavigation.Navigate(typeof(DashboardPage));

        // コマンドパレットのコマンド一覧をバックグラウンドで事前構築 (初回起動を即時化)
        _ = _viewModel.CommandPaletteViewModel.PreBuildAsync();

        // 起動時: App.xaml.cs で画面外位置に設定済み。
        // OnLoaded で WPF が描画完了後、ウィンドウを画面中央に移動して表示する。
        MoveOnScreen();

        if (!_startupPreloadScheduled)
        {
            _startupPreloadScheduled = true;
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ContextIdle,
                new Action(StartStartupPreload));
        }
    }

    private async void StartStartupPreload()
    {
        try
        {
            await _startupPreloadService.PreloadAsync(_startupPreloadCancellation.Token);
        }
        catch (OperationCanceledException) when (_startupPreloadCancellation.IsCancellationRequested)
        {
            // Application shutdown cancels pending preload work.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StartupPreload] Unexpected failure: {ex}");
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (WpfKeyboard.Modifiers.HasFlag(WpfModifierKeys.Shift))
        {
            _startupPreloadCancellation.Cancel();
            _hotkeyService.Unregister();
            _trayService.Dispose();
            WpfApplication.Current.Shutdown();
        }
        else
        {
            e.Cancel = true;
            MoveOffScreen();
        }
    }

    private void OnKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == WpfKey.Escape)
        {
            MoveOffScreen();
            e.Handled = true;
            return;
        }

        if (WpfKeyboard.Modifiers == WpfModifierKeys.Control)
        {
            switch (e.Key)
            {
                case WpfKey.D1:
                    RootNavigation.Navigate(typeof(DashboardPage));
                    e.Handled = true;
                    break;
                case WpfKey.D2:
                    RootNavigation.Navigate(typeof(EditorPage));
                    e.Handled = true;
                    break;
                case WpfKey.D3:
                    RootNavigation.Navigate(typeof(WeeklySchedulePage));
                    e.Handled = true;
                    break;
                case WpfKey.D4:
                    RootNavigation.Navigate(typeof(TimelinePage));
                    e.Handled = true;
                    break;
                case WpfKey.D5:
                    RootNavigation.Navigate(typeof(WikiPage));
                    e.Handled = true;
                    break;
                case WpfKey.D6:
                    RootNavigation.Navigate(typeof(GitReposPage));
                    e.Handled = true;
                    break;
                case WpfKey.D7:
                    RootNavigation.Navigate(typeof(AsanaSyncPage));
                    e.Handled = true;
                    break;
                case WpfKey.D8:
                    RootNavigation.Navigate(typeof(AgentHubPage));
                    e.Handled = true;
                    break;
                case WpfKey.D9:
                    RootNavigation.Navigate(typeof(SettingsPage));
                    e.Handled = true;
                    break;
                case WpfKey.K:
                    ShowCommandPaletteWindow();
                    e.Handled = true;
                    break;
            }
        }
    }

    public void ToggleVisibility()
    {
        if (_isShownOnScreen && IsActive)
        {
            // FlashBlocker を1フレーム描画してからウィンドウを画面外に移動
            MoveOffScreen();
        }
        else
        {
            // ウィンドウを画面中央に移動し、次フレームで FlashBlocker を非表示
            MoveOnScreen();
        }
    }

    /// <summary>
    /// アプリ本体を前面に表示する（非表示の場合は表示、既に表示中の場合はアクティブ化のみ）。
    /// </summary>
    public void BringToFront()
    {
        if (!_isShownOnScreen)
            MoveOnScreen();
        else
        {
            Activate();
            Focus();
        }
    }

    /// <summary>
    /// DashboardPage からEditorPageへ遷移し、指定プロジェクトを選択する。
    /// </summary>
    public void NavigateToEditor(ProjectInfo project)
    {
        var editorVm = _serviceProvider.GetRequiredService<EditorViewModel>();
        var match = editorVm.Projects.FirstOrDefault(p => p.HiddenKey == project.HiddenKey);
        editorVm.SelectedProject = match ?? project;
        RootNavigation.Navigate(typeof(EditorPage));
    }

    public async Task NavigateToDashboardAndShowBriefingAsync(ProjectInfo project)
    {
        RootNavigation.Navigate(typeof(DashboardPage));
        await Dispatcher.InvokeAsync(async () =>
        {
            var dashboard = _serviceProvider.GetRequiredService<DashboardPage>();
            await dashboard.ShowBriefingForProjectAsync(project);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    public void NavigateToSettings()
    {
        RootNavigation.Navigate(typeof(SettingsPage));
    }

    /// <summary>
    /// DashboardPage から TimelinePage へ遷移し、指定プロジェクトを選択する。
    /// </summary>
    public void NavigateToTimeline(ProjectInfo project)
    {
        var timelineVm = _serviceProvider.GetRequiredService<TimelineViewModel>();
        timelineVm.NavigateToProjectKey = project.HiddenKey;
        RootNavigation.Navigate(typeof(TimelinePage));
    }

    /// <summary>
    /// TimelinePage からEditorPageへ遷移し、指定プロジェクト + ファイルを開く。
    /// </summary>
    public void NavigateToEditorAndOpenFile(ProjectInfo project, string filePath)
    {
        var editorVm = _serviceProvider.GetRequiredService<EditorViewModel>();
        var match = editorVm.Projects.FirstOrDefault(p => p.HiddenKey == project.HiddenKey);
        editorVm.NavigateToProjectAndOpenFile(match ?? project, filePath);
        RootNavigation.Navigate(typeof(EditorPage));
    }

    /// <summary>
    /// Quick Capture の decision ルーティング専用。
    /// Editor に遷移後、AI Decision Log フローを自動発火する。
    /// </summary>
    private void NavigateToEditorAndTriggerDecision(ProjectInfo project, string capturedText)
    {
        var editorVm = _serviceProvider.GetRequiredService<EditorViewModel>();
        var match = editorVm.Projects.FirstOrDefault(p => p.HiddenKey == project.HiddenKey);
        editorVm.SelectedProject = match ?? project;
        editorVm.RequestDecisionLogOnOpen(capturedText);
        RootNavigation.Navigate(typeof(EditorPage));
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
            new System.Action(async () => await editorVm.TriggerDecisionLogIfPendingAsync()));
    }

    /// <summary>
    /// Quick Capture の focus_update ルーティング専用。
    /// エディタを指定ファイルで開いた後、Update Focus LLM フローを自動発火する。
    /// </summary>
    private void NavigateToEditorAndTriggerFocusUpdate(ProjectInfo project, string filePath, string capturedText)
    {
        var editorVm = _serviceProvider.GetRequiredService<EditorViewModel>();
        var match = editorVm.Projects.FirstOrDefault(p => p.HiddenKey == project.HiddenKey);
        editorVm.RequestFocusUpdateOnOpen(capturedText);
        editorVm.NavigateToProjectAndOpenFile(match ?? project, filePath);
        RootNavigation.Navigate(typeof(EditorPage));
        // ファイル読み込み・LoadProjectsAsync が落ち着いてからトリガー (OpenFileAndSelectNodeAsync 側の
        // トリガーが何らかの理由でミスした場合のフォールバック)
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
            new System.Action(() => editorVm.TriggerFocusUpdateIfPending()));
    }

    /// <summary>
    /// SettingsPage からホットキー変更時にトレイアイコンの表示を更新する。
    /// </summary>
    public void UpdateTrayHotkeyDisplay(string displayText)
    {
        _trayService.UpdateHotkeyDisplay(displayText);
    }

    /// <summary>
    /// キャプチャホットキーが押されたときに CaptureWindow を表示する。
    /// </summary>
    private void ShowCaptureWindow()
    {
        if (_activeCaptureWindow != null)
        {
            _activeCaptureWindow.Activate();
            return;
        }

        var discoveryService = _serviceProvider.GetRequiredService<ProjectDiscoveryService>();
        var captureWindow = new CaptureWindow(_captureService, discoveryService, _configService);
        captureWindow.Owner = this;
        captureWindow.Closed += (_, _) => _activeCaptureWindow = null;
        _activeCaptureWindow = captureWindow;

        captureWindow.OnNavigateToFile = (projectName, filePath) =>
        {
            var project = ResolveProject(projectName);
            if (project != null)
                NavigateToEditorAndOpenFile(project, filePath);
            MoveOnScreen();
        };

        captureWindow.OnNavigateToFocusUpdate = (projectName, filePath, capturedText) =>
        {
            var project = ResolveProject(projectName);
            if (project != null)
                NavigateToEditorAndTriggerFocusUpdate(project, filePath, capturedText);
            MoveOnScreen();
        };

        captureWindow.OnNavigateToDecision = (projectName, capturedText) =>
        {
            var project = ResolveProject(projectName);
            if (project != null)
                NavigateToEditorAndTriggerDecision(project, capturedText);
            MoveOnScreen();
        };

        captureWindow.ShowDialog();
    }

    /// <summary>
    /// Command Palette ウィンドウを表示する。Ctrl+K および Ctrl+Shift+K グローバルホットキーから呼ばれる。
    /// </summary>
    public void ShowCommandPaletteWindow()
    {
        if (_activeCommandPaletteWindow != null)
        {
            _activeCommandPaletteWindow.Activate();
            return;
        }

        var vm = _viewModel.CommandPaletteViewModel;
        vm.Prepare();

        var window = new CommandPaletteWindow(vm, this);
        window.Closed += (_, _) => _activeCommandPaletteWindow = null;
        _activeCommandPaletteWindow = window;
        window.Show();
        window.Activate();
    }

    /// <summary>
    /// task 固定モードで CaptureWindow を開く (Dashboard の Add Task ボタン用)。
    /// </summary>
    public void ShowCaptureWindowForTask()
    {
        var discoveryService = _serviceProvider.GetRequiredService<ProjectDiscoveryService>();
        var captureWindow = new CaptureWindow(_captureService, discoveryService, _configService, fixedCategory: "task");
        captureWindow.Owner = this;

        captureWindow.OnNavigateToFile = (projectName, filePath) =>
        {
            var project = ResolveProject(projectName);
            if (project != null)
                NavigateToEditorAndOpenFile(project, filePath);
            MoveOnScreen();
        };

        captureWindow.ShowDialog();
        // task 追加後に Today Queue をリフレッシュ
        var dashboardPage = _serviceProvider.GetRequiredService<DashboardPage>();
        _ = dashboardPage.ViewModel.LoadTodayQueueAsync();
    }

    /// <summary>
    /// Add Task ダイアログを開く。コマンドパレットから呼び出す。
    /// </summary>
    public async void ShowAddTaskDialog(string? initialProjectName = null)
    {
        var dashboardPage = _serviceProvider.GetRequiredService<DashboardPage>();
        await dashboardPage.ShowAddTaskDialogAsync(initialProjectName);
    }

    /// <summary>
    /// プロジェクト名でプロジェクトを解決する。
    /// EditorViewModel が未ロードの場合は ProjectDiscoveryService のキャッシュから取得する。
    /// </summary>
    private ProjectInfo? ResolveProject(string projectName)
    {
        var editorVm = _serviceProvider.GetRequiredService<EditorViewModel>();
        var project = editorVm.Projects.FirstOrDefault(p =>
            string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));

        if (project == null)
        {
            var discoveryService = _serviceProvider.GetRequiredService<ProjectDiscoveryService>();
            var allProjects = discoveryService.GetProjectInfoList();
            project = allProjects.FirstOrDefault(p =>
                string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
        }

        return project;
    }
}
