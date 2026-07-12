using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Curia.Helpers;
using Curia.Models;
using Curia.Services;

namespace Curia.ViewModels;

/// <summary>タイムラインの1エントリ (通常行 or ギャップ行)</summary>
public partial class TimelineEntryItem : ObservableObject
{
    public bool IsGap { get; init; }
    public int GapDays { get; init; }           // IsGap == true のとき有効
    public DateTime Date { get; init; }          // IsGap == false のとき有効
    public string EntryType { get; init; } = ""; // "Focus" | "Decision" | "Work"
    public string Preview { get; init; } = "";
    public string FilePath { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string ProjectHiddenKey { get; init; } = "";

    [ObservableProperty]
    private bool isExpanded;

    [ObservableProperty]
    private string expandedContent = "";

    public string GapText => GapDays == 1 ? "-- 1 day gap --" : $"-- {GapDays} days gap --";
    public string DateText => Date.ToString("yyyy-MM-dd (ddd)");
    public string TypeBadge => EntryType switch
    {
        "Decision" => "[Decision]",
        "Work" => "[Work]",
        "Pomodoro" => "[Pomodoro]",
        _ => "[Focus]",
    };
    public string OpenButtonLabel => EntryType == "Work" ? "Open in Explorer" : "Open in Editor";
}

public class TimelineHeatmapCellItem
{
    public string ProjectHiddenKey { get; init; } = "";
    public string BucketLabel { get; init; } = "";
    public DateTime BucketDate { get; init; }
    public int Count { get; init; }
    public double Intensity { get; init; }
    public string ToolTipText { get; init; } = "";
    public string DominantType { get; init; } = "";  // "Focus" | "Decision" | "Work"
    public int FocusCount { get; init; }
    public int DecisionCount { get; init; }
    public int WorkCount { get; init; }
}

public class TimelineHeatmapBucketItem
{
    public DateTime Date { get; init; }
    public string Label { get; init; } = "";
    public string ShortLabel { get; init; } = "";
}

public class TimelineHeatmapRowItem
{
    public string ProjectName { get; init; } = "";
    public string ProjectKey { get; init; } = "";
    public ObservableCollection<TimelineHeatmapCellItem> Cells { get; } = [];
}

public partial class TimelineViewModel : ObservableObject
{
    private readonly ProjectDiscoveryService _discoveryService;
    private readonly ConfigService _configService;
    private List<ProjectInfo> _allProjects = [];
    private List<string> _hiddenKeys = [];
    private List<TimelineRawEntry> _cachedRawEntries = [];
    private CancellationTokenSource? _heatmapCts;
    private readonly AsyncInitializationGate _initialization = new();
    private Exception? _initializationError;

    // focus_history プレビューキャッシュ: filePath → (LastWriteTimeUtc, previewText)
    // ファイルが更新されていなければ再読み込みしない
    private static readonly Dictionary<string, (DateTime Lwt, string Preview)> _previewCache = [];
    private static readonly object _previewCacheLock = new();

    [ObservableProperty]
    private ObservableCollection<ProjectInfo> projects = [];

    [ObservableProperty]
    private bool showHidden = false;

    public int HiddenCount => _allProjects.Count(p => _hiddenKeys.Contains(p.HiddenKey));

    [ObservableProperty]
    private ProjectInfo? selectedProject;

    [ObservableProperty]
    private int daysBack = 30;  // 0 = All

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private bool isGraphLoading;

    [ObservableProperty]
    private string statsText = "Loading...";

    [ObservableProperty]
    private string graphStatsText = "";

    [ObservableProperty]
    private string graphEmptyText = "No timeline events found for this range.";

    [ObservableProperty]
    private string selectedGraphScope = "All projects";

    [ObservableProperty]
    private bool showFocus = true;

    [ObservableProperty]
    private bool showDecision = true;

    [ObservableProperty]
    private bool showWork = true;

    [ObservableProperty]
    private bool showPomodoro = true;

    [ObservableProperty]
    private string searchText = "";

    /// <summary>プロジェクト未選択時は全プロジェクト横断表示モード。</summary>
    public bool IsCrossProjectMode => SelectedProject == null;

