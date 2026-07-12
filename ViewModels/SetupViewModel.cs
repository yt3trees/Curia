using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Curia.Helpers;
using Curia.Models;
using Curia.Services;

namespace Curia.ViewModels;

public partial class WorkstreamManageItem : ObservableObject
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";

    [ObservableProperty]
    private string label = "";

    [ObservableProperty]
    private bool isClosed;

    public string StatusText => IsClosed ? "Closed" : "Active";
    partial void OnIsClosedChanged(bool value) => OnPropertyChanged(nameof(StatusText));
}

public partial class SetupViewModel : ObservableObject
{
    private readonly ProjectDiscoveryService _discoveryService;
    private readonly ContextCompressionLayerService _contextCompressionLayerService;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _manageWorkstreamsLoadLock = new(1, 1);
    private readonly AsyncInitializationGate _initialization = new();

    // 内部用プロジェクト情報リスト (Tier/Category 自動判定)
    private List<ProjectInfo> _projectInfos = [];

    // --- 共有状態 ---

    [ObservableProperty]
    private bool isRunning;

    [ObservableProperty]
    private int selectedTabIndex = 0;

    [ObservableProperty]
    private string outputText = "";

    // --- New タブ ---

    [ObservableProperty]
    private bool isDetectingBoxProjects;

    public ObservableCollection<BoxOnlyProjectCandidate> BoxOnlyProjects { get; } = [];

    [ObservableProperty]
    private BoxOnlyProjectCandidate? selectedBoxOnlyProject;

    public bool HasBoxOnlyProjects => BoxOnlyProjects.Count > 0;

    public bool CanDetectBoxProjects => !IsRunning && !IsDetectingBoxProjects;

    [ObservableProperty]
    private string newProjectName = "";

    [ObservableProperty]
    private string newTier = "full";

    [ObservableProperty]
    private string newCategory = "project";

    [ObservableProperty]
    private string newExternalSharedPaths = "";

    [ObservableProperty]
    private bool newAlsoRunAiContext = true;

    [ObservableProperty]
    private bool newAiContextForce = false;

    [ObservableProperty]
    private bool newAiContextResetSkills = false;

    public bool NewAiContextForceEnabled => NewAlsoRunAiContext && !IsRunning;
    public bool NewAiContextResetSkillsEnabled => NewAlsoRunAiContext && !IsRunning;

    // --- Check / Archive / Convert 共通: プロジェクト名リスト ---

    public ObservableCollection<string> ProjectNames { get; } = [];

    // --- Check タブ ---

    [ObservableProperty]
    private string checkProjectName = "";

    // --- Archive タブ ---

    [ObservableProperty]
    private string archiveProjectName = "";

    [ObservableProperty]
    private bool archiveDryRun = true;

    // --- Convert タブ ---

    [ObservableProperty]
    private string convertProjectName = "";

    [ObservableProperty]
    private string convertTargetTier = "full";

    [ObservableProperty]
    private bool convertDryRun = true;

    // --- Workstreams tab ---

    [ObservableProperty]
    private string manageProjectName = "";

    [ObservableProperty]
    private string newWorkstreamId = "";

    [ObservableProperty]
    private string newWorkstreamLabel = "";

    [ObservableProperty]
    private WorkstreamManageItem? selectedManageWorkstream;

    public ObservableCollection<WorkstreamManageItem> ManageWorkstreams { get; } = [];
    public bool HasManageWorkstreams => ManageWorkstreams.Count > 0;
    public bool CanManageWorkstreams => !IsRunning && !string.IsNullOrWhiteSpace(ManageProjectName);
    public Action<ProjectInfo, string>? OnOpenWorkstreamFocusInEditor;

    private static readonly Regex NonKebabCharsRx =
        new(@"[^a-z0-9-]", RegexOptions.Compiled);

    public SetupViewModel(
        ProjectDiscoveryService discoveryService,
        ContextCompressionLayerService contextCompressionLayerService)
    {
        _discoveryService = discoveryService;
        _contextCompressionLayerService = contextCompressionLayerService;
    }

    /// <summary>プロジェクト名リストを非同期で読み込む。ページ表示時に呼ぶ。</summary>
    public async Task LoadProjectNamesAsync()
    {
        _projectInfos = await Task.Run(() => _discoveryService.GetProjectInfoList());
        ProjectNames.Clear();
        foreach (var p in _projectInfos)
            ProjectNames.Add(p.Name);

        if (string.IsNullOrWhiteSpace(ManageProjectName) && ProjectNames.Count > 0)
            ManageProjectName = ProjectNames[0];

        await LoadManageWorkstreamsAsync();
    }

