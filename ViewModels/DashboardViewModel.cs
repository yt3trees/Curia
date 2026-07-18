using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Curia.Helpers;
using Curia.Models;
using Curia.Services;

namespace Curia.ViewModels;

// アクティビティバーの1日分
public class ActivityDay
{
    public bool IsActive { get; init; }
}

// Workstream カード行のモデル
public class WorkstreamCardItem
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string? FocusFile { get; set; }
    public string FocusFreshness { get; set; } = "missing";
    public string FocusAgeText { get; set; } = "–";
    public int DecisionLogCount { get; set; }
    public bool IsClosed { get; set; }
    public bool HasTeamView { get; set; }
    public Models.TeamViewConfig? WorkstreamTeamView { get; set; }
}

// プロジェクトカードのViewModel
public partial class ProjectCardViewModel : ObservableObject
{
    [ObservableProperty]
    private bool isHidden;

    [ObservableProperty]
    private bool isAutoUpdatingFocus;

    public ProjectInfo Info { get; }

    public string DisplayName => Info.DisplayName;
    public string Tier => Info.Tier;
    public string Category => Info.Category;
    public int? FocusAge => Info.FocusAge;
    public int? SummaryAge => Info.SummaryAge;
    public string? FocusFile => Info.FocusFile;
    public string? SummaryFile => Info.SummaryFile;
    public string JunctionShared => Info.JunctionShared;
    public string JunctionObsidian => Info.JunctionObsidian;
    public string JunctionContext => Info.JunctionContext;
    public int DecisionLogCount => Info.DecisionLogCount;
    public bool HasUncommittedChanges => Info.HasUncommittedChanges;
    public int UncommittedRepoCount => Info.UncommittedRepoPaths.Count;
    public string UncommittedBadgeText => $"Uncommitted {UncommittedRepoCount}";
    public string UncommittedChangesTooltip => HasUncommittedChanges
        ? $"Uncommitted changes detected:\n{string.Join("\n", Info.UncommittedRepoPaths.Select(p => $"- {p}"))}"
        : "No uncommitted changes.";
    public IReadOnlyList<ActivityDay> ActivityDays { get; }

    public string FocusAgeText => FocusAge.HasValue ? $"{FocusAge}d" : "–";
    public string SummaryAgeText => SummaryAge.HasValue ? $"{SummaryAge}d" : "–";

    // "fresh" | "ok" | "aging" | "stale" | "missing"
    public string FocusFreshness => GetFreshness(FocusAge);
    public string SummaryFreshness => GetFreshness(SummaryAge);
    // FocusAge がある (current_focus.md が存在する) 前提で、fresh/ok (7日以内) を超えたら対象。
    // 30日超で FocusFreshness が "missing" になるケースも含める。ファイル自体が無い場合は FocusAge が null なので対象外。
    public bool IsFocusStale => FocusAge.HasValue && FocusAge.Value > 7;

    // ---- Workstream ----
    public ObservableCollection<WorkstreamCardItem> Workstreams { get; } = [];
    private List<WorkstreamCardItem> _allWorkstreams = [];
    public bool HasWorkstreams => _allWorkstreams.Count > 0;
    public bool HasClosedWorkstreams => _allWorkstreams.Any(w => w.IsClosed);
    public int ActiveWorkstreamCount => _allWorkstreams.Count(w => !w.IsClosed);

    public bool HasTeamView { get; }
    public string TeamTasksFilePath { get; }

    [ObservableProperty]
    private bool isWorkstreamExpanded = false;

    [ObservableProperty]
    private bool showClosedWorkstreams = false;

    partial void OnShowClosedWorkstreamsChanged(bool value) => RebuildVisibleWorkstreams();

    private void RebuildVisibleWorkstreams()
    {
        Workstreams.Clear();
        var src = ShowClosedWorkstreams ? _allWorkstreams : _allWorkstreams.Where(w => !w.IsClosed);
        foreach (var w in src) Workstreams.Add(w);
    }

