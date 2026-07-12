using System.Diagnostics;
using Curia.ViewModels;

namespace Curia.Services;

/// <summary>Preloads singleton ViewModel data after the first window render.</summary>
public sealed class StartupPreloadService
{
    private readonly DashboardViewModel _dashboard;
    private readonly SettingsViewModel _settings;
    private readonly EditorViewModel _editor;
    private readonly WikiViewModel _wiki;
    private readonly GitReposViewModel _gitRepos;
    private readonly AsanaSyncViewModel _asanaSync;
    private readonly SetupViewModel _setup;
    private readonly AgentChatViewModel _agentChat;
    private readonly AgentHubViewModel _agentHub;
    private readonly TimelineViewModel _timeline;
    private readonly WeeklyScheduleViewModel _schedule;
    private readonly object _lock = new();
    private Task? _preloadTask;

    public StartupPreloadService(
        DashboardViewModel dashboard,
        SettingsViewModel settings,
        EditorViewModel editor,
        WikiViewModel wiki,
        GitReposViewModel gitRepos,
        AsanaSyncViewModel asanaSync,
        SetupViewModel setup,
        AgentChatViewModel agentChat,
        AgentHubViewModel agentHub,
        TimelineViewModel timeline,
        WeeklyScheduleViewModel schedule)
    {
        _dashboard = dashboard;
        _settings = settings;
        _editor = editor;
        _wiki = wiki;
        _gitRepos = gitRepos;
        _asanaSync = asanaSync;
        _setup = setup;
        _agentChat = agentChat;
        _agentHub = agentHub;
        _timeline = timeline;
        _schedule = schedule;
    }

    public Task PreloadAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
            return _preloadTask ??= PreloadCoreAsync(cancellationToken);
    }

    private async Task PreloadCoreAsync(CancellationToken cancellationToken)
    {
        await PreloadItemAsync("Dashboard", _dashboard.EnsureInitializedAsync, cancellationToken);
        await PreloadItemAsync("Settings", _settings.EnsureInitializedAsync, cancellationToken);
        await PreloadItemAsync("Editor", _editor.EnsureInitializedAsync, cancellationToken);
        await PreloadItemAsync("Wiki", _wiki.EnsureInitializedAsync, cancellationToken);
        await PreloadItemAsync("Git Repos", _gitRepos.EnsureInitializedAsync, cancellationToken);
        await PreloadItemAsync("Asana Sync", _asanaSync.EnsureInitializedAsync, cancellationToken);
        await PreloadItemAsync("Setup", _setup.EnsureInitializedAsync, cancellationToken);
        await PreloadItemAsync("Agent Chat", _agentChat.EnsureInitializedAsync, cancellationToken);
        await PreloadItemAsync("Agent Hub", _agentHub.EnsureInitializedAsync, cancellationToken);
        await PreloadItemAsync("Timeline", _timeline.EnsureInitializedAsync, cancellationToken);
        await PreloadItemAsync("Schedule", _schedule.EnsureInitializedAsync, cancellationToken);
    }

    private static async Task PreloadItemAsync(string name, Func<Task> initializeAsync, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        Debug.WriteLine($"[StartupPreload] {name} started.");
        try
        {
            await initializeAsync();
            Debug.WriteLine($"[StartupPreload] {name} completed in {stopwatch.ElapsedMilliseconds:N0} ms.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Debug.WriteLine($"[StartupPreload] {name} cancelled after {stopwatch.ElapsedMilliseconds:N0} ms.");
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StartupPreload] {name} failed after {stopwatch.ElapsedMilliseconds:N0} ms: {ex}");
        }
    }
}