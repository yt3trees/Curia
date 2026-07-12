using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Curia.Services;
using Curia.Services.Adapters;
using Curia.Services.Agent;
using Curia.Services.Agent.Tools;
using Curia.ViewModels;
using Curia.Views;
using Curia.Views.Pages;
using AppPageService = Curia.Services.PageService;
using WpfApplication = System.Windows.Application;

namespace Curia;

public partial class App : WpfApplication
{
    private const string MutexName = "Global\\Curia_SingleInstance";
    private Mutex? _mutex;
    private IServiceProvider? _serviceProvider;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        // アクセントカラーを固定値 (#0078D4) に設定し、PC ごとのシステム設定に左右されないようにする
        ApplicationAccentColorManager.Apply(
            System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD4),
            ApplicationTheme.Dark,
            false);

        // シングルインスタンス制御
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show(
                "Curia is already running.",
                "Curia",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            Shutdown();
            return;
        }

        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;

        // DI コンテナ構築
        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        // Standup タイマー起動
        var standup = _serviceProvider.GetRequiredService<StandupGeneratorService>();
        standup.StartScheduler();

        // Silence Alert スケジューラー起動
        var silenceAlert = _serviceProvider.GetRequiredService<SilenceAlertService>();
        silenceAlert.StartScheduler();

        // Asana Sync スケジュールタイマー起動
        var asanaSync = _serviceProvider.GetRequiredService<AsanaSyncViewModel>();
        asanaSync.StartScheduler();

        // MainWindow 表示
        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        // 画面外に配置してから Show() することで、DWM 初期化時のフラッシュを防ぐ。
        // OnLoaded で MoveOnScreen() が呼ばれ、画面中央に移動する。
        mainWindow.Left = -32000;
        mainWindow.Top = -32000;
        mainWindow.Show();

        // スケジュール通知タイマー起動 (TrayService は MainWindow.Show 内で初期化済み)
        var scheduleNotification = _serviceProvider.GetRequiredService<ScheduleNotificationService>();
        scheduleNotification.Start();

        // 会議終了フォローアップタイマー起動
        var meetingFollowup = _serviceProvider.GetRequiredService<MeetingFollowupService>();
        meetingFollowup.Start();

        // Ask Curia キャッシュウォームアップ
        var curiaQuery = _serviceProvider.GetRequiredService<CuriaQueryService>();
        _ = curiaQuery.WarmCacheAsync();

        // ポモドーロ古いログのアーカイブ
        var pomodoroService = _serviceProvider.GetRequiredService<PomodoroService>();
        _ = pomodoroService.ArchiveOldLogsAsync();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Core services
        services.AddSingleton<ConfigService>();
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<TrayService>();
        services.AddSingleton<ScriptRunnerService>();
        services.AddSingleton<ProjectDiscoveryService>();
        services.AddSingleton<ContextCompressionLayerService>();
        services.AddSingleton<FileEncodingService>();
        services.AddSingleton<TodayQueueService>();
        services.AddSingleton<StandupGeneratorService>();
        services.AddSingleton<AsanaSyncService>();
        services.AddSingleton<TeamTaskParser>();
        services.AddSingleton<LlmClientService>();
        services.AddSingleton<AsanaTaskParser>();
        services.AddSingleton<FocusUpdateService>();
        services.AddSingleton<DecisionLogGeneratorService>();
        services.AddSingleton<DecisionLogService>();
        services.AddSingleton<CaptureService>();
        services.AddSingleton<MeetingNotesService>();
        services.AddSingleton<StateSnapshotService>();
        services.AddSingleton<AgentHubService>();
        services.AddSingleton<AgentDeploymentService>();
        services.AddSingleton<WikiService>();
        services.AddSingleton<WikiIngestService>();
        services.AddSingleton<WikiQueryService>();
        services.AddSingleton<WikiLintService>();

        // Curia cross-project query
        services.AddSingleton<ICuriaSourceAdapter, DecisionLogSourceAdapter>();
        services.AddSingleton<ICuriaSourceAdapter, FocusHistorySourceAdapter>();
        services.AddSingleton<ICuriaSourceAdapter, MeetingNotesSourceAdapter>();
        services.AddSingleton<ICuriaSourceAdapter, TasksSourceAdapter>();
        services.AddSingleton<CuriaQueryService>();
        services.AddSingleton<ScheduleService>();
        services.AddSingleton<ScheduleNotificationService>();
        services.AddSingleton<SilenceAlertService>();
        services.AddSingleton<OutlookCalendarService>();
        services.AddSingleton<IcsCalendarService>();
        services.AddSingleton<MeetingFollowupService>();
        services.AddSingleton<PomodoroService>();
        services.AddSingleton<AgentCompatibilityService>();
        services.AddSingleton<AgentChatHistoryService>();
        services.AddSingleton<ICuriaAgentTool, ListProjectsTool>();
        services.AddSingleton<ICuriaAgentTool, GetTodayTasksTool>();
        services.AddSingleton<ICuriaAgentTool, GetProjectTasksTool>();
        services.AddSingleton<ICuriaAgentTool, SearchDecisionLogsTool>();
        services.AddSingleton<ICuriaAgentTool, AskKnowledgeBaseTool>();
        services.AddSingleton<AgentToolRegistry>();
        services.AddSingleton<AgentOrchestratorService>();

        // ViewModels
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<EditorViewModel>();
        services.AddSingleton<TimelineViewModel>();
        services.AddSingleton<GitReposViewModel>();
        services.AddSingleton<AsanaSyncViewModel>();
        services.AddSingleton<SetupViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<CommandPaletteViewModel>();
        services.AddSingleton<PomodoroViewModel>();
        services.AddSingleton<AgentHubViewModel>();
        services.AddSingleton<WikiViewModel>();
        services.AddSingleton<WeeklyScheduleViewModel>();
        services.AddSingleton<AgentChatViewModel>();

        // Pages
        services.AddSingleton<DashboardPage>();
        services.AddSingleton<EditorPage>();
        services.AddSingleton<TimelinePage>();
        services.AddSingleton<GitReposPage>();
        services.AddSingleton<AsanaSyncPage>();
        services.AddSingleton<SetupPage>();
        services.AddSingleton<SettingsPage>();
        services.AddSingleton<AgentHubPage>();
        services.AddSingleton<WikiPage>();
        services.AddSingleton<WeeklySchedulePage>();
        services.AddSingleton<AgentChatPage>();

        // wpf-ui: IPageService for DI-based page resolution
        services.AddSingleton<IPageService, AppPageService>();
        services.AddSingleton<IContentDialogService, ContentDialogService>();

        // MainWindow
        services.AddSingleton<MainWindow>();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        if (_serviceProvider is IDisposable disposable)
            disposable.Dispose();

        _mutex?.ReleaseMutex();
        _mutex?.Dispose();

        base.OnExit(e);
    }
}