    public Task EnsureInitializedAsync() => _initialization.EnsureAsync(LoadProjectNamesAsync);

    // --- コマンド ---

    [RelayCommand]
    private async Task RunNew()
    {
        if (string.IsNullOrWhiteSpace(NewProjectName))
        {
            AppendOutput("[ERROR] Please enter a project name.");
            return;
        }
        // LoadProjectNamesAsync が ProjectNames.Clear() を呼ぶと ComboBox 双方向バインディングで
        // NewProjectName がリセットされる。async 処理前にスナップショットを取る。
        var projectName = NewProjectName;
        var tier = NewTier;
        var category = NewCategory;
        var alsoRunAiContext = NewAlsoRunAiContext;
        var aiContextForce = NewAiContextForce;
        var aiContextResetSkills = NewAiContextResetSkills;

        await RunScript(async () =>
        {
            var externalPaths = string.IsNullOrWhiteSpace(NewExternalSharedPaths)
                ? Array.Empty<string>()
                : NewExternalSharedPaths.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var result = await _discoveryService.SetupProjectAsync(projectName, tier, category, externalPaths);

            foreach (var log in result.Logs)
            {
                AppendOutput(log);
            }

            if (result.Success)
            {
                _discoveryService.InvalidateCache();
                await LoadProjectNamesAsync();

                if (alsoRunAiContext)
                {
                    AppendOutput("");
                    AppendOutput("[AI Context Setup] Starting native C# context setup...");
                    var contextResult = await _contextCompressionLayerService.SetupForProjectAsync(
                        projectName,
                        tier,
                        category,
                        aiContextForce,
                        aiContextResetSkills,
                        _cts!.Token);

                    foreach (var log in contextResult.Logs)
                    {
                        AppendOutput(log);
                    }

                    if (!contextResult.Success)
                    {
                        AppendOutput($"[ERROR] {contextResult.Message}");
                    }
                }
            }
            else
            {
                AppendOutput($"[ERROR] {result.Message}");
            }
        });
    }

    [RelayCommand]
    private async Task RunCheck()
    {
        if (string.IsNullOrWhiteSpace(CheckProjectName))
        {
            AppendOutput("[ERROR] Please select or enter a project name.");
            return;
        }

        var project = _projectInfos.FirstOrDefault(p => p.Name == CheckProjectName);
        if (project == null)
        {
            AppendOutput($"[ERROR] Project '{CheckProjectName}' not found.");
            return;
        }

        await RunScript(async () =>
        {
            AppendOutput($"Checking project: {project.DisplayName}...");
            AppendOutput($"Path: {project.Path}");
            AppendOutput(new string('-', 60));

            var result = await _discoveryService.CheckProjectAsync(project);
            
            foreach (var item in result.Items)
            {
                var statusIcon = item.Status switch
                {
                    "OK" => "[ OK ]",
                    "Warning" => "[WARN]",
                    "Error" => "[ERR ]",
                    _ => "[INFO]"
                };
                AppendOutput($"{statusIcon} {item.Name,-30} : {item.Message}");
            }

            AppendOutput(new string('-', 60));
            if (result.HasError)
                AppendOutput("Result: FAILED (Critical issues found)");
            else if (result.HasWarning)
                AppendOutput("Result: PASSED with Warnings");
            else
                AppendOutput("Result: PASSED");
        });
    }

    [RelayCommand]
    private async Task RunArchive()
    {
        if (string.IsNullOrWhiteSpace(ArchiveProjectName))
        {
            AppendOutput("[ERROR] Please select or enter a project name.");
            return;
        }

        var project = _projectInfos.FirstOrDefault(p => p.Name == ArchiveProjectName);
        if (project == null)
        {
            AppendOutput($"[ERROR] Project '{ArchiveProjectName}' not found.");
            return;
        }

        await RunScript(async () =>
        {
            var result = await _discoveryService.ArchiveProjectAsync(project, ArchiveDryRun);
            
            foreach (var log in result.Logs)
            {
                AppendOutput(log);
            }

            if (!result.Success)
            {
                AppendOutput($"[ERROR] {result.Message}");
            }
            else if (!ArchiveDryRun)
            {
                _discoveryService.InvalidateCache();
                await LoadProjectNamesAsync();
            }
        });
    }

