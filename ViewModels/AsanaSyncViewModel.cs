using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Curia.Models;
using Curia.Helpers;
using Curia.Services;

namespace Curia.ViewModels;

public partial class AsanaSyncViewModel : ObservableObject
{
    private readonly ConfigService _configService;
    private readonly ProjectDiscoveryService _discoveryService;
    private readonly AsanaSyncService _asanaSyncService;
    private System.Timers.Timer? _syncTimer;
    private readonly AsyncInitializationGate _initialization = new();

    [ObservableProperty]
    private ObservableCollection<ProjectInfo> projects = [];

    [ObservableProperty]
    private ProjectInfo? selectedProject;

    [ObservableProperty]
    private bool isSyncing;

    [ObservableProperty]
    private bool scheduleEnabled;

    [ObservableProperty]
    private int intervalMin = 60;

    [ObservableProperty]
    private string lastSyncTime = "--";

    public ObservableCollection<SyncLogEntry> LogEntries { get; } = [];

    // Auto Sync 有効時にログが無制限に増えて描画が重くなるのを防ぐ上限
    private const int MaxLogEntries = 500;

    private string _lineBuffer = "";

    [ObservableProperty]
    private string asanaConfigGids = "";

    [ObservableProperty]
    private string asanaConfigAliases = "";

    [ObservableProperty]
    private string asanaConfigWorkstreamMap = "";

    [ObservableProperty]
    private string asanaConfigWorkstreamFieldName = "workstream-id";

    [ObservableProperty]
    private string asanaConfigStatus = "";

    [ObservableProperty]
    private bool skipHiddenProjects = true;

    // Team View 設定
    [ObservableProperty]
    private bool teamViewEnabled;

    [ObservableProperty]
    private string teamViewProjectGids = "";

    [ObservableProperty]
    private string teamViewWorkstreamGids = "";

    public AsanaSyncViewModel(
        ConfigService configService,
        ProjectDiscoveryService discoveryService,
        AsanaSyncService asanaSyncService)
    {
        _configService = configService;
        _discoveryService = discoveryService;
        _asanaSyncService = asanaSyncService;
    }

    public void StartScheduler()
    {
        var settings = _configService.LoadSettings();
        if (settings.AsanaSync != null)
        {
            IntervalMin = settings.AsanaSync.IntervalMin > 0 ? settings.AsanaSync.IntervalMin : 60;
            SkipHiddenProjects = settings.AsanaSync.SkipHiddenProjects;
            ScheduleEnabled = settings.AsanaSync.Enabled;  // OnScheduleEnabledChanged でタイマー開始
        }
    }

    public async Task InitAsync()
    {
        // プロジェクトリスト読み込み
        var infos = await Task.Run(() => _discoveryService.GetProjectInfoList());
        Projects.Clear();
        foreach (var p in infos) Projects.Add(p);

        // スケジュール設定は StartScheduler() で読み込み済み
    }

    public Task EnsureInitializedAsync() => _initialization.EnsureAsync(InitAsync);

    [RelayCommand]
    private async Task Sync() => await RunSyncAsync();

    [RelayCommand]
    private void ClearOutput()
    {
        LogEntries.Clear();
        _lineBuffer = "";
    }

    [RelayCommand]
    private void SaveSchedule()
    {
        var settings = _configService.LoadSettings();
        settings.AsanaSync ??= new AsanaSyncConfig();
        settings.AsanaSync.Enabled = ScheduleEnabled;
        settings.AsanaSync.IntervalMin = IntervalMin;
        settings.AsanaSync.SkipHiddenProjects = SkipHiddenProjects;
        _configService.SaveSettings(settings);

        UpdateScheduleTimer();

        var stateText = ScheduleEnabled ? "ON" : "OFF";
        AppendOutput($"[SAVED] Schedule: {stateText}, Interval: {IntervalMin} min\n");
    }

    partial void OnScheduleEnabledChanged(bool value) => UpdateScheduleTimer();