    public ProjectCardViewModel(
        ProjectInfo info,
        bool hasTeamView = false,
        string teamTasksFilePath = "",
        Models.TeamViewConfig? teamViewConfig = null)
    {
        Info = info;
        // project_gids がある場合のみプロジェクトカードに [Team] ボタンを表示
        HasTeamView = hasTeamView && (teamViewConfig?.ProjectGids.Count > 0);
        TeamTasksFilePath = teamTasksFilePath;
        var today = DateTime.Today;
        var activeDates = info.FocusHistoryDates
            .Concat(info.DecisionLogDates)
            .Select(d => d.Date)
            .ToHashSet();
        var days = new List<ActivityDay>(30);
        for (int i = 29; i >= 0; i--)
            days.Add(new ActivityDay { IsActive = activeDates.Contains(today.AddDays(-i)) });
        ActivityDays = days;

        // Workstream カードを構築 (workstream 単位の TeamView 情報も設定)
        var wsTeamGids = teamViewConfig?.WorkstreamProjectGids
            ?? new Dictionary<string, List<string>>();
        _allWorkstreams = info.Workstreams.Select(ws =>
        {
            wsTeamGids.TryGetValue(ws.Id, out var wsGids);
            var hasWsTeamView = teamViewConfig?.Enabled == true
                && wsGids?.Count > 0;
            Models.TeamViewConfig? wsTeamView = hasWsTeamView
                ? new Models.TeamViewConfig
                {
                    Enabled = true,
                    ProjectGids = wsGids!,
                    WorkstreamProjectGids = []
                }
                : null;
            return new WorkstreamCardItem
            {
                Id = ws.Id,
                Label = ws.Label,
                FocusFile = ws.FocusFile,
                FocusFreshness = GetFreshness(ws.FocusAge),
                FocusAgeText = ws.FocusAge.HasValue ? $"{ws.FocusAge}d" : "–",
                DecisionLogCount = ws.DecisionLogCount,
                IsClosed = ws.IsClosed,
                HasTeamView = hasWsTeamView,
                WorkstreamTeamView = wsTeamView,
            };
        }).ToList();

        // 初期表示はアクティブのみ
        foreach (var w in _allWorkstreams.Where(w => !w.IsClosed)) Workstreams.Add(w);
    }

    private static string GetFreshness(int? age) => age switch
    {
        null => "missing",
        <= 3 => "fresh",
        <= 7 => "ok",
        <= 14 => "aging",
        <= 30 => "stale",
        _ => "missing"
    };
}

public partial class DashboardViewModel : ObservableObject
{
    private readonly ProjectDiscoveryService _discoveryService;
    private readonly ConfigService _configService;
    private readonly TodayQueueService _todayQueueService;
    private readonly StateSnapshotService _stateSnapshotService;
    private readonly SilenceAlertService _silenceAlertService;
    private readonly FocusSignalCollectorService _focusSignalCollectorService;
    private readonly FocusUpdateService _focusUpdateService;
    private readonly ProposalInboxService _proposalInboxService;
    private System.Timers.Timer? _refreshTimer;
    private readonly AsyncInitializationGate _initialization = new();
    private List<string> _hiddenKeys = [];
    private List<ProjectCardViewModel> _allCards = [];

    [ObservableProperty]
    private ObservableCollection<ProjectCardViewModel> projects = [];

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private ObservableCollection<TodayQueueTask> todayQueueTasks = [];

    [ObservableProperty]
    private string todayQueueStatus = "Today Queue: -";

    [ObservableProperty]
    private bool showAllTasks = false;

    [ObservableProperty]
    private ObservableCollection<string> projectFilterItems = ["All Projects"];

    [ObservableProperty]
    private string selectedProjectFilter = "All Projects";

    [ObservableProperty]
    private ObservableCollection<string> workstreamFilterItems = ["All Workstreams"];

    [ObservableProperty]
    private string selectedWorkstreamFilter = "All Workstreams";

    private List<TodayQueueTask> _cachedAllTasks = [];

    [ObservableProperty]
    private bool showHidden = false;

    // ---------- Pinned Folders ----------

    private List<PinnedFolder> _pinnedFoldersList = [];

    [ObservableProperty]
    private ObservableCollection<PinnedFolder> pinnedFolders = [];

    public bool HasPinnedFolders => PinnedFolders.Count > 0;

    partial void OnShowHiddenChanged(bool value) => ApplyFilter();

    public int HiddenCount => _allCards.Count(c => c.IsHidden);

    [ObservableProperty]
    private int snoozeCount;

    // Snooze カウントが 0 より大きい場合のみ表示用
    public bool HasSnoozed => SnoozeCount > 0;

    [ObservableProperty]
    private bool isAiEnabled;

    [ObservableProperty]
    private bool focusAutoUpdateBadgeEnabled;