    [RelayCommand]
    private async Task RunConvert()
    {
        if (string.IsNullOrWhiteSpace(ConvertProjectName))
        {
            AppendOutput("[ERROR] Please select or enter a project name.");
            return;
        }

        var project = _projectInfos.FirstOrDefault(p => p.Name == ConvertProjectName);
        if (project == null)
        {
            AppendOutput($"[ERROR] Project '{ConvertProjectName}' not found.");
            return;
        }

        await RunScript(async () =>
        {
            var result = await _discoveryService.ConvertProjectTierAsync(project, ConvertTargetTier, ConvertDryRun);
            
            foreach (var log in result.Logs)
            {
                AppendOutput(log);
            }

            if (!result.Success)
            {
                AppendOutput($"[ERROR] {result.Message}");
            }
            else if (!ConvertDryRun)
            {
                _discoveryService.InvalidateCache();
                await LoadProjectNamesAsync();
            }
        });
    }

    [RelayCommand]
    private async Task ReloadWorkstreams()
    {
        await RunScript(LoadManageWorkstreamsAsync);
    }

    [RelayCommand]
    private async Task AddWorkstream()
    {
        var project = GetManageProject();
        if (project == null)
        {
            AppendOutput("[ERROR] Please select a project in the Workstreams tab.");
            return;
        }

        var rawId = NewWorkstreamId.Trim();
        if (string.IsNullOrWhiteSpace(rawId))
        {
            AppendOutput("[ERROR] Please enter a Workstream ID.");
            return;
        }

        var normalizedId = NormalizeWorkstreamId(rawId);
        if (string.IsNullOrWhiteSpace(normalizedId))
        {
            AppendOutput("[ERROR] Workstream ID must contain letters or numbers.");
            return;
        }

        await RunScript(async () =>
        {
            var wsRoot = Path.Combine(project.AiContextContentPath, "workstreams");
            var wsDir = Path.Combine(wsRoot, normalizedId);
            if (Directory.Exists(wsDir))
            {
                AppendOutput($"[WARN] Workstream '{normalizedId}' already exists.");
                return;
            }

            Directory.CreateDirectory(wsDir);
            Directory.CreateDirectory(Path.Combine(wsDir, "decision_log"));
            Directory.CreateDirectory(Path.Combine(wsDir, "focus_history"));
            var focusFile = Path.Combine(wsDir, "current_focus.md");
            if (!File.Exists(focusFile))
            {
                await File.WriteAllTextAsync(focusFile, ContextCompressionLayerService.CurrentFocusTemplate, Encoding.UTF8, _cts!.Token);
            }

            var sharedWorkDir = Path.Combine(project.Path, "shared", "_work", normalizedId);
            Directory.CreateDirectory(sharedWorkDir);

            var obsidianNotes = Path.Combine(project.Path, "_ai-context", "obsidian_notes");
            string? obsidianWsDir = null;
            if (Directory.Exists(obsidianNotes))
            {
                obsidianWsDir = Path.Combine(obsidianNotes, "workstreams", normalizedId);
                Directory.CreateDirectory(obsidianWsDir);
                foreach (var folder in new[] { "troubleshooting", "daily", "meetings", "notes" })
                    Directory.CreateDirectory(Path.Combine(obsidianWsDir, folder));
            }

            var label = NewWorkstreamLabel.Trim();
            if (!string.IsNullOrWhiteSpace(label))
                await UpsertWorkstreamLabelAsync(wsRoot, normalizedId, label, _cts!.Token);

            AppendOutput($"[OK] Created workstream '{normalizedId}'.");
            AppendOutput($"  - Context: {wsDir}");
            AppendOutput($"  - Shared:  {sharedWorkDir}");
            if (obsidianWsDir != null)
                AppendOutput($"  - Obsidian: {obsidianWsDir}");

            NewWorkstreamId = "";
            NewWorkstreamLabel = "";

            _discoveryService.InvalidateCache();
            await LoadManageWorkstreamsAsync();

            if (File.Exists(focusFile))
            {
                AppendOutput("[OK] Opening current_focus.md in Editor...");
                OnOpenWorkstreamFocusInEditor?.Invoke(project, focusFile);
            }
        });
    }