    public ObservableCollection<TimelineEntryItem> Entries { get; } = [];
    public ObservableCollection<TimelineHeatmapBucketItem> HeatmapBuckets { get; } = [];
    public ObservableCollection<TimelineHeatmapRowItem> HeatmapRows { get; } = [];
    public ObservableCollection<string> GraphScopes { get; } = ["All projects", "Selected project"];

    /// <summary>エントリクリック時のコールバック (ProjectInfo, filePath)</summary>
    public Action<ProjectInfo, string>? OnOpenFileInEditor;

    /// <summary>Dashboard からジャンプ時のターゲットキー (InitAsync後にSelectedProjectへ反映)</summary>
    public string? NavigateToProjectKey { get; set; }

    public TimelineViewModel(ProjectDiscoveryService discoveryService, ConfigService configService)
    {
        _discoveryService = discoveryService;
        _configService = configService;
    }

    /// <summary>ページ表示時に呼ぶ。プロジェクトリストを読み込む。</summary>
    public async Task InitAsync()
    {
        _initializationError = null;
        IsLoading = true;
        try
        {
            var infos = await Task.Run(() => _discoveryService.GetProjectInfoList());
            _hiddenKeys = _configService.LoadHiddenProjects();
            _allProjects = infos;
            ApplyProjectFilter();
            OnPropertyChanged(nameof(HiddenCount));
        }
        catch (Exception ex)
        {
            _initializationError = ex;
            Debug.WriteLine($"[Timeline] InitAsync failed: {ex}");
        }
        finally
        {
            IsLoading = false;
        }

        await Task.WhenAll(LoadEntriesAsync(), LoadHeatmapAsync());
    }

    public Task EnsureInitializedAsync() => _initialization.EnsureAsync(async () =>
    {
        await InitAsync();
        if (_initializationError != null)
            throw new InvalidOperationException("Timeline initialization failed.", _initializationError);
    });

    partial void OnShowHiddenChanged(bool value)
    {
        ApplyProjectFilter();
        _ = LoadHeatmapAsync();
    }

    private void ApplyProjectFilter()
    {
        var current = SelectedProject?.HiddenKey;
        var src = ShowHidden ? _allProjects : _allProjects.Where(p => !_hiddenKeys.Contains(p.HiddenKey));
        Projects.Clear();
        foreach (var p in src) Projects.Add(p);
        // 選択中プロジェクトがフィルタで消えた場合は選択解除
        if (current != null && !Projects.Any(p => p.HiddenKey == current))
            SelectedProject = null;
    }

    partial void OnSelectedProjectChanged(ProjectInfo? value)
    {
        OnPropertyChanged(nameof(IsCrossProjectMode));
        _ = LoadEntriesAsync();
        _ = LoadHeatmapAsync();
    }

    partial void OnDaysBackChanged(int value)
    {
        _ = LoadEntriesAsync();
        _ = LoadHeatmapAsync();
    }

    partial void OnSelectedGraphScopeChanged(string value)
        => _ = LoadHeatmapAsync();

    partial void OnShowFocusChanged(bool value)    => ApplyEntryFilters();
    partial void OnShowDecisionChanged(bool value) => ApplyEntryFilters();
    partial void OnShowWorkChanged(bool value)     => ApplyEntryFilters();
    partial void OnShowPomodoroChanged(bool value) => ApplyEntryFilters();
    partial void OnSearchTextChanged(string value) => ApplyEntryFilters();