    // ---------- Silence Alerts ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSilenceAlerts))]
    private ObservableCollection<SilenceAlert> silenceAlerts = [];

    public bool HasSilenceAlerts => SilenceAlerts.Count > 0;

    // ---------- Proposal Inbox ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingProposals))]
    private ObservableCollection<ProposalItem> pendingProposals = [];

    public bool HasPendingProposals => PendingProposals.Count > 0;

    public int AutoRefreshMinutes { get; set; }
    public int TodayQueueLimit { get; set; }

    // Dashboard -> Editor 遷移コールバック
    public Action<ProjectInfo>? OnOpenInEditor;

    // Dashboard -> Timeline 遷移コールバック
    public Action<ProjectInfo>? OnOpenInTimeline;

    public DashboardViewModel(
        ProjectDiscoveryService discoveryService,
        ConfigService configService,
        TodayQueueService todayQueueService,
        StateSnapshotService stateSnapshotService,
        SilenceAlertService silenceAlertService,
        FocusSignalCollectorService focusSignalCollectorService,
        FocusUpdateService focusUpdateService,
        ProposalInboxService proposalInboxService)
    {
        _discoveryService = discoveryService;
        _configService = configService;
        _todayQueueService = todayQueueService;
        _stateSnapshotService = stateSnapshotService;
        _silenceAlertService = silenceAlertService;
        _focusSignalCollectorService = focusSignalCollectorService;
        _focusUpdateService = focusUpdateService;
        _proposalInboxService = proposalInboxService;
        var settings = configService.LoadSettings();
        AutoRefreshMinutes = settings.DashboardAutoRefreshMinutes;
        TodayQueueLimit = settings.DashboardTodayQueueLimit;
        _hiddenKeys = configService.LoadHiddenProjects();

        _pinnedFoldersList = configService.LoadPinnedFolders();
        foreach (var pf in _pinnedFoldersList) PinnedFolders.Add(pf);
        PinnedFolders.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPinnedFolders));

        IsAiEnabled = settings.AiEnabled;
        FocusAutoUpdateBadgeEnabled = settings.FocusAutoUpdateBadgeEnabled;
        WeakReferenceMessenger.Default.Register<AiEnabledChangedMessage>(this,
            (_, msg) =>
            {
                IsAiEnabled = msg.Enabled;
                _ = LoadPendingProposalsAsync();
            });
        WeakReferenceMessenger.Default.Register<ProposalInboxChangedMessage>(this,
            (_, _) => _ = LoadPendingProposalsAsync());

        SilenceAlerts = new ObservableCollection<SilenceAlert>(_silenceAlertService.CurrentAlerts);
        _silenceAlertService.AlertsUpdated += OnSilenceAlertsUpdated;
    }

    private void OnSilenceAlertsUpdated(List<SilenceAlert> alerts)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            SilenceAlerts = new ObservableCollection<SilenceAlert>(alerts));
    }

    public void DismissSilenceAlert(SilenceAlert alert)
        => _silenceAlertService.Dismiss(alert.ProjectId);

    public void SnoozeSilenceAlert(SilenceAlert alert)
        => _silenceAlertService.Snooze(alert.ProjectId);

    public void OpenSilenceAlertProject(SilenceAlert alert)
    {
        var card = _allCards.FirstOrDefault(c => c.Info.Name == alert.ProjectId);
        if (card != null) OnOpenInEditor?.Invoke(card.Info);
    }

    public async Task ForceRefreshSilenceAlertsAsync()
        => await _silenceAlertService.ForceRefreshAsync();

    public List<TodayQueueTask> GetTopTasksForAi(int limit = 30)
        => _cachedAllTasks.Take(limit).ToList();

    public async Task RefreshAsync(bool force = false)
    {
        IsLoading = true;
        try
        {
            var list = await _discoveryService.GetProjectInfoListAsync(force: force);
            _allCards = list.Select(p =>
            {
                var teamConfig = _configService.LoadAsanaProjectConfig(p);
                var teamView = teamConfig?.TeamView;
                var hasTeamView = teamView?.Enabled == true;
                var teamTasksFile = hasTeamView
                    ? System.IO.Path.Combine(_configService.GetObsidianProjectPath(p), "team-tasks.md")
                    : "";
                return new ProjectCardViewModel(p, hasTeamView, teamTasksFile, teamView)
                {
                    IsHidden = _hiddenKeys.Contains(p.HiddenKey)
                };
            }).ToList();
            ApplyFilter();
            // Today Queue もキャッシュ済みリストで更新
            await LoadTodayQueueInternalAsync(list);
            // Proposal Inbox の Pending 一覧を更新
            await LoadPendingProposalsAsync();
            // State Snapshot をバックグラウンドで書き出す
            _ = _stateSnapshotService.ExportAsync(list);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public Task EnsureInitializedAsync()
        => _initialization.EnsureAsync(async () =>
        {
            await RefreshAsync();
            SetupAutoRefresh();
        });

    // ---------- Focus Auto-Update ----------

    public async Task<FocusUpdateResult> GenerateAutoUpdateFocusProposalAsync(
        ProjectCardViewModel card, CancellationToken ct)
    {
        var signals = await _focusSignalCollectorService.CollectAsync(card.Info, ct);
        return await _focusUpdateService.GenerateProposalAsync(card.Info, workstreamId: null, ct, signals: signals);
    }

    public async Task<string> RefineAutoUpdateFocusProposalAsync(
        FocusUpdateResult result,
        string instructions,
        List<(string instruction, string result)> refineHistory,
        CancellationToken ct)
    {
        var refined = await _focusUpdateService.RefineAsync(
            result.DebugUserPrompt, result.ProposedContent, instructions, refineHistory, ct);
        refineHistory.Add((instructions, refined));
        return refined;
    }

    public async Task ApplyAutoUpdateFocusProposalAsync(FocusUpdateResult result, string content, CancellationToken ct)
    {
        await _focusUpdateService.ApplyProposalAsync(result, content, ct);
        await RefreshAsync(force: true);
    }

    // ---------- Proposal Inbox ----------

    /// <summary>Pending 提案を読み込む。表示前に陳腐化チェックを行う。</summary>
    public async Task LoadPendingProposalsAsync()
    {
        try
        {
            var aiEnabled = _configService.LoadSettings().AiEnabled;
            var pending = new List<ProposalItem>();
            if (aiEnabled)
            {
                await _proposalInboxService.ExpireStaleAsync();
                pending = await _proposalInboxService.LoadPendingAsync();
            }
            Application.Current?.Dispatcher.Invoke(() =>
                PendingProposals = new ObservableCollection<ProposalItem>(pending));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Dashboard] LoadPendingProposalsAsync failed: {ex}");
        }
    }

    /// <summary>
    /// 提案を適用する。適用直前に陳腐化を再チェックし、ズレていたら適用せず Expired に落とす。
    /// </summary>
    public async Task<(bool ok, string message)> AcceptProposalAsync(
        ProposalItem item, string content, CancellationToken ct = default)
    {
        if (ProposalInboxService.IsStale(item))
        {
            await _proposalInboxService.UpdateStatusAsync(item, ProposalStatus.Expired);
            return (false, "This proposal has expired: current_focus.md was modified after it was generated.");
        }

        await _focusUpdateService.ApplyProposalAsync(item.ToFocusUpdateResult(), content, ct);
        await _proposalInboxService.UpdateStatusAsync(item, ProposalStatus.Accepted);
        await RefreshAsync(force: true);
        return (true, "Proposal applied.");
    }

    public Task RejectProposalAsync(ProposalItem item)
        => _proposalInboxService.UpdateStatusAsync(item, ProposalStatus.Rejected);

    public Task ExpireProposalAsync(ProposalItem item)
        => _proposalInboxService.UpdateStatusAsync(item, ProposalStatus.Expired);

    public async Task LoadTodayQueueAsync()
    {
        TodayQueueStatus = "Today Queue: Loading...";
        try
        {
            var list = await _discoveryService.GetProjectInfoListAsync();
            await LoadTodayQueueInternalAsync(list);
        }
        catch (Exception ex)
        {
            TodayQueueStatus = $"Today Queue: Error - {ex.Message}";
        }
    }

    private async Task LoadTodayQueueInternalAsync(List<ProjectInfo> projects)
    {
        try
        {
            TodayQueueLimit = _configService.LoadSettings().DashboardTodayQueueLimit;
            var allTasks = await Task.Run(() => _todayQueueService.GetAllTasksSorted(projects, 10000));
            _todayQueueService.EnsureSnoozeLoaded();
            _cachedAllTasks = allTasks;

            // フィルタ一覧を更新 (現在の選択値を保持)
            var projectNames = allTasks
                .Select(t => t.ProjectShortName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct()
                .OrderBy(n => n)
                .ToList();

            var workstreamNames = allTasks
                .Where(t => t.HasWorkstream)
                .Select(t => t.WorkstreamLabel!)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct()
                .OrderBy(n => n)
                .ToList();

            Application.Current.Dispatcher.Invoke(() =>
            {
                var currentProject = SelectedProjectFilter;
                var currentWorkstream = SelectedWorkstreamFilter;
                ProjectFilterItems.Clear();
                ProjectFilterItems.Add("All Projects");
                foreach (var name in projectNames)
                    if (!ProjectFilterItems.Contains(name))
                        ProjectFilterItems.Add(name);

                WorkstreamFilterItems.Clear();
                WorkstreamFilterItems.Add("All Workstreams");
                foreach (var name in workstreamNames)
                    if (!WorkstreamFilterItems.Contains(name))
                        WorkstreamFilterItems.Add(name);

                SelectedProjectFilter = ProjectFilterItems.Contains(currentProject) ? currentProject : "All Projects";
                SelectedWorkstreamFilter = WorkstreamFilterItems.Contains(currentWorkstream) ? currentWorkstream : "All Workstreams";
            });

            ApplyProjectFilter();
        }
        catch (Exception ex)
        {
            Application.Current.Dispatcher.Invoke(() =>
                TodayQueueStatus = $"Today Queue: Error - {ex.Message}");
        }
    }

    partial void OnSelectedProjectFilterChanged(string value) => ApplyProjectFilter();
    partial void OnSelectedWorkstreamFilterChanged(string value) => ApplyProjectFilter();

    private void ApplyProjectFilter()
    {
        var tasks = _cachedAllTasks;
        var projectFilter = SelectedProjectFilter;
        var workstreamFilter = SelectedWorkstreamFilter;
        var isProjectFiltered = projectFilter != "All Projects" && !string.IsNullOrEmpty(projectFilter);
        var isWorkstreamFiltered = workstreamFilter != "All Workstreams" && !string.IsNullOrEmpty(workstreamFilter);

        if (isProjectFiltered)
        {
            var normalizedFilter = projectFilter.Trim();

            // "Project / Workstream" 指定なら完全一致。
            // "Project" 指定なら配下の "Project / xxx" も含める。
            if (normalizedFilter.Contains(" / ", StringComparison.Ordinal))
            {
                tasks = tasks.Where(t =>
                    string.Equals(t.ProjectFilterLabel.Trim(), normalizedFilter, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            else
            {
                tasks = tasks.Where(t =>
                {
                    var project = t.ProjectShortName.Trim();
                    var composite = t.ProjectFilterLabel.Trim();
                    return string.Equals(project, normalizedFilter, StringComparison.OrdinalIgnoreCase)
                        || composite.StartsWith($"{normalizedFilter} / ", StringComparison.OrdinalIgnoreCase);
                }).ToList();
            }
        }

        if (isWorkstreamFiltered)
        {
            var normalizedWorkstream = workstreamFilter.Trim();
            tasks = tasks.Where(t =>
                !string.IsNullOrWhiteSpace(t.WorkstreamLabel) &&
                string.Equals(t.WorkstreamLabel.Trim(), normalizedWorkstream, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var limit = ShowAllTasks ? int.MaxValue : TodayQueueLimit;
        var visible = tasks.Where(t => !_todayQueueService.IsSnoozed(t.SnoozeKey)).Take(limit).ToList();
        var snoozed = tasks.Count(t => _todayQueueService.IsSnoozed(t.SnoozeKey));

        Application.Current.Dispatcher.Invoke(() =>
        {
            TodayQueueTasks.Clear();
            foreach (var t in visible) TodayQueueTasks.Add(t);
            SnoozeCount = snoozed;
            OnPropertyChanged(nameof(HasSnoozed));

            string status;
            if (visible.Count == 0 && snoozed == 0)
                status = "Today Queue: No tasks";
            else if (visible.Count == 0)
                status = $"Today Queue: {snoozed} snoozed";
            else
            {
                var suffixParts = new List<string>();
                if (isProjectFiltered) suffixParts.Add(projectFilter);
                if (isWorkstreamFiltered) suffixParts.Add(workstreamFilter);
                var suffix = suffixParts.Count > 0
                    ? $" ({string.Join(" / ", suffixParts)})"
                    : (ShowAllTasks ? "" : $" (Top {TodayQueueLimit})");
                status = $"Today Queue: {visible.Count} items{suffix}";
                if (snoozed > 0) status += $", {snoozed} snoozed";
            }
            TodayQueueStatus = status;
        });
    }

    public async Task CompleteTaskAsync(TodayQueueTask task)
    {
        if (task.IsLocalOnly)
        {
            TodayQueueStatus = "Today Queue: Completing local task...";
            await Task.Run(() => _todayQueueService.MarkLocalTaskCompletedInFile(task));
            _cachedAllTasks.Remove(task);
            Application.Current.Dispatcher.Invoke(() => TodayQueueTasks.Remove(task));
            TodayQueueStatus = "Today Queue: Task completed.";
        }
        else
        {
            TodayQueueStatus = "Today Queue: Submitting to Asana...";
            var (ok, msg) = await _todayQueueService.CompleteAsanaTaskAsync(task.AsanaTaskGid!);
            if (ok)
            {
                await Task.Run(() => _todayQueueService.MarkTaskCompletedInFile(task));
                _cachedAllTasks.Remove(task);
                Application.Current.Dispatcher.Invoke(() => TodayQueueTasks.Remove(task));
            }
            TodayQueueStatus = $"Today Queue: {msg}";
        }
    }

    public async Task<(bool Success, string Message)> ChangeDueDateAsync(
        TodayQueueTask task, string dueOn, string dueAt = "")
    {
        TodayQueueStatus = "Today Queue: Updating due date...";
        var (ok, msg) = await _todayQueueService.UpdateDueDateAsync(task.AsanaTaskGid!, dueOn, dueAt);
        if (ok)
        {
            // tasks.md のファイルも更新してから再読み込み
            var dateOnly = string.IsNullOrWhiteSpace(dueOn)
                ? (dueAt.Length >= 10 ? dueAt[..10] : "")
                : dueOn;
            await Task.Run(() => _todayQueueService.UpdateDueDateInFile(task, dateOnly));
            await LoadTodayQueueAsync();
        }
        else
            TodayQueueStatus = $"Today Queue: {msg}";
        return (ok, msg);
    }

    public async Task SnoozeTaskAsync(TodayQueueTask task)
    {
        await Task.Run(() => _todayQueueService.SnoozeTask(task.SnoozeKey));
        ApplyProjectFilter();
    }

    public async Task UnsnoozeAllAsync()
    {
        await Task.Run(() => _todayQueueService.UnsnoozeAll());
        ApplyProjectFilter();
    }

    public void SetupAutoRefresh()
    {
        _refreshTimer?.Stop();
        _refreshTimer?.Dispose();
        _refreshTimer = null;
        if (AutoRefreshMinutes <= 0) return;
        _refreshTimer = new System.Timers.Timer(AutoRefreshMinutes * 60_000);
        _refreshTimer.Elapsed += async (_, _) => await RefreshAsync(force: true);
        _refreshTimer.AutoReset = true;
        _refreshTimer.Start();
    }

    private void ApplyFilter()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            Projects.Clear();
            var src = ShowHidden ? _allCards : _allCards.Where(c => !c.IsHidden);
            foreach (var c in src) Projects.Add(c);
            OnPropertyChanged(nameof(HiddenCount));
        });
    }

    public void HideProject(ProjectCardViewModel card)
    {
        if (!_hiddenKeys.Contains(card.Info.HiddenKey))
        {
            _hiddenKeys.Add(card.Info.HiddenKey);
            _configService.SaveHiddenProjects(_hiddenKeys);
        }
        card.IsHidden = true;
        if (!ShowHidden)
            Projects.Remove(card);
        OnPropertyChanged(nameof(HiddenCount));
    }

    public void UnhideProject(ProjectCardViewModel card)
    {
        _hiddenKeys.Remove(card.Info.HiddenKey);
        _configService.SaveHiddenProjects(_hiddenKeys);
        card.IsHidden = false;
        ApplyFilter();
    }

    public void OpenDirectory(ProjectCardViewModel card)
    {
        if (Directory.Exists(card.Info.Path))
            Process.Start("explorer.exe", $"\"{card.Info.Path}\"");
    }

    public void OpenTerminal(ProjectCardViewModel card)
    {
        if (Directory.Exists(card.Info.Path))
            OpenTerminalAtPath(card.Info.Path);
    }

    // ---------- Pinned Folders: ピン操作 ----------

    public void PinFolder(ProjectCardViewModel card, WorkstreamCardItem? workstream, string folderName, string fullPath)
    {
        // 重複チェック
        if (_pinnedFoldersList.Any(p =>
            p.Project == card.Info.Name &&
            p.Workstream == workstream?.Id &&
            p.Folder == folderName))
            return;

        var pf = new PinnedFolder
        {
            Project = card.Info.Name,
            Workstream = workstream?.Id,
            Folder = folderName,
            FullPath = fullPath,
            PinnedAt = DateTime.Today.ToString("yyyy-MM-dd"),
        };
        _pinnedFoldersList.Add(pf);
        _configService.SavePinnedFolders(_pinnedFoldersList);
        Application.Current.Dispatcher.Invoke(() => PinnedFolders.Add(pf));
    }

    public void MovePinnedFolder(PinnedFolder source, PinnedFolder target)
    {
        var fromIndex = _pinnedFoldersList.IndexOf(source);
        var toIndex = _pinnedFoldersList.IndexOf(target);
        if (fromIndex < 0 || toIndex < 0 || fromIndex == toIndex) return;
        _pinnedFoldersList.RemoveAt(fromIndex);
        _pinnedFoldersList.Insert(toIndex, source);
        _configService.SavePinnedFolders(_pinnedFoldersList);
        PinnedFolders.Move(fromIndex, toIndex);
    }

    public void UnpinFolder(PinnedFolder pf)
    {
        _pinnedFoldersList.Remove(pf);
        _configService.SavePinnedFolders(_pinnedFoldersList);
        PinnedFolders.Remove(pf);
    }

    public void ClearPinnedFolders()
    {
        _pinnedFoldersList.Clear();
        _configService.SavePinnedFolders(_pinnedFoldersList);
        PinnedFolders.Clear();
    }

    public void OpenPinnedFolder(PinnedFolder pf)
    {
        if (Directory.Exists(pf.FullPath))
            Process.Start("explorer.exe", $"\"{pf.FullPath}\"");
        else
            System.Windows.MessageBox.Show(
                "The folder no longer exists.",
                "Folder Not Found",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
    }

    public async Task<List<(string FolderName, string FullPath)>> GetRecentWorkFoldersAsync(string projectPath, string? workstreamId, int limit = 10)
    {
        return await Task.Run(() =>
        {
            var results = new List<(string, string)>();
            try
            {
                bool isWorkstream = !string.IsNullOrWhiteSpace(workstreamId);
                IEnumerable<string> yearMonthDirs;

                if (isWorkstream)
                {
                    var baseRoot = Path.Combine(projectPath, "shared", "_work", workstreamId!);
                    if (!Directory.Exists(baseRoot)) return results;

                    yearMonthDirs = Directory.GetDirectories(baseRoot)
                        .Where(d => Regex.IsMatch(Path.GetFileName(d), @"^\d{6}$"))
                        .OrderByDescending(d => d);
                }
                else
                {
                    var baseRoot = Path.Combine(projectPath, "shared", "_work");
                    if (!Directory.Exists(baseRoot)) return results;

                    // year/yearMonth 構造をフラット化して降順に並べる
                    var allYM = new List<string>();
                    foreach (var yearDir in Directory.GetDirectories(baseRoot)
                        .Where(d => Regex.IsMatch(Path.GetFileName(d), @"^\d{4}$"))
                        .OrderByDescending(d => d))
                    {
                        allYM.AddRange(
                            Directory.GetDirectories(yearDir)
                                .Where(d => Regex.IsMatch(Path.GetFileName(d), @"^\d{6}$"))
                                .OrderByDescending(d => d));
                    }
                    yearMonthDirs = allYM;
                }

                foreach (var ymDir in yearMonthDirs)
                {
                    if (results.Count >= limit) break;
                    foreach (var workDir in Directory.GetDirectories(ymDir)
                        .Where(d => Regex.IsMatch(Path.GetFileName(d), @"^\d{8}_"))
                        .OrderByDescending(d => d))
                    {
                        results.Add((Path.GetFileName(workDir), workDir));
                        if (results.Count >= limit) break;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Dashboard] GetRecentWorkFolders error: {ex}");
            }
            return results;
        });
    }

    public void OpenVSCode(ProjectCardViewModel card)
    {
        if (!Directory.Exists(card.Info.Path)) return;
        TryStart("code", $"\"{card.Info.Path}\"");
    }

    public void OpenWorkRoot(ProjectCardViewModel card)
    {
        var workRoot = Path.Combine(card.Info.Path, "shared", "_work");
        if (Directory.Exists(workRoot))
            Process.Start("explorer.exe", $"\"{workRoot}\"");
        else
            System.Windows.MessageBox.Show(
                "The shared\\_work folder does not exist.",
                "Folder Not Found",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
    }

    public void OpenWorkstreamWorkRoot(ProjectCardViewModel card, WorkstreamCardItem workstream)
    {
        var workstreamRoot = Path.Combine(card.Info.Path, "shared", "_work", workstream.Id);
        if (Directory.Exists(workstreamRoot))
            Process.Start("explorer.exe", $"\"{workstreamRoot}\"");
        else
            System.Windows.MessageBox.Show(
                "The workstream work root folder does not exist.",
                "Folder Not Found",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
    }

    public string? CreateTodayGeneralWorkFolder(ProjectCardViewModel card, string featureName)
    {
        return CreateTodayWorkFolder(card.Info.Path, workstreamId: null, featureName);
    }

    public string? CreateTodayWorkstreamWorkFolder(ProjectCardViewModel card, WorkstreamCardItem workstream, string featureName)
    {
        return CreateTodayWorkFolder(card.Info.Path, workstream.Id, featureName);
    }

    public void OpenAgentTerminal(ProjectCardViewModel card, string agent)
    {
        if (!Directory.Exists(card.Info.Path)) return;
        OpenAgentAtPath(card.Info.Path, agent);
    }

    private static string? CreateTodayWorkFolder(string projectPath, string? workstreamId, string featureName)
    {
        try
        {
            var today = DateTime.Today;
            var year = today.ToString("yyyy");
            var yearMonth = today.ToString("yyyyMM");
            var day = today.ToString("yyyyMMdd");
            var safeFeature = NormalizeWorkFolderSuffix(featureName);
            if (string.IsNullOrWhiteSpace(safeFeature))
            {
                System.Windows.MessageBox.Show(
                    "Feature name is required.",
                    "Invalid Name",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return null;
            }

            var baseRoot = string.IsNullOrWhiteSpace(workstreamId)
                ? Path.Combine(projectPath, "shared", "_work")
                : Path.Combine(projectPath, "shared", "_work", workstreamId);

            var target = string.IsNullOrWhiteSpace(workstreamId)
                ? Path.Combine(baseRoot, year, yearMonth, $"{day}_{safeFeature}")
                : Path.Combine(baseRoot, yearMonth, $"{day}_{safeFeature}");
            Directory.CreateDirectory(target);
            return target;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Dashboard] Failed to create today work folder: {ex}");
            System.Windows.MessageBox.Show(
                $"Failed to create work folder.\n{ex.Message}",
                "Create Folder Failed",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            return null;
        }
    }

    private static string NormalizeWorkFolderSuffix(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return "";
        return Regex.Replace(trimmed, @"[\\/:*?""<>|]", "_");
    }

    private static void OpenTerminalAtPath(string path)
    {
        if (TryStart("wt.exe", $"-d \"{path}\"")) return;
        if (TryStart("pwsh.exe", $"-NoExit -Command \"Set-Location '{path}'\"")) return;
        TryStart("powershell.exe", $"-NoExit -Command \"Set-Location '{path}'\"");
    }

    private static void OpenAgentAtPath(string path, string agent)
    {
        if (TryStart("wt.exe", $"-d \"{path}\" -- pwsh.exe -NoExit -Command \"{agent}\"")) return;
        if (TryStart("pwsh.exe", $"-NoExit -Command \"Set-Location '{path}'; {agent}\"")) return;
        TryStart("powershell.exe", $"-NoExit -Command \"Set-Location '{path}'; {agent}\"");
    }

    private static bool TryStart(string exe, string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    public void OpenInEditor(ProjectCardViewModel card) => OnOpenInEditor?.Invoke(card.Info);

    public void OpenInTimeline(ProjectCardViewModel card) => OnOpenInTimeline?.Invoke(card.Info);

    public string ShowAllButtonLabel => ShowAllTasks ? $"Show Top {TodayQueueLimit}" : "Show All";

    [RelayCommand]
    private void ToggleShowAll()
    {
        ShowAllTasks = !ShowAllTasks;
        OnPropertyChanged(nameof(ShowAllButtonLabel));
        ApplyProjectFilter();
    }
}