    [RelayCommand]
    private async Task ToggleWorkstreamClosed(WorkstreamManageItem? item)
    {
        if (item == null) return;

        var action = item.IsClosed ? "reopen" : "close";
        var result = MessageBox.Show(
            $"Are you sure you want to {action} '{item.Id}'?",
            "Confirm Workstream Status",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        await RunScript(async () =>
        {
            var marker = Path.Combine(item.Path, "_closed");
            if (item.IsClosed)
            {
                if (File.Exists(marker))
                    File.Delete(marker);
                item.IsClosed = false;
                AppendOutput($"[OK] Reopened workstream '{item.Id}'.");
            }
            else
            {
                await File.WriteAllTextAsync(marker, "", Encoding.UTF8, _cts!.Token);
                item.IsClosed = true;
                AppendOutput($"[OK] Closed workstream '{item.Id}'.");
            }

            _discoveryService.InvalidateCache();
            await LoadManageWorkstreamsAsync();
        });
    }

    [RelayCommand]
    private async Task SaveWorkstreamLabels()
    {
        var project = GetManageProject();
        if (project == null)
        {
            AppendOutput("[ERROR] Please select a project in the Workstreams tab.");
            return;
        }

        await RunScript(async () =>
        {
            var wsRoot = Path.Combine(project.AiContextContentPath, "workstreams");
            Directory.CreateDirectory(wsRoot);

            var map = await LoadWorkstreamLabelMapAsync(wsRoot, _cts!.Token);

            // 同一IDが重複していても、明示ラベルを優先して1件に正規化する。
            var normalizedById = ManageWorkstreams
                .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g =>
                    {
                        var preferred = g
                            .Select(x => x.Label.Trim())
                            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && !string.Equals(x, g.Key, StringComparison.OrdinalIgnoreCase));
                        return preferred ?? g.First().Label.Trim();
                    },
                    StringComparer.OrdinalIgnoreCase);

            foreach (var (id, rawLabel) in normalizedById)
            {
                var label = rawLabel.Trim();
                // IDと同一のラベルは「カスタム未設定」とみなす。
                if (string.IsNullOrWhiteSpace(label) || string.Equals(label, id, StringComparison.OrdinalIgnoreCase))
                {
                    map.Remove(id);
                    continue;
                }

                map[id] = new WorkstreamLabelSetting { Label = label };
            }

            await SaveWorkstreamLabelMapAsync(wsRoot, map, _cts!.Token);
            AppendOutput("[OK] Saved workstream labels.");

            _discoveryService.InvalidateCache();
            await LoadManageWorkstreamsAsync();
        });
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void ClearOutput() => OutputText = "";

    // --- ヘルパー ---

    /// <summary>プロジェクト情報リストから Tier/Category を参照して -Mini / -Category 引数を組み立てる。</summary>
    private string BuildProjectArgs(string projectName)
    {
        var info = _projectInfos.FirstOrDefault(p => p.Name == projectName);
        var args = $"-ProjectName \"{projectName}\"";
        if (info?.Tier == "mini") args += " -Mini";
        if (info?.Category == "domain") args += " -Category domain";
        return args;
    }

    partial void OnNewProjectNameChanged(string value)
    {
        var existing = _projectInfos.FirstOrDefault(p => p.Name == value);
        if (existing != null)
        {
            NewTier = existing.Tier;
            NewCategory = existing.Category;
            NewExternalSharedPaths = string.Join("\n", existing.ExternalSharedPaths);
        }
    }

