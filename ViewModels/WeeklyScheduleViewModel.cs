using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Curia.Models;
using Curia.Services;

namespace Curia.ViewModels;

// ピッカーポップアップ用 (スケジュール済みを含む全タスク)
public record UnscheduledTaskGroup(string ProjectShortName, IReadOnlyList<TodayQueueTask> Tasks);

// 左パネル用 (スケジュール済みフラグ付き)
public record TaskViewItem(TodayQueueTask Task, bool IsScheduledThisWeek);
public record TaskViewGroup(string ProjectShortName, IReadOnlyList<TaskViewItem> Items);

public partial class WeeklyScheduleViewModel : ObservableObject
{
    private readonly ScheduleService _scheduleService;
    private readonly TodayQueueService _todayQueueService;
    private readonly ProjectDiscoveryService _discoveryService;
    private readonly ConfigService _configService;
    private readonly OutlookCalendarService _outlookCalendarService;
    private readonly IcsCalendarService _icsCalendarService;

    [ObservableProperty]
    private DateTime _weekStart;

    [ObservableProperty]
    private string _weekRangeText = "";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _isLoading;

    // 左パネル: 全タスク (スケジュール済みは IsScheduledThisWeek=true で薄く表示)
    public ObservableCollection<TaskViewGroup> TaskListGroups { get; } = [];
    // ピッカーポップアップ用: 全タスク (フラットな形式)
    public ObservableCollection<UnscheduledTaskGroup> AllTaskGroups { get; } = [];
    public ObservableCollection<ScheduleBlock> TimedBlocks { get; } = [];
    public ObservableCollection<ScheduleBlock> AllDayBlocks { get; } = [];
    // Outlook カレンダーイベント (読み取り専用オーバーレイ)
    public ObservableCollection<OutlookEvent> OutlookEvents { get; } = [];

    // 他ページへのナビゲーション コールバック
    public Action<Models.ProjectInfo, string>? OnOpenInEditor { get; set; }

    // タスクキャッシュ (5分 TTL — 週切り替えではタスクリストは変わらない)
    // hidden_projects.json が変わった場合もキャッシュを無効化するため hiddenKeys をセットで保持する
    private List<TodayQueueTask>? _cachedAllTasks;
    private DateTime _tasksCachedAt = DateTime.MinValue;
    private HashSet<string> _cachedHiddenKeys = [];
    private const int TasksCacheTtlSeconds = 300;

    public WeeklyScheduleViewModel(
        ScheduleService scheduleService,
        TodayQueueService todayQueueService,
        ProjectDiscoveryService discoveryService,
        ConfigService configService,
        OutlookCalendarService outlookCalendarService,
        IcsCalendarService icsCalendarService)
    {
        _scheduleService = scheduleService;
        _todayQueueService = todayQueueService;
        _discoveryService = discoveryService;
        _configService = configService;
        _outlookCalendarService = outlookCalendarService;
        _icsCalendarService = icsCalendarService;

        WeekStart = GetMondayOf(DateTime.Today);
        UpdateWeekRangeText();
    }

    [RelayCommand]
    private void PrevWeek()
    {
        WeekStart = WeekStart.AddDays(-7);
        UpdateWeekRangeText();
        _ = LoadWeekAsync();
    }

    [RelayCommand]
    private void NextWeek()
    {
        WeekStart = WeekStart.AddDays(7);
        UpdateWeekRangeText();
        _ = LoadWeekAsync();
    }

    [RelayCommand]
    private void Today()
    {
        WeekStart = GetMondayOf(DateTime.Today);
        UpdateWeekRangeText();
        _ = LoadWeekAsync();
    }