    /// <summary>ファイルIOを行い rawEntries をキャッシュしてフィルタを適用する。</summary>
    public async Task LoadEntriesAsync()
    {
        Entries.Clear();
        StatsText = "Loading...";
        IsLoading = true;

        var projectList = Projects.ToList();
        var selectedProject = SelectedProject;
        var daysBack = DaysBack;

        try
        {
            _cachedRawEntries = await Task.Run(() =>
            {
                var cutoff = daysBack > 0 ? DateTime.Today.AddDays(-daysBack) : DateTime.MinValue;
                var raw = new List<TimelineRawEntry>();

                if (selectedProject != null)
                {
                    raw.AddRange(BuildRawEntries(selectedProject, cutoff));
                }
                else
                {
                    foreach (var p in projectList)
                        raw.AddRange(BuildRawEntries(p, cutoff));
                }
                return raw;
            });

            ApplyEntryFilters();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Timeline] LoadEntriesAsync failed: {ex}");
            StatsText = $"[ERROR] {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>キャッシュ済み rawEntries にタイプ/検索フィルタを適用してエントリリストを再構築する (ファイルIO なし)。</summary>
    private void ApplyEntryFilters()
    {
        var filtered = _cachedRawEntries.AsEnumerable();

        if (!ShowFocus)    filtered = filtered.Where(e => e.Type != "Focus");
        if (!ShowDecision) filtered = filtered.Where(e => e.Type != "Decision");
        if (!ShowWork)     filtered = filtered.Where(e => e.Type != "Work");
        if (!ShowPomodoro) filtered = filtered.Where(e => e.Type != "Pomodoro");

        var search = SearchText.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            filtered = filtered.Where(e =>
                e.Topic.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                e.PreviewText.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        var rawList = filtered
            .OrderByDescending(e => e.Date)
            .ThenBy(e => e.Type, StringComparer.Ordinal)
            .ToList();

        var result = new List<TimelineEntryItem>();
        DateTime? prevDate = null;

        foreach (var raw in rawList)
        {
            if (prevDate.HasValue && prevDate.Value.Date != raw.Date.Date)
            {
                int gap = (int)(prevDate.Value.Date - raw.Date.Date).TotalDays - 1;
                if (gap > 0)
                    result.Add(new TimelineEntryItem { IsGap = true, GapDays = gap });
            }

            string preview = raw.Type switch
            {
                "Focus"    => "[Focus] " + raw.PreviewText,
                "Decision" => "[Decision] " + raw.Topic,
                "Work"     => "[Work] " + raw.Topic,
                "Pomodoro" => "[Pomodoro] " + raw.PreviewText,
                _          => raw.Topic,
            };

            result.Add(new TimelineEntryItem
            {
                Date             = raw.Date,
                EntryType        = raw.Type,
                Preview          = preview,
                FilePath         = raw.Path,
                ProjectName      = raw.ProjectName,
                ProjectHiddenKey = raw.ProjectHiddenKey,
            });

            prevDate = raw.Date;
        }

        Entries.Clear();
        foreach (var e in result) Entries.Add(e);

        if (rawList.Count == 0)
        {
            StatsText = "Total: 0 entries";
            return;
        }

        int focusCount    = rawList.Count(e => e.Type == "Focus");
        int decisionCount = rawList.Count(e => e.Type == "Decision");
        int pomodoroCount = rawList.Count(e => e.Type == "Pomodoro");
        int workCount     = rawList.Count(e => e.Type == "Work");
        var uniqueDays    = rawList.Select(e => e.Date.Date).Distinct().Count();
        var newest = rawList.First().Date.ToString("yyyy-MM-dd");
        var oldest = rawList.Last().Date.ToString("yyyy-MM-dd");
        string typeSummary = $"Focus: {focusCount}  Decision: {decisionCount}  Work: {workCount}  Pomodoro: {pomodoroCount}";

        if (DaysBack > 0)
        {
            double activeRate = uniqueDays * 100.0 / DaysBack;
            StatsText = $"Total: {rawList.Count} entries  ({typeSummary}) | Active: {uniqueDays} days | Period: {oldest} ~ {newest} | Rate: {activeRate:F1}%";
        }
        else
        {
            StatsText = $"Total: {rawList.Count} entries  ({typeSummary}) | Active: {uniqueDays} days | Period: {oldest} ~ {newest}";
        }
    }

    public async Task LoadHeatmapAsync()
    {
        // 前回の実行をキャンセルして重複ロードを防ぐ
        _heatmapCts?.Cancel();
        _heatmapCts?.Dispose();
        _heatmapCts = new CancellationTokenSource();
        var cts = _heatmapCts;

        HeatmapBuckets.Clear();
        HeatmapRows.Clear();
        GraphStatsText = "";
        GraphEmptyText = "No timeline events found for this range.";

        if (Projects.Count == 0)
        {
            GraphEmptyText = "No projects available.";
            return;
        }

        IsGraphLoading = true;
        try
        {
            var snapshot = Projects.ToList();
            var result = await Task.Run(() => BuildHeatmap(
                snapshot,
                SelectedProject,
                DaysBack,
                SelectedGraphScope), cts.Token);

            if (cts.IsCancellationRequested)
                return;

            foreach (var bucket in result.Buckets)
                HeatmapBuckets.Add(bucket);
            foreach (var row in result.Rows)
                HeatmapRows.Add(row);

            GraphStatsText = result.Stats;
            GraphEmptyText = result.EmptyText;
        }
        catch (OperationCanceledException)
        {
            // 後続の呼び出しがあるため無視
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Timeline] LoadHeatmapAsync failed: {ex}");
            GraphEmptyText = $"Failed to load graph: {ex.Message}";
        }
        finally
        {
            if (!cts.IsCancellationRequested)
                IsGraphLoading = false;
        }
    }

    /// <summary>エントリクリック時に呼ぶ。展開/折りたたみをトグルし、展開時はファイル内容を読み込む。</summary>
    public async Task ToggleEntryAsync(TimelineEntryItem entry)
    {
        if (entry.IsGap || string.IsNullOrEmpty(entry.FilePath))
            return;

        if (entry.IsExpanded)
        {
            entry.IsExpanded = false;
            return;
        }

        if (entry.EntryType == "Work")
        {
            entry.ExpandedContent = entry.FilePath;
            entry.IsExpanded = true;
            return;
        }

        try
        {
            var content = await Task.Run(() =>
            {
                var (text, _) = EncodingDetector.ReadFile(entry.FilePath);
                var lines = text.Split('\n').Take(30);
                return string.Join("\n", lines).TrimEnd();
            });
            entry.ExpandedContent = content;
        }
        catch (Exception ex)
        {
            entry.ExpandedContent = $"(Failed to load: {ex.Message})";
        }
        entry.IsExpanded = true;
    }

    /// <summary>エディタ/エクスプローラーで開くボタン押下時に呼ぶ。</summary>
    public void OpenEntryInEditor(TimelineEntryItem entry)
    {
        if (entry.IsGap || string.IsNullOrEmpty(entry.FilePath))
            return;

        if (entry.EntryType == "Work")
        {
            if (Directory.Exists(entry.FilePath))
                Process.Start("explorer.exe", $"\"{entry.FilePath}\"");
            return;
        }

        var project = Projects.FirstOrDefault(p => p.HiddenKey == entry.ProjectHiddenKey) ?? SelectedProject;
        if (project == null)
            return;

        OnOpenFileInEditor?.Invoke(project, entry.FilePath);
    }

    public void OpenHeatmapCell(TimelineHeatmapCellItem? cell)
    {
        if (cell == null || cell.Count <= 0)
            return;

        var project = Projects.FirstOrDefault(p => p.HiddenKey == cell.ProjectHiddenKey);
        if (project == null)
            return;

        var focusPath = Path.Combine(project.AiContextContentPath, "focus_history", $"{cell.BucketDate:yyyy-MM-dd}.md");
        if (File.Exists(focusPath))
        {
            OnOpenFileInEditor?.Invoke(project, focusPath);
            return;
        }

        var decisionDir = Path.Combine(project.AiContextContentPath, "decision_log");
        if (!Directory.Exists(decisionDir))
            return;

        var pattern = $"{cell.BucketDate:yyyy-MM-dd}_*.md";
        var decisionPath = Directory.EnumerateFiles(decisionDir, pattern)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(decisionPath))
            OnOpenFileInEditor?.Invoke(project, decisionPath);
    }