    partial void OnNewAlsoRunAiContextChanged(bool value)
    {
        OnPropertyChanged(nameof(NewAiContextForceEnabled));
        OnPropertyChanged(nameof(NewAiContextResetSkillsEnabled));
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(NewAiContextForceEnabled));
        OnPropertyChanged(nameof(NewAiContextResetSkillsEnabled));
        OnPropertyChanged(nameof(CanManageWorkstreams));
        OnPropertyChanged(nameof(CanDetectBoxProjects));
    }

    partial void OnIsDetectingBoxProjectsChanged(bool value)
        => OnPropertyChanged(nameof(CanDetectBoxProjects));

    partial void OnSelectedBoxOnlyProjectChanged(BoxOnlyProjectCandidate? value)
    {
        if (value == null) return;
        NewProjectName         = value.Name;
        NewTier                = value.Tier;
        NewCategory            = value.Category;
        NewExternalSharedPaths = string.Join("\n", value.ExternalSharedPaths);
    }

    [RelayCommand]
    private async Task DetectBoxProjects()
    {
        IsDetectingBoxProjects = true;
        try
        {
            var candidates = await _discoveryService.ScanBoxOnlyProjectsAsync();
            BoxOnlyProjects.Clear();
            foreach (var c in candidates)
                BoxOnlyProjects.Add(c);
            OnPropertyChanged(nameof(HasBoxOnlyProjects));

            AppendOutput(candidates.Count == 0
                ? "[Detect from BOX] No BOX-only projects found."
                : $"[Detect from BOX] Found {candidates.Count} project(s). Select one to auto-fill.");
        }
        catch (Exception ex)
        {
            AppendOutput($"[ERROR] Detect from BOX: {ex.Message}");
        }
        finally
        {
            IsDetectingBoxProjects = false;
        }
    }

    partial void OnManageProjectNameChanged(string value)
    {
        OnPropertyChanged(nameof(CanManageWorkstreams));
        _ = LoadManageWorkstreamsSafeAsync();
    }

    private async Task LoadManageWorkstreamsSafeAsync()
    {
        try
        {
            await LoadManageWorkstreamsAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Workstreams] {ex.Message}");
        }
    }

    private async Task LoadManageWorkstreamsAsync()
    {
        await _manageWorkstreamsLoadLock.WaitAsync();
        try
        {
            var project = GetManageProject();

            ManageWorkstreams.Clear();
            if (project == null)
            {
                OnPropertyChanged(nameof(HasManageWorkstreams));
                return;
            }

            var refreshed = await Task.Run(() =>
            {
                _discoveryService.InvalidateCache();
                return _discoveryService.GetProjectInfoList();
            });

            _projectInfos = refreshed;
            var current = _projectInfos.FirstOrDefault(p => p.Name == ManageProjectName);
            if (current == null)
            {
                OnPropertyChanged(nameof(HasManageWorkstreams));
                return;
            }

            foreach (var ws in current.Workstreams
                .GroupBy(w => w.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(w => w.IsClosed)
                .ThenBy(w => w.Id))
            {
                ManageWorkstreams.Add(new WorkstreamManageItem
                {
                    Id = ws.Id,
                    Label = string.IsNullOrWhiteSpace(ws.Label) ? ws.Id : ws.Label,
                    Path = ws.Path,
                    IsClosed = ws.IsClosed
                });
            }

            OnPropertyChanged(nameof(HasManageWorkstreams));
        }
        finally
        {
            _manageWorkstreamsLoadLock.Release();
        }
    }

    private ProjectInfo? GetManageProject()
        => _projectInfos.FirstOrDefault(p => p.Name == ManageProjectName);

    private static string NormalizeWorkstreamId(string raw)
    {
        var normalized = raw.Trim().ToLowerInvariant()
            .Replace('_', '-')
            .Replace(' ', '-');
        normalized = NonKebabCharsRx.Replace(normalized, "-");
        normalized = Regex.Replace(normalized, "-{2,}", "-").Trim('-');
        return normalized;
    }

    private sealed class WorkstreamLabelSetting
    {
        public string Label { get; set; } = "";
    }

    private static async Task<Dictionary<string, WorkstreamLabelSetting>> LoadWorkstreamLabelMapAsync(
        string workstreamsRoot,
        CancellationToken ct)
    {
        var path = Path.Combine(workstreamsRoot, "workstream.json");
        if (!File.Exists(path)) return [];

        try
        {
            var raw = await File.ReadAllTextAsync(path, ct);
            using var doc = JsonDocument.Parse(raw);
            var result = new Dictionary<string, WorkstreamLabelSetting>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;

                string? label = null;
                if (prop.Value.TryGetProperty("label", out var lower))
                    label = lower.GetString();
                else if (prop.Value.TryGetProperty("Label", out var upper))
                    label = upper.GetString();

                if (!string.IsNullOrWhiteSpace(label))
                    result[prop.Name] = new WorkstreamLabelSetting { Label = label };
            }

            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WorkstreamLabels] Load error: {ex.Message}");
            return [];
        }
    }

    private static async Task SaveWorkstreamLabelMapAsync(
        string workstreamsRoot,
        Dictionary<string, WorkstreamLabelSetting> map,
        CancellationToken ct)
    {
        var path = Path.Combine(workstreamsRoot, "workstream.json");
        var ordered = map.OrderBy(kv => kv.Key)
            .ToDictionary(
                kv => kv.Key,
                kv => new Dictionary<string, string> { ["label"] = kv.Value.Label });
        var json = JsonSerializer.Serialize(ordered, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, Encoding.UTF8, ct);
    }

    private static async Task UpsertWorkstreamLabelAsync(
        string workstreamsRoot,
        string workstreamId,
        string label,
        CancellationToken ct)
    {
        var map = await LoadWorkstreamLabelMapAsync(workstreamsRoot, ct);
        map[workstreamId] = new WorkstreamLabelSetting { Label = label };
        await SaveWorkstreamLabelMapAsync(workstreamsRoot, map, ct);
    }

    private async Task RunScript(Func<Task> action)
    {
        IsRunning = true;
        OutputText = "";
        _cts = new CancellationTokenSource();
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            AppendOutput("[Cancelled]");
        }
        catch (Exception ex)
        {
            AppendOutput($"[ERROR] {ex.Message}");
        }
        finally
        {
            IsRunning = false;
            OnPropertyChanged(nameof(CanManageWorkstreams));
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void AppendOutput(string line)
    {
        OutputText += line + "\n";
    }
}