    [RelayCommand]
    private void LoadAsanaConfig()
    {
        if (SelectedProject == null)
        {
            AsanaConfigStatus = "Please select a project";
            return;
        }

        var configPath = ResolveAsanaConfigPath(SelectedProject);
        if (!File.Exists(configPath))
        {
            AsanaConfigGids = "";
            AsanaConfigAliases = "";
            AsanaConfigWorkstreamMap = "";
            AsanaConfigWorkstreamFieldName = "workstream-id";
            TeamViewEnabled = false;
            TeamViewProjectGids = "";
            TeamViewWorkstreamGids = "";
            AsanaConfigStatus = "New file (not yet created)";
            return;
        }

        try
        {
            var content = File.ReadAllText(configPath, new UTF8Encoding(false));
            var config = JsonSerializer.Deserialize<AsanaProjectConfig>(content) ?? new AsanaProjectConfig();
            AsanaConfigGids = string.Join("\n", config.AsanaProjectGids);
            AsanaConfigAliases = string.Join("\n", config.AnkenAliases);
            AsanaConfigWorkstreamMap = string.Join("\n",
                config.WorkstreamProjectMap
                    .OrderBy(kv => kv.Key)
                    .Select(kv => $"{kv.Key}={kv.Value}"));
            AsanaConfigWorkstreamFieldName = string.IsNullOrWhiteSpace(config.WorkstreamFieldName)
                ? "workstream-id"
                : config.WorkstreamFieldName.Trim();

            // Team View
            var tv = config.TeamView;
            TeamViewEnabled = tv?.Enabled == true;
            TeamViewProjectGids = tv != null ? string.Join("\n", tv.ProjectGids) : "";
            TeamViewWorkstreamGids = tv != null
                ? string.Join("\n", tv.WorkstreamProjectGids
                    .OrderBy(kv => kv.Key)
                    .Select(kv => $"{kv.Key}={string.Join(",", kv.Value)}"))
                : "";

            AsanaConfigStatus = "Loaded";
        }
        catch (Exception ex)
        {
            AsanaConfigStatus = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void SaveAsanaConfig()
    {
        if (SelectedProject == null)
        {
            AsanaConfigStatus = "Please select a project";
            return;
        }

        var configPath = ResolveAsanaConfigPath(SelectedProject);
        try
        {
            var dir = Path.GetDirectoryName(configPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var gids = AsanaConfigGids
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            var aliases = AsanaConfigAliases
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

            var map = AsanaConfigWorkstreamMap
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Select(ParseWorkstreamMapLine)
                .Where(x => x != null)
                .GroupBy(x => x!.Value.ProjectGid, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last()!.Value.WorkstreamId, StringComparer.OrdinalIgnoreCase);

            var fieldName = string.IsNullOrWhiteSpace(AsanaConfigWorkstreamFieldName)
                ? "workstream-id"
                : AsanaConfigWorkstreamFieldName.Trim();

            // Team View
            TeamViewConfig? teamView = null;
            if (TeamViewEnabled || !string.IsNullOrWhiteSpace(TeamViewProjectGids) || !string.IsNullOrWhiteSpace(TeamViewWorkstreamGids))
            {
                var tvGids = TeamViewProjectGids
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

                var tvWsGids = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in TeamViewWorkstreamGids
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0))
                {
                    var eqIdx = line.IndexOf('=');
                    if (eqIdx <= 0) continue;
                    var wsId = line[..eqIdx].Trim();
                    var wsGids = line[(eqIdx + 1)..].Split(',')
                        .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                    if (wsId.Length > 0 && wsGids.Count > 0)
                        tvWsGids[wsId] = wsGids;
                }

                teamView = new TeamViewConfig
                {
                    Enabled = TeamViewEnabled,
                    ProjectGids = tvGids,
                    WorkstreamProjectGids = tvWsGids
                };
            }

            var config = new AsanaProjectConfig
            {
                AsanaProjectGids = gids,
                AnkenAliases = aliases,
                WorkstreamProjectMap = map,
                WorkstreamFieldName = fieldName,
                TeamView = teamView
            };
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json, new UTF8Encoding(false));
            AsanaConfigStatus = $"Saved {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            AsanaConfigStatus = $"Error: {ex.Message}";
        }
    }

    // ----- プライベート -----

    private static readonly Regex StepRx        = new(@"^\[(\d+/\d+)\]\s+(.+)$",                RegexOptions.Compiled);
    private static readonly Regex SectionRx      = new(@"^\s*---\s+(.+?)\s+---\s*$",             RegexOptions.Compiled);
    private static readonly Regex SectionEqRx    = new(@"^\s*===\s+(.+?)\s+===\s*$",             RegexOptions.Compiled);
    private static readonly Regex FetchingRx     = new(@"^Fetching:\s+(.+)\s+\((\d{10,})\)$",   RegexOptions.Compiled);
    private static readonly Regex FetchResultRx  = new(@"^->\s+(\d+)\s+tasks\s+\((.+?)\)$",     RegexOptions.Compiled);
    private static readonly Regex FoundRx        = new(@"^Found:\s+(.+?)\s+\((.+?)\)$",          RegexOptions.Compiled);
    private static readonly Regex OutputRx       = new(@"^\s*Output:\s+(.+)$",                    RegexOptions.Compiled);