    // ----- プライベート -----

    private static TimelineHeatmapBuildResult BuildHeatmap(
        IReadOnlyList<ProjectInfo> allProjects,
        ProjectInfo? selectedProject,
        int daysBack,
        string graphScope)
    {
        var scopeProjects = graphScope == "Selected project" && selectedProject != null
            ? new List<ProjectInfo> { selectedProject }
            : [.. allProjects];

        if (scopeProjects.Count == 0)
        {
            return new TimelineHeatmapBuildResult
            {
                EmptyText = "No project selected.",
            };
        }

        var cutoff = daysBack > 0
            ? DateTime.Today.AddDays(-daysBack)
            : DateTime.MinValue;

        var rawEntries = new List<TimelineRawEntry>();
        foreach (var project in scopeProjects)
            rawEntries.AddRange(BuildRawEntries(project, cutoff));

        if (rawEntries.Count == 0)
        {
            return new TimelineHeatmapBuildResult
            {
                EmptyText = "No timeline events found for this range.",
            };
        }

        DateTime startDate;
        DateTime endDate;

        if (daysBack > 0)
        {
            endDate = DateTime.Today;
            startDate = DateTime.Today.AddDays(-(daysBack - 1));
        }
        else
        {
            startDate = rawEntries.Min(e => e.Date).Date;
            endDate = rawEntries.Max(e => e.Date).Date;
        }

        if (startDate > endDate)
            startDate = endDate;

        var bucketDates = new List<DateTime>();
        for (var d = startDate.Date; d <= endDate.Date; d = d.AddDays(1))
            bucketDates.Add(d);

        var buckets = bucketDates
            .Select((date, index) => new TimelineHeatmapBucketItem
            {
                Date = date,
                Label = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ShortLabel = BuildShortBucketLabel(date, index, bucketDates.Count),
            })
            .ToList();

        // タイプ別集計
        var grouped = rawEntries
            .GroupBy(e => (e.ProjectHiddenKey, Date: e.Date.Date))
            .ToDictionary(g => g.Key, g => new
            {
                Total    = g.Count(),
                Focus    = g.Count(e => e.Type == "Focus"),
                Decision = g.Count(e => e.Type == "Decision"),
                Work     = g.Count(e => e.Type == "Work"),
            });

        int maxCount = grouped.Count > 0 ? grouped.Values.Max(v => v.Total) : 0;

        var rows = scopeProjects
            .Select(project =>
            {
                var row = new TimelineHeatmapRowItem
                {
                    ProjectName = project.DisplayName,
                    ProjectKey = project.HiddenKey,
                };

                foreach (var bucket in buckets)
                {
                    grouped.TryGetValue((project.HiddenKey, bucket.Date.Date), out var counts);
                    int count    = counts?.Total    ?? 0;
                    int focus    = counts?.Focus    ?? 0;
                    int decision = counts?.Decision ?? 0;
                    int work     = counts?.Work     ?? 0;

                    string dominantType = "";
                    if (count > 0)
                    {
                        if (focus >= decision && focus >= work)  dominantType = "Focus";
                        else if (decision >= work)               dominantType = "Decision";
                        else                                     dominantType = "Work";
                    }

                    var intensity = maxCount > 0 ? (double)count / maxCount : 0d;
                    row.Cells.Add(new TimelineHeatmapCellItem
                    {
                        ProjectHiddenKey = project.HiddenKey,
                        BucketLabel      = bucket.Label,
                        BucketDate       = bucket.Date,
                        Count            = count,
                        Intensity        = intensity,
                        DominantType     = dominantType,
                        FocusCount       = focus,
                        DecisionCount    = decision,
                        WorkCount        = work,
                        ToolTipText      = count > 0
                            ? $"{project.DisplayName}\n{bucket.Label}\nFocus: {focus}  Decision: {decision}  Work: {work}\nClick to open file"
                            : $"{project.DisplayName}\n{bucket.Label}\nEvents: 0",
                    });
                }

                return row;
            })
            .ToList();

        var stats = $"Projects: {rows.Count} | Buckets: {buckets.Count} days | Events: {rawEntries.Count} | Peak/day: {maxCount}";

        return new TimelineHeatmapBuildResult
        {
            Buckets = buckets,
            Rows = rows,
            Stats = stats,
            EmptyText = "No timeline events found for this range.",
        };
    }