    public async Task LoadWeekAsync()
    {
        IsLoading = true;
        StatusText = "Loading...";
        try
        {
            // タスクはキャッシュが有効な間は再取得しない (週切り替えでは変わらないため)
            // hidden_projects.json が変わった場合は TTL 内でもキャッシュを破棄して再取得する
            var hiddenKeys = _configService.LoadHiddenProjects().ToHashSet(StringComparer.Ordinal);
            bool hiddenKeysChanged = !hiddenKeys.SetEquals(_cachedHiddenKeys);

            List<TodayQueueTask> allTasks;
            if (_cachedAllTasks != null
                && !hiddenKeysChanged
                && (DateTime.Now - _tasksCachedAt).TotalSeconds < TasksCacheTtlSeconds)
            {
                allTasks = _cachedAllTasks;
            }
            else
            {
                var projects = await _discoveryService.GetProjectInfoListAsync();
                // 非表示プロジェクトのタスクは読み込まない
                var visibleProjects = projects
                    .Where(p => !hiddenKeys.Contains(p.HiddenKey))
                    .ToList();
                allTasks = await Task.Run(() =>
                    _todayQueueService.GetAllTasksSorted(visibleProjects, 10000));
                _cachedAllTasks   = allTasks;
                _tasksCachedAt    = DateTime.Now;
                _cachedHiddenKeys = hiddenKeys;
            }

            var blocksInWeek = _scheduleService.GetBlocksForWeek(WeekStart);

            // カレンダーイベント取得 (ICS 優先、次いで COM Interop)
            var settings = _configService.LoadSettings();
            IReadOnlyList<OutlookEvent> outlookEvents = [];
            if (settings.IcsCalendarEnabled && !string.IsNullOrWhiteSpace(settings.IcsCalendarUrl))
            {
                try { outlookEvents = await _icsCalendarService.GetEventsForWeekAsync(settings.IcsCalendarUrl, WeekStart); }
                catch { /* 取得失敗はサイレント無視 */ }

                // 除外ワードでフィルタ
                var excludes = settings.IcsExcludeSubjects
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (excludes.Count > 0)
                    outlookEvents = outlookEvents.Where(e => !excludes.Contains(e.Subject)).ToList();
            }
            else if (settings.OutlookCalendarEnabled)
                outlookEvents = await _outlookCalendarService.GetEventsForWeekAsync(WeekStart);

            // TitleSnapshot をライブタスクで更新
            var identityToTask = allTasks
                .GroupBy(BuildIdentity)
                .ToDictionary(g => g.Key, g => g.First());
            foreach (var block in blocksInWeek)
            {
                if (identityToTask.TryGetValue(block.TaskIdentity, out var live))
                    block.TitleSnapshot = live.DisplayMainTitle;
            }

            // Outlook/ICS イベントにリンク済みタスクのメタを名寄せ
            var gidToTask = allTasks
                .Where(t => !string.IsNullOrEmpty(t.AsanaTaskGid))
                .GroupBy(t => t.AsanaTaskGid!)
                .ToDictionary(g => g.Key, g => g.First());
            var allProjects = await _discoveryService.GetProjectInfoListAsync();
            foreach (var ev in outlookEvents)
            {
                if (!string.IsNullOrEmpty(ev.LinkedAsanaGid)
                    && gidToTask.TryGetValue(ev.LinkedAsanaGid, out var linkedTask))
                {
                    ev.LinkedTaskTitle        = linkedTask.DisplayMainTitle;
                    ev.LinkedProjectShortName = linkedTask.ProjectShortName;
                    ev.LinkedProject = allProjects.FirstOrDefault(p =>
                        p.DisplayName.Contains(linkedTask.ProjectShortName, StringComparison.OrdinalIgnoreCase));
                }
            }

            var scheduledIdentities = blocksInWeek
                .Select(b => b.TaskIdentity)
                .ToHashSet(StringComparer.Ordinal);

            // 左パネル: 全タスク + スケジュール済みフラグ
            var taskListGroups = allTasks
                .GroupBy(t => t.ProjectShortName)
                .OrderBy(g => g.Key)
                .Select(g => new TaskViewGroup(g.Key,
                    g.Select(t => new TaskViewItem(t, scheduledIdentities.Contains(BuildIdentity(t))))
                     .ToList()))
                .ToList();

            // ピッカー用: 全タスク (フラット)
            var allGroups = allTasks
                .GroupBy(t => t.ProjectShortName)
                .OrderBy(g => g.Key)
                .Select(g => new UnscheduledTaskGroup(g.Key, g.ToList()))
                .ToList();

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                TaskListGroups.Clear();
                foreach (var g in taskListGroups) TaskListGroups.Add(g);

                AllTaskGroups.Clear();
                foreach (var g in allGroups) AllTaskGroups.Add(g);

                TimedBlocks.Clear();
                AllDayBlocks.Clear();
                foreach (var b in blocksInWeek)
                {
                    if (b.Kind == ScheduleBlockKind.Timed) TimedBlocks.Add(b);
                    else AllDayBlocks.Add(b);
                }

                OutlookEvents.Clear();
                foreach (var ev in outlookEvents) OutlookEvents.Add(ev);

                StatusText = $"{blocksInWeek.Count} block(s)" +
                             (outlookEvents.Count > 0 ? $" + {outlookEvents.Count} Outlook" : "");
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    // --- ブロック追加 ---

    public void AddTimedBlock(TodayQueueTask task, DateTime startAt, int durationSlots = 2)
    {
        var block = new ScheduleBlock
        {
            Kind = ScheduleBlockKind.Timed,
            TaskIdentity = BuildIdentity(task),
            ProjectShortName = task.ProjectShortName,
            TitleSnapshot = task.DisplayMainTitle,
            StartAt = startAt,
            DurationSlots = durationSlots,
            ColorKey = GetColorKey(task.ProjectShortName),
        };
        _scheduleService.AddBlock(block);
        TimedBlocks.Add(block);
        _ = LoadWeekAsync(); // 左パネルのスケジュール済みフラグを更新
    }

    public void AddAllDayBlock(TodayQueueTask task, DateTime startDate, DateTime endDate)
    {
        var block = new ScheduleBlock
        {
            Kind = ScheduleBlockKind.AllDay,
            TaskIdentity = BuildIdentity(task),
            ProjectShortName = task.ProjectShortName,
            TitleSnapshot = task.DisplayMainTitle,
            StartDate = startDate.Date,
            EndDate = endDate.Date,
            ColorKey = GetColorKey(task.ProjectShortName),
        };
        _scheduleService.AddBlock(block);
        AllDayBlocks.Add(block);
        _ = LoadWeekAsync(); // 左パネルのスケジュール済みフラグを更新
    }

    // --- ブロック更新 ---

    public void MoveTimedBlock(string blockId, DateTime newStartAt)
    {
        var block = TimedBlocks.FirstOrDefault(b => b.Id == blockId);
        if (block == null) return;
        block.StartAt = newStartAt;
        _scheduleService.UpdateBlock(block);
    }

    public void MoveAllDayBlock(string blockId, int dayDelta)
    {
        var block = AllDayBlocks.FirstOrDefault(b => b.Id == blockId);
        if (block == null) return;
        block.StartDate = block.StartDate!.Value.AddDays(dayDelta);
        block.EndDate = block.EndDate!.Value.AddDays(dayDelta);
        _scheduleService.UpdateBlock(block);
    }

    public void ResizeTimedBlock(string blockId, DateTime? newStartAt, int? newDurationSlots)
    {
        var block = TimedBlocks.FirstOrDefault(b => b.Id == blockId);
        if (block == null) return;
        if (newStartAt.HasValue) block.StartAt = newStartAt;
        if (newDurationSlots.HasValue && newDurationSlots.Value >= 1)
            block.DurationSlots = newDurationSlots.Value;
        _scheduleService.UpdateBlock(block);
    }

    public void ResizeAllDayBlock(string blockId, DateTime? newStartDate, DateTime? newEndDate)
    {
        var block = AllDayBlocks.FirstOrDefault(b => b.Id == blockId);
        if (block == null) return;
        if (newStartDate.HasValue) block.StartDate = newStartDate.Value.Date;
        if (newEndDate.HasValue) block.EndDate = newEndDate.Value.Date;
        if (block.EndDate < block.StartDate) block.EndDate = block.StartDate;
        _scheduleService.UpdateBlock(block);
    }

    // --- ブロック削除 ---

    public void RemoveBlock(string blockId)
    {
        var timed = TimedBlocks.FirstOrDefault(b => b.Id == blockId);
        if (timed != null) TimedBlocks.Remove(timed);
        var allDay = AllDayBlocks.FirstOrDefault(b => b.Id == blockId);
        if (allDay != null) AllDayBlocks.Remove(allDay);
        _scheduleService.RemoveBlock(blockId);
        _ = LoadWeekAsync();
    }

    // --- ユーティリティ ---

    public static string BuildIdentity(TodayQueueTask task)
    {
        if (!string.IsNullOrEmpty(task.AsanaTaskGid))
            return task.AsanaTaskGid;
        return $"{task.ProjectShortName}|{task.WorkstreamId ?? ""}|{task.Title}";
    }

    internal static DateTime GetMondayOf(DateTime date)
    {
        int diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        return date.Date.AddDays(-diff);
    }

    private void UpdateWeekRangeText()
    {
        var weekEnd = WeekStart.AddDays(6);
        WeekRangeText = $"{WeekStart:yyyy-MM-dd} - {weekEnd:MM/dd}";
    }

    private static readonly string[] ColorKeys =
        ["blue", "green", "orange", "purple", "teal", "pink", "cyan", "red"];

    public static string GetColorKey(string projectShortName)
    {
        var hash = Math.Abs(projectShortName.GetHashCode());
        return ColorKeys[hash % ColorKeys.Length];
    }
}