    private static SyncLogEntry ParseLine(string line)
    {
        var trimmed = line.Trim();

        if (string.IsNullOrEmpty(trimmed))
            return new SyncLogEntry { Kind = SyncLogEntryKind.Empty };

        if (trimmed == "---")
            return new SyncLogEntry { Kind = SyncLogEntryKind.Separator };

        if (trimmed.StartsWith(">>> "))
            return new SyncLogEntry { Kind = SyncLogEntryKind.Header, Text = trimmed[4..] };

        var stepM = StepRx.Match(trimmed);
        if (stepM.Success)
            return new SyncLogEntry { Kind = SyncLogEntryKind.Step, Badge = stepM.Groups[1].Value, Text = stepM.Groups[2].Value };

        var secM = SectionRx.Match(trimmed);
        if (secM.Success)
        {
            var name = secM.Groups[1].Value;
            return name.StartsWith("Done", StringComparison.OrdinalIgnoreCase)
                ? new SyncLogEntry { Kind = SyncLogEntryKind.Done, Text = name }
                : new SyncLogEntry { Kind = SyncLogEntryKind.Section, Text = name };
        }

        var secEqM = SectionEqRx.Match(trimmed);
        if (secEqM.Success)
            return new SyncLogEntry { Kind = SyncLogEntryKind.Section, Text = secEqM.Groups[1].Value };

        if (trimmed.StartsWith("(no ", StringComparison.OrdinalIgnoreCase) && trimmed.EndsWith(")"))
            return new SyncLogEntry { Kind = SyncLogEntryKind.Skipped, Text = trimmed.Trim('(', ')') };

        if (trimmed.StartsWith("[ERROR]", StringComparison.OrdinalIgnoreCase))
            return new SyncLogEntry { Kind = SyncLogEntryKind.Error, Text = trimmed };

        if (trimmed.Contains("Sync complete", StringComparison.OrdinalIgnoreCase))
            return new SyncLogEntry { Kind = SyncLogEntryKind.Done, Text = trimmed };

        var outputM = OutputRx.Match(trimmed);
        if (outputM.Success)
        {
            var path = outputM.Groups[1].Value.Trim();
            var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var display = parts.Length >= 2 ? string.Join("/", parts[^2..]) : Path.GetFileName(path);
            return new SyncLogEntry { Kind = SyncLogEntryKind.Output, Text = display, SubText = path };
        }

        var fetchingM = FetchingRx.Match(trimmed);
        if (fetchingM.Success)
            return new SyncLogEntry { Kind = SyncLogEntryKind.Fetching, Text = fetchingM.Groups[1].Value, SubText = fetchingM.Groups[2].Value };

        var fetchResultM = FetchResultRx.Match(trimmed);
        if (fetchResultM.Success)
            return new SyncLogEntry { Kind = SyncLogEntryKind.FetchResult, Text = $"{fetchResultM.Groups[1].Value} tasks", SubText = fetchResultM.Groups[2].Value };

        var foundM = FoundRx.Match(trimmed);
        if (foundM.Success)
            return new SyncLogEntry { Kind = SyncLogEntryKind.Found, Text = foundM.Groups[1].Value, SubText = foundM.Groups[2].Value };

        return new SyncLogEntry { Kind = SyncLogEntryKind.Info, Text = trimmed };
    }

    private async Task RunSyncAsync()
    {
        if (IsSyncing) return;

        IsSyncing = true;
        AppendOutput($">>> Asana Sync\n---\n");

        try
        {
            await Task.Run(async () => await _asanaSyncService.RunAsync(AppendOutput, SkipHiddenProjects));
            AppendOutput("\n--- Done (exit: 0) ---\n");
            LastSyncTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }
        catch (Exception ex)
        {
            AppendOutput($"\n[ERROR] {ex.Message}\n--- Done (error) ---\n");
        }
        finally
        {
            IsSyncing = false;
        }
    }

    private void UpdateScheduleTimer()
    {
        _syncTimer?.Stop();
        _syncTimer?.Dispose();
        _syncTimer = null;

        if (!ScheduleEnabled || IntervalMin <= 0) return;

        _syncTimer = new System.Timers.Timer(IntervalMin * 60_000.0);
        _syncTimer.Elapsed += async (_, _) =>
        {
            AppendOutput("\n=== Scheduled Sync ===\n");
            await RunSyncAsync();
        };
        _syncTimer.AutoReset = true;
        _syncTimer.Start();
    }

    private void AppendOutput(string text)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _lineBuffer += text;
            int newlineIdx;
            while ((newlineIdx = _lineBuffer.IndexOf('\n')) >= 0)
            {
                var line = _lineBuffer[..newlineIdx].TrimEnd('\r');
                _lineBuffer = _lineBuffer[(newlineIdx + 1)..];
                LogEntries.Add(ParseLine(line));
            }

            while (LogEntries.Count > MaxLogEntries)
                LogEntries.RemoveAt(0);
        });
    }

    private static (string ProjectGid, string WorkstreamId)? ParseWorkstreamMapLine(string line)
    {
        var raw = line.Trim();
        if (raw.Length == 0 || raw.StartsWith('#')) return null;

        // 受け付け形式:
        // 1) gid=ws
        // 2) gid:ws
        // 3) gid -> ws
        // 4) gid,ws
        // 5) gid ws
        foreach (var sep in new[] { "->", "=", ":", "," })
        {
            var idx = raw.IndexOf(sep, StringComparison.Ordinal);
            if (idx <= 0 || idx + sep.Length >= raw.Length) continue;

            var gid = raw[..idx].Trim();
            var ws = raw[(idx + sep.Length)..].Trim();
            if (gid.Length > 0 && ws.Length > 0)
                return (gid, ws);
        }

        var parts = raw.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
            return (parts[0], parts[1]);

        return null;
    }

    private string ResolveAsanaConfigPath(ProjectInfo project)
        => _configService.ResolveAsanaConfigPath(project);
}