    private static string BuildShortBucketLabel(DateTime date, int index, int totalCount)
    {
        if (index is 0 || index == totalCount - 1)
            return date.ToString("M/d", CultureInfo.InvariantCulture);

        return date.Day == 1
            ? date.ToString("M/d", CultureInfo.InvariantCulture)
            : "";
    }

    private static List<TimelineRawEntry> BuildRawEntries(ProjectInfo project, DateTime cutoff)
    {
        var histDir = Path.Combine(project.AiContextContentPath, "focus_history");
        var logDir = Path.Combine(project.AiContextContentPath, "decision_log");

        var rawEntries = new List<TimelineRawEntry>();

        // Focus History files
        if (Directory.Exists(histDir))
        {
            foreach (var file in Directory.EnumerateFiles(histDir, "*.md"))
            {
                var baseName = Path.GetFileNameWithoutExtension(file);
                if (DateTime.TryParseExact(baseName, "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date)
                    && date >= cutoff)
                {
                    rawEntries.Add(new TimelineRawEntry
                    {
                        Date             = date,
                        Path             = file,
                        Type             = "Focus",
                        Topic            = "",
                        PreviewText      = GetFocusPreview(file),
                        ProjectName      = project.Name,
                        ProjectHiddenKey = project.HiddenKey,
                    });
                }
            }
        }

        // Decision Log files
        if (Directory.Exists(logDir))
        {
            foreach (var file in Directory.EnumerateFiles(logDir, "*.md"))
            {
                var baseName = Path.GetFileNameWithoutExtension(file);
                if (baseName == "TEMPLATE")
                    continue;

                var match = Regex.Match(baseName, @"^(\d{4}-\d{2}-\d{2})_(.+)$");
                if (!match.Success)
                    continue;

                if (DateTime.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date)
                    && date >= cutoff)
                {
                    rawEntries.Add(new TimelineRawEntry
                    {
                        Date             = date,
                        Path             = file,
                        Type             = "Decision",
                        Topic            = match.Groups[2].Value,
                        PreviewText      = "",
                        ProjectName      = project.Name,
                        ProjectHiddenKey = project.HiddenKey,
                    });
                }
            }
        }

        // Work folders under shared/_work/
        var workRoot = Path.Combine(project.Path, "shared", "_work");
        if (Directory.Exists(workRoot))
            ScanWorkFolders(workRoot, project, cutoff, rawEntries);

        // Pomodoro logs under focus_history/pomodoro/
        var pomodoroDir = Path.Combine(project.AiContextContentPath, "focus_history", "pomodoro");
        if (Directory.Exists(pomodoroDir))
        {
            foreach (var file in Directory.EnumerateFiles(pomodoroDir, "????-??-??.md"))
            {
                var baseName = Path.GetFileNameWithoutExtension(file);
                if (DateTime.TryParseExact(baseName, "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date)
                    && date >= cutoff)
                {
                    rawEntries.Add(new TimelineRawEntry
                    {
                        Date             = date,
                        Path             = file,
                        Type             = "Pomodoro",
                        Topic            = "",
                        PreviewText      = GetPomodoroPreview(file),
                        ProjectName      = project.Name,
                        ProjectHiddenKey = project.HiddenKey,
                    });
                }
            }
        }

        return rawEntries;
    }

    private static string GetPomodoroPreview(string filePath)
    {
        try
        {
            var lines = File.ReadAllLines(filePath);
            int completed = lines.Count(l => l.StartsWith("- ", StringComparison.Ordinal) && l.Contains("completed"));
            int totalMin = 0;
            foreach (var line in lines.Where(l => l.StartsWith("- ", StringComparison.Ordinal) && l.Contains("min ")))
            {
                var m = Regex.Match(line, @"(\d+)min");
                if (m.Success) totalMin += int.Parse(m.Groups[1].Value);
            }
            return $"{completed} sessions / {totalMin} min";
        }
        catch
        {
            return "(No preview)";
        }
    }

    /// <summary>
    /// shared/_work/ 以下の日付フォルダをスキャンして rawEntries に追加する。
    /// General:    _work/{year(4d)}/{yearMonth(6d)}/{yyyyMMdd}_{name}
    /// Workstream: _work/{workstreamId}/{yearMonth(6d)}/{yyyyMMdd}_{name}
    /// </summary>
    private static void ScanWorkFolders(string workRoot, ProjectInfo project, DateTime cutoff, List<TimelineRawEntry> rawEntries)
    {
        string[] l1Dirs;
        try { l1Dirs = Directory.GetDirectories(workRoot); }
        catch (Exception ex) { Debug.WriteLine($"[Timeline] _work L1 error: {ex.Message}"); return; }

        foreach (var l1 in l1Dirs)
        {
            string[] l2Dirs;
            try { l2Dirs = Directory.GetDirectories(l1); }
            catch { continue; }

            foreach (var l2 in l2Dirs)
            {
                if (!Regex.IsMatch(Path.GetFileName(l2)!, @"^\d{6}$"))
                    continue;

                string[] l3Dirs;
                try { l3Dirs = Directory.GetDirectories(l2); }
                catch { continue; }

                foreach (var l3 in l3Dirs)
                {
                    var name = Path.GetFileName(l3)!;
                    if (!Regex.IsMatch(name, @"^\d{8}_"))
                        continue;

                    if (!DateTime.TryParseExact(name[..8], "yyyyMMdd",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out var date)
                        || date < cutoff)
                        continue;

                    rawEntries.Add(new TimelineRawEntry
                    {
                        Date             = date,
                        Path             = l3,
                        Type             = "Work",
                        Topic            = name[9..],
                        PreviewText      = "",
                        ProjectName      = project.Name,
                        ProjectHiddenKey = project.HiddenKey,
                    });
                }
            }
        }
    }

    /// <summary>focus_history ファイルから「今やってること」セクションの最初の箇条書きを取得。
    /// ファイルの LastWriteTimeUtc が変わっていなければキャッシュ値を返す。</summary>
    private static string GetFocusPreview(string filePath)
    {
        try
        {
            var lwt = File.GetLastWriteTimeUtc(filePath);
            lock (_previewCacheLock)
            {
                if (_previewCache.TryGetValue(filePath, out var cached) && cached.Lwt == lwt)
                    return cached.Preview;
            }

            var (content, _) = EncodingDetector.ReadFile(filePath);
            var lines = content.Split('\n');
            bool inSection = false;
            string result = "";

            foreach (var raw in lines)
            {
                var line = raw.TrimEnd('\r');
                if (Regex.IsMatch(line, @"^## (今やってること|Now doing)"))
                {
                    inSection = true;
                    continue;
                }

                if (inSection && Regex.IsMatch(line, @"^## "))
                    break;

                if (inSection && Regex.IsMatch(line, @"^\s*-\s+\S"))
                {
                    var preview = Regex.Replace(line, @"^\s*-\s+", "").Trim();
                    result = preview.Length > 80 ? preview[..77] + "..." : preview;
                    break;
                }
            }

            // fallback: first non-empty line
            if (result.Length == 0)
            {
                foreach (var raw in lines)
                {
                    var trimmed = raw.TrimEnd('\r').Trim();
                    if (trimmed.Length > 0
                        && !trimmed.StartsWith('#')
                        && !trimmed.StartsWith("<!--", StringComparison.Ordinal)
                        && !trimmed.StartsWith("---", StringComparison.Ordinal)
                        && trimmed != "-")
                    {
                        result = trimmed.Length > 80 ? trimmed[..77] + "..." : trimmed;
                        break;
                    }
                }
            }

            lock (_previewCacheLock)
                _previewCache[filePath] = (lwt, result);

            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Timeline] Failed to read focus preview: {filePath}: {ex}");
        }

        return "";
    }

    private sealed class TimelineRawEntry
    {
        public DateTime Date { get; init; }
        public string Path { get; init; } = "";
        public string Type { get; init; } = "";
        public string Topic { get; init; } = "";
        public string PreviewText { get; init; } = "";
        public string ProjectName { get; init; } = "";
        public string ProjectHiddenKey { get; init; } = "";
    }

    private sealed class TimelineHeatmapBuildResult
    {
        public List<TimelineHeatmapBucketItem> Buckets { get; init; } = [];
        public List<TimelineHeatmapRowItem> Rows { get; init; } = [];
        public string Stats { get; init; } = "";
        public string EmptyText { get; init; } = "No timeline events found for this range.";
    }
}
