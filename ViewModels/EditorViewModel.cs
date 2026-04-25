using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wpf.Ui;
using Wpf.Ui.Controls;
using System.Threading;
using Curia.Models;
using Curia.Services;
using Curia.Views.Pages;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Curia.ViewModels;

// ファイルツリーのノードモデル
public partial class FileTreeNode : ObservableObject
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public bool IsClosedWorkstream { get; set; }
    public ObservableCollection<FileTreeNode> Children { get; } = [];

    [ObservableProperty]
    private bool isExpanded;

    [ObservableProperty]
    private bool isSelected;
}

public partial class EditorViewModel : ObservableObject
{
    private readonly FileEncodingService _encodingService;
    private readonly ProjectDiscoveryService _discoveryService;
    private readonly FocusUpdateService _focusUpdateService;
    private readonly LlmClientService _llmClient;
    private readonly ConfigService _configService;
    private readonly DecisionLogGeneratorService _decisionLogService;
    private readonly MeetingNotesService _meetingNotesService;
    private readonly TrayService _trayService;
    private string? _pendingFileToOpen;
    // NavigateToProjectAndOpenFile で指定されたファイル。LoadProjectsAsync が消費するまで保持する。
    // _pendingFileToOpen は OnSelectedProjectChanged でクリアされるが、その後 LoadProjectsAsync が
    // 再度 OnSelectedProjectChanged を発火させる際には既に null になっているため、このフィールドで補完する。
    private string? _externalNavigationFile;
    private bool _suppressAutoFileOpenOnProjectChange;
    private CancellationTokenSource? _focusUpdateCts;
    private CancellationTokenSource? _decisionLogCts;
    private string? _capturedContextForFocusUpdate;
    private string? _capturedContextForDecisionLog;

    // ---- 選択プロジェクト ----
    [ObservableProperty]
    private ObservableCollection<ProjectInfo> projects = [];

    [ObservableProperty]
    private ProjectInfo? selectedProject;

    // ---- ファイルツリー ----
    [ObservableProperty]
    private ObservableCollection<FileTreeNode> treeNodes = [];

    [ObservableProperty]
    private FileTreeNode? selectedNode;

    // ---- エディタ状態 ----
    [ObservableProperty]
    private string currentFile = "";

    [ObservableProperty]
    private string editorText = "";

    private string _originalContent = "";

    [ObservableProperty]
    private bool isDirty;

    [ObservableProperty]
    private bool isDiffViewActive;

    [ObservableProperty]
    private bool canUseDiff;

    [ObservableProperty]
    private string encoding = "UTF8";

    [ObservableProperty]
    private bool suppressChangeEvent;

    // ---- 検索 ----
    [ObservableProperty]
    private bool isSearchBarVisible;

    [ObservableProperty]
    private string searchText = "";

    // ---- ローディング ----
    [ObservableProperty]
    private bool isLoading;

    // ---- AI 機能 ----
    [ObservableProperty]
    private bool isAiEnabled;

    // ---- フォントサイズ / 文字色 ----
    [ObservableProperty]
    private int editorFontSize = 14;

    [ObservableProperty]
    private string editorTextColor = "";

    // ---- new decision_log 要求コールバック ----
    public Func<Task<string?>>? RequestNewDecisionLogName;

    // ---- Update Focus ダイアログコールバック ----
    // workstream 選択: workstream リストを渡す → (ok=false でキャンセル, ok=true で wsId=null:general / wsId=id:workstream)
    public Func<List<WorkstreamInfo>, Task<(bool ok, string? wsId)>>? RequestWorkstreamSelection;
    // 更新提案を表示 → (適用するか, 適用コンテンツ) を返す
    // 第2引数: refineFunc(currentProposed, instructions) → 改訂後全文
    public Func<FocusUpdateResult, Func<string, string, Task<string>>, Task<(bool apply, string? content)>>? RequestFocusUpdateApproval;
    // スクロール可能なエラーダイアログ
    public Action<string, string>? ShowScrollableError;

    // ---- AI Decision Log コールバック ----
    // 入力ダイアログ: 検出候補リストと prefill テキストを渡す → 入力結果 (null=キャンセル)
    public Func<List<DetectedDecision>, string?, Task<AiDecisionLogInputResult?>>? RequestAiDecisionLogInput;
    // プレビューダイアログ: ドラフト + Refine 関数を渡す → (保存するか, コンテンツ, ファイル名, tension削除フラグ)
    public Func<DecisionLogDraftResult, Func<string, string, Task<string>>,
        Task<(bool save, string? content, string? fileName, bool removeTension)>>? RequestDecisionLogPreview;

    // ---- Meeting Notes Import コールバック ----
    public Func<ProjectInfo, List<WorkstreamInfo>, Task<MeetingNotesInputResult?>>? RequestMeetingNotesInput;
    public Func<MeetingAnalysisResult, ProjectInfo, string?, Task<bool>>? RequestMeetingNotesPreview;

    public string OriginalContent => _originalContent;

    public EditorViewModel(
        FileEncodingService encodingService,
        ProjectDiscoveryService discoveryService,
        FocusUpdateService focusUpdateService,
        LlmClientService llmClient,
        ConfigService configService,
        DecisionLogGeneratorService decisionLogService,
        MeetingNotesService meetingNotesService,
        TrayService trayService)
    {
        _encodingService = encodingService;
        _discoveryService = discoveryService;
        _focusUpdateService = focusUpdateService;
        _llmClient = llmClient;
        _configService = configService;
        _decisionLogService = decisionLogService;
        _meetingNotesService = meetingNotesService;
        _trayService = trayService;

        var settings = _configService.LoadSettings();
        IsAiEnabled     = settings.AiEnabled;
        EditorFontSize  = settings.EditorFontSize;
        EditorTextColor = settings.EditorTextColor;
        WeakReferenceMessenger.Default.Register<AiEnabledChangedMessage>(this,
            (_, msg) => IsAiEnabled = msg.Enabled);
        WeakReferenceMessenger.Default.Register<FontSizeChangedMessage>(this,
            (_, msg) => EditorFontSize = msg.EditorFontSize);
        WeakReferenceMessenger.Default.Register<TextColorChangedMessage>(this,
            (_, msg) => EditorTextColor = msg.EditorTextColor);
    }

    // =====================================================================
    // プロジェクト一覧ロード
    // =====================================================================
    public async Task LoadProjectsAsync()
    {
        // NavigateToProjectAndOpenFile で指定されたファイルを非同期 yield 前にキャプチャして消費する。
        // _pendingFileToOpen は OnSelectedProjectChanged で既にクリアされているが、
        // そこで起動した OpenFileAndSelectNodeAsync がまだ完了していない場合に CurrentFile が
        // 更新されておらず、SelectedProject の再セット時にデフォルト (FocusFile) が開かれてしまうのを防ぐ。
        var externalFile = _externalNavigationFile;
        _externalNavigationFile = null;

        IsLoading = true;
        try
        {
            var list = await _discoveryService.GetProjectInfoListAsync();
            var hiddenKeys = _configService.LoadHiddenProjects();
            var currentSelectionKey = SelectedProject?.HiddenKey;

            Projects.Clear();
            foreach (var p in list.Where(p => !hiddenKeys.Contains(p.HiddenKey)))
                Projects.Add(p);

            // 選択状態を復元
            if (currentSelectionKey != null)
            {
                var match = Projects.FirstOrDefault(p => p.HiddenKey == currentSelectionKey);
                if (match != null)
                {
                    _suppressAutoFileOpenOnProjectChange = true;
                    SelectedProject = match;
                    _suppressAutoFileOpenOnProjectChange = false;

                    // 外部指定ファイルまたは現在開いているファイルをプロジェクト配下なら復元する
                    var fileToRestore = externalFile ?? CurrentFile;
                    if (!string.IsNullOrEmpty(fileToRestore)
                        && File.Exists(fileToRestore)
                        && IsPathUnderDirectory(fileToRestore, match.Path))
                    {
                        _ = OpenFileAndSelectNodeAsync(fileToRestore);
                    }
                    else if (!string.IsNullOrEmpty(match.FocusFile) && File.Exists(match.FocusFile))
                    {
                        _ = OpenFileAndSelectNodeAsync(match.FocusFile);
                    }
                }
            }
        }
        finally
        {
            // focus update 実行中はローディングを維持する
            if (_focusUpdateCts == null)
                IsLoading = false;
        }
    }

    // =====================================================================
    // プロジェクト選択変更 -> ツリー再構築
    // =====================================================================
    partial void OnSelectedProjectChanged(ProjectInfo? value)
    {
        if (value == null) return;
        IsDiffViewActive = false;
        BuildFileTree(value);
        UpdateStatus();
        UpdateFocusCommand.NotifyCanExecuteChanged();
        ImportMeetingNotesCommand.NotifyCanExecuteChanged();

        if (_suppressAutoFileOpenOnProjectChange)
            return;

        // 外部遷移（Timeline など）で開くべきファイルが指定されている場合はそちらを優先
        if (!string.IsNullOrEmpty(_pendingFileToOpen))
        {
            var target = _pendingFileToOpen;
            _pendingFileToOpen = null;

            if (!string.IsNullOrEmpty(target) && File.Exists(target))
            {
                _ = OpenFileAndSelectNodeAsync(target);
                return;
            }
        }

        // 既に同一プロジェクト配下のファイルを開いている場合は、そのファイルを維持する。
        // (例: 外部遷移でファイルを開いた直後に、LoadProjectsAsync の再選択が走るケース)
        if (!string.IsNullOrEmpty(CurrentFile)
            && File.Exists(CurrentFile)
            && IsPathUnderDirectory(CurrentFile, value.Path))
        {
            _ = OpenFileAndSelectNodeAsync(CurrentFile);
            return;
        }

        // プロジェクト選択時、デフォルトで current_focus.md を開く
        if (!string.IsNullOrEmpty(value.FocusFile) && File.Exists(value.FocusFile))
        {
            _ = OpenFileAndSelectNodeAsync(value.FocusFile);
        }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var selectedKey = SelectedProject?.HiddenKey;
            var currentFile = CurrentFile;

            var list = await _discoveryService.GetProjectInfoListAsync(force: true);
            var hiddenKeys = _configService.LoadHiddenProjects();

            Projects.Clear();
            foreach (var p in list.Where(p => !hiddenKeys.Contains(p.HiddenKey)))
                Projects.Add(p);

            var nextSelected = selectedKey != null
                ? Projects.FirstOrDefault(p => p.HiddenKey == selectedKey)
                : Projects.FirstOrDefault();

            if (nextSelected != null)
            {
                _suppressAutoFileOpenOnProjectChange = true;
                SelectedProject = nextSelected;
                _suppressAutoFileOpenOnProjectChange = false;

                if (!string.IsNullOrEmpty(currentFile)
                    && File.Exists(currentFile)
                    && IsPathUnderDirectory(currentFile, nextSelected.Path))
                {
                    if (IsDirty)
                    {
                        foreach (var node in TreeNodes)
                        {
                            if (TrySelectNodeRecursive(node, currentFile))
                                break;
                        }
                    }
                    else
                    {
                        await OpenFileAndSelectNodeAsync(currentFile);
                    }
                }
            }
        }
        finally
        {
            _suppressAutoFileOpenOnProjectChange = false;
            IsLoading = false;
        }
    }

    /// <summary>
    /// 外部ページから Editor へ遷移する際に、プロジェクトを切り替えて指定ファイルを開く。
    /// </summary>
    public void NavigateToProjectAndOpenFile(ProjectInfo project, string filePath)
    {
        _pendingFileToOpen = filePath;
        _externalNavigationFile = filePath;

        bool projectWillChange = !string.Equals(
            SelectedProject?.HiddenKey,
            project.HiddenKey,
            StringComparison.Ordinal);

        SelectedProject = project;

        // 同一プロジェクト再選択では OnSelectedProjectChanged が発火しないため、ここで直接開く
        if (!projectWillChange)
        {
            _pendingFileToOpen = null;
            _externalNavigationFile = null;
            _ = OpenFileAndSelectNodeAsync(filePath);
        }
    }

    private static bool IsPathUnderDirectory(string filePath, string directoryPath)
    {
        try
        {
            var fileFull = Path.GetFullPath(filePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var dirFull = Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return fileFull.StartsWith(dirFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileFull, dirFull, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void BuildFileTree(ProjectInfo project)
    {
        var expandedState = CaptureExpandedState(TreeNodes);

        TreeNodes.Clear();

        var aiCtxContent = project.AiContextContentPath;  // _ai-context/context
        var aiCtxObsidianNotes = Path.Combine(project.AiContextPath, "obsidian_notes");

        // 表示するフォルダ定義
        var sections = new[]
        {
            ("_ai-context",           aiCtxContent),
            ("decision_log",          Path.Combine(aiCtxContent, "decision_log")),
            ("focus_history",         Path.Combine(aiCtxContent, "focus_history")),
            ("obsidian_notes",        aiCtxObsidianNotes),
            ("asana (legacy)",        Path.Combine(aiCtxContent, "asana")),
            (".context",              Path.Combine(project.Path, ".context")),
            ("briefings",             Path.Combine(aiCtxContent, "briefings")),
        };

        foreach (var (label, dir) in sections)
        {
            if (!Directory.Exists(dir)) continue;
            
            // decision_log / focus_history / obsidian_notes はデフォルトで閉じる
            bool shouldExpand = label != "decision_log"
                && label != "focus_history"
                && label != "obsidian_notes";
            
            var node = new FileTreeNode { Name = label, FullPath = dir, IsDirectory = true, IsExpanded = shouldExpand };
            AddDirectoryChildren(node, dir, includeSubdirectories: label == "obsidian_notes" || label == "focus_history");
            TreeNodes.Add(node);
        }

        // workstreams セクション
        if (project.HasWorkstreams)
        {
            var workstreamsDir = Path.Combine(aiCtxContent, "workstreams");
            var workstreamsNode = new FileTreeNode
            {
                Name = "workstreams",
                FullPath = workstreamsDir,
                IsDirectory = true,
                IsExpanded = true
            };

            foreach (var ws in project.Workstreams.OrderBy(w => w.IsClosed).ThenBy(w => w.Id))
            {
                var wsLabel = ws.IsClosed ? $"{ws.Label} [closed]" : ws.Label;
                var wsNode = new FileTreeNode
                {
                    Name = wsLabel,
                    FullPath = ws.Path,
                    IsDirectory = true,
                    IsExpanded = !ws.IsClosed,
                    IsClosedWorkstream = ws.IsClosed
                };

                if (ws.FocusFile != null)
                    wsNode.Children.Add(new FileTreeNode { Name = "current_focus.md", FullPath = ws.FocusFile, IsDirectory = false, IsClosedWorkstream = ws.IsClosed });

                var dlDir = Path.Combine(ws.Path, "decision_log");
                if (Directory.Exists(dlDir))
                {
                    var dlNode = new FileTreeNode { Name = "decision_log", FullPath = dlDir, IsDirectory = true, IsExpanded = false, IsClosedWorkstream = ws.IsClosed };
                    AddDirectoryChildren(dlNode, dlDir);
                    wsNode.Children.Add(dlNode);
                }

                var fhDir = Path.Combine(ws.Path, "focus_history");
                if (Directory.Exists(fhDir))
                {
                    var fhNode = new FileTreeNode { Name = "focus_history", FullPath = fhDir, IsDirectory = true, IsExpanded = false, IsClosedWorkstream = ws.IsClosed };
                    AddDirectoryChildren(fhNode, fhDir);
                    wsNode.Children.Add(fhNode);
                }

                workstreamsNode.Children.Add(wsNode);
            }

            TreeNodes.Add(workstreamsNode);
        }

        // ルートの特定ファイル (CLAUDE.md, AGENTS.md)
        var rootFiles = new[] { "CLAUDE.md", "AGENTS.md", "README.md" };
        foreach (var fn in rootFiles)
        {
            var fp = Path.Combine(project.Path, fn);
            if (File.Exists(fp))
                TreeNodes.Add(new FileTreeNode { Name = fn, FullPath = fp, IsDirectory = false });
        }

        ApplyExpandedState(TreeNodes, expandedState);
    }

    private static Dictionary<string, bool> CaptureExpandedState(IEnumerable<FileTreeNode> nodes)
    {
        var state = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
            CaptureExpandedStateRecursive(node, state);
        return state;
    }

    private static void CaptureExpandedStateRecursive(FileTreeNode node, IDictionary<string, bool> state)
    {
        if (node.IsDirectory)
            state[node.FullPath] = node.IsExpanded;

        foreach (var child in node.Children)
            CaptureExpandedStateRecursive(child, state);
    }

    private static void ApplyExpandedState(IEnumerable<FileTreeNode> nodes, IReadOnlyDictionary<string, bool> state)
    {
        foreach (var node in nodes)
            ApplyExpandedStateRecursive(node, state);
    }

    private static void ApplyExpandedStateRecursive(FileTreeNode node, IReadOnlyDictionary<string, bool> state)
    {
        if (node.IsDirectory && state.TryGetValue(node.FullPath, out var isExpanded))
            node.IsExpanded = isExpanded;

        foreach (var child in node.Children)
            ApplyExpandedStateRecursive(child, state);
    }

    private static void AddDirectoryChildren(FileTreeNode parentNode, string dir, bool includeSubdirectories = false)
    {
        try
        {
            if (includeSubdirectories)
            {
                foreach (var subDir in Directory
                    .GetDirectories(dir)
                    .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                {
                    var directoryNode = new FileTreeNode
                    {
                        Name = Path.GetFileName(subDir),
                        FullPath = subDir,
                        IsDirectory = true,
                        IsExpanded = false
                    };

                    AddDirectoryChildren(directoryNode, subDir, includeSubdirectories: true);
                    parentNode.Children.Add(directoryNode);
                }
            }

            foreach (var f in Directory
                .GetFiles(dir, "*.md")
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(f);
                if (string.Equals(name, "TEMPLATE.md", StringComparison.OrdinalIgnoreCase)) continue;
                parentNode.Children.Add(new FileTreeNode { Name = name, FullPath = f, IsDirectory = false });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Editor] Failed to build tree node for '{dir}': {ex.Message}");
        }
    }

    // =====================================================================
    // ファイルを開き、ツリーのノードを選択状態にする
    // =====================================================================
    public async Task OpenFileAndSelectNodeAsync(string path)
    {
        await OpenFileAsync(path);

        // ツリー内の該当ノードを探して選択・展開
        foreach (var node in TreeNodes)
        {
            if (TrySelectNodeRecursive(node, path))
                break;
        }

        // focus_update ルーティング: OpenFileAsync 完了後 (IsLoading リセット済み) に発火
        // UpdateStatus() からは呼ばない (OpenFileAsync と IsLoading が競合するため)
        if (_capturedContextForFocusUpdate != null && IsCurrentFocusFile(path) && CanUpdateFocus())
            _ = UpdateFocusAsync();
    }

    private bool TrySelectNodeRecursive(FileTreeNode node, string targetPath)
    {
        if (string.Equals(node.FullPath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            node.IsSelected = true;
            return true;
        }

        if (node.IsDirectory)
        {
            foreach (var child in node.Children)
            {
                if (TrySelectNodeRecursive(child, targetPath))
                {
                    node.IsExpanded = true;
                    return true;
                }
            }
        }
        return false;
    }

    // =====================================================================
    // ファイルを開く (内部処理)
    // =====================================================================
    public async Task OpenFileAsync(string path)
    {
        IsDiffViewActive = false;

        if (IsDirty)
        {
            var result = MessageBox.Show(
                $"'{Path.GetFileName(CurrentFile)}' has unsaved changes.\nDiscard and continue?",
                "Unsaved Changes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
        }

        IsLoading = true;
        try
        {
            var (content, enc) = await _encodingService.ReadFileAsync(path);
            SuppressChangeEvent = true;
            EditorText = content;
            SuppressChangeEvent = false;
            _originalContent = content;
            CurrentFile = path;
            // 同じパスを再オープンした場合 CurrentFile の PropertyChanged が発火しないため強制通知
            OnPropertyChanged(nameof(CurrentFile));
            Encoding = enc;
            IsDirty = false;
            UpdateStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open file:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    // =====================================================================
    // エディタテキスト変更
    // =====================================================================
    public void NotifyTextChanged(string newText)
    {
        if (SuppressChangeEvent) return;
        EditorText = newText;
        IsDirty = newText != _originalContent;
        UpdateStatus();
    }

    // =====================================================================
    // 保存 (Ctrl+S)
    // =====================================================================
    [RelayCommand]
    public async Task SaveAsync()
    {
        if (string.IsNullOrEmpty(CurrentFile)) return;
        try
        {
            await _encodingService.WriteFileAsync(CurrentFile, EditorText, Encoding);

            // current_focus.md 保存時は focus_history スナップショットを作成
            if (Path.GetFileName(CurrentFile).Equals("current_focus.md", StringComparison.OrdinalIgnoreCase))
                await TakeFocusSnapshotAsync();

            _originalContent = EditorText;
            IsDirty = false;
            IsDiffViewActive = false;
            UpdateStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // =====================================================================
    // focus_history スナップショット
    // =====================================================================
    private async Task TakeFocusSnapshotAsync()
    {
        try
        {
            var dir = Path.GetDirectoryName(CurrentFile)!;
            var histDir = Path.Combine(dir, "focus_history");
            Directory.CreateDirectory(histDir);
            var snapPath = Path.Combine(histDir, $"{DateTime.Now:yyyy-MM-dd}.md");
            await _encodingService.WriteFileAsync(snapPath, EditorText, Encoding);
        }
        catch { }
    }

    // =====================================================================
    // Import Meeting Notes
    // =====================================================================
    [RelayCommand(CanExecute = nameof(CanImportMeetingNotes))]
    public async Task ImportMeetingNotesAsync()
    {
        if (SelectedProject == null) return;

        var settings = _configService.LoadSettings();
        if (!LlmClientService.IsCliProvider(settings.LlmProvider) && string.IsNullOrWhiteSpace(settings.LlmApiKey))
        {
            MessageBox.Show(
                "LLM API key is not configured.\nPlease open Settings and set the API key under \"LLM API\".",
                "Import Meeting Notes",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (RequestMeetingNotesInput == null) return;

        var activeWorkstreams = SelectedProject.Workstreams.Where(w => !w.IsClosed).ToList();
        var input = await RequestMeetingNotesInput(SelectedProject, activeWorkstreams);
        if (input == null) return;

        IsLoading = true;
        MeetingAnalysisResult result;
        try
        {
            result = await _meetingNotesService.AnalyzeAsync(
                input.MeetingNotes, SelectedProject, input.WorkstreamId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            IsLoading = false;
            if (ShowScrollableError != null)
                ShowScrollableError("Meeting Notes analysis failed", ex.Message);
            else
                MessageBox.Show($"Analysis failed:\n{ex.Message}", "Import Meeting Notes",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally
        {
            IsLoading = false;
        }

        if (RequestMeetingNotesPreview == null) return;
        var apply = await RequestMeetingNotesPreview(result, SelectedProject, input.WorkstreamId);
        if (!apply) return;

        IsLoading = true;
        var createdFiles = new List<string>();
        MeetingAsanaApplyResult asanaApply = new();
        try
        {
            var decisions = await _meetingNotesService.ApplyDecisionsAsync(
                result, SelectedProject, input.WorkstreamId);
            createdFiles.AddRange(decisions);

            var focusPath = await _meetingNotesService.ApplyFocusAsync(
                result, SelectedProject, input.WorkstreamId);
            if (focusPath != null) createdFiles.Add(focusPath);

            var openIssuesPath = await _meetingNotesService.ApplyOpenIssuesAsync(
                result, SelectedProject, input.WorkstreamId);
            if (openIssuesPath != null) createdFiles.Add(openIssuesPath);

            asanaApply = await _meetingNotesService.ApplyAsanaTasksAsync(
                result, SelectedProject, input.WorkstreamId, CancellationToken.None);
            if (asanaApply.FilePath != null) createdFiles.Add(asanaApply.FilePath);
        }
        catch (Exception ex)
        {
            IsLoading = false;
            MessageBox.Show($"Apply failed:\n{ex.Message}", "Import Meeting Notes",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally
        {
            IsLoading = false;
        }

        if (SelectedProject != null)
            BuildFileTree(SelectedProject);

        // 作成したファイルを順番に開く (decision_log 優先)
        var firstFile = createdFiles.FirstOrDefault(f => f.Contains("decision_log"))
                     ?? createdFiles.FirstOrDefault();
        if (firstFile != null)
            await OpenFileAndSelectNodeAsync(firstFile);

        var msgParts = new System.Text.StringBuilder();
        msgParts.AppendLine($"{createdFiles.Count} file(s) created/updated.");
        if (asanaApply.HasApiResult)
        {
            msgParts.Append($"{asanaApply.ApiSuccessCount} task(s) created in Asana");
            if (asanaApply.ApiFailCount > 0)
                msgParts.Append($", {asanaApply.ApiFailCount} failed");
            msgParts.AppendLine(".");
        }
        if (asanaApply.Errors.Count > 0)
        {
            msgParts.AppendLine("Errors:");
            foreach (var err in asanaApply.Errors)
                msgParts.AppendLine($"  - {err}");
        }
        MessageBox.Show(
            msgParts.ToString().TrimEnd(),
            "Import Meeting Notes",
            MessageBoxButton.OK,
            asanaApply.ApiFailCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private bool CanImportMeetingNotes() => SelectedProject != null && IsAiEnabled;

    // =====================================================================
    // 新規 decision_log 作成
    // =====================================================================
    [RelayCommand]
    public async Task NewDecisionLogAsync()
    {
        if (SelectedProject == null) return;

        // AI 有効時は AI フローへルーティング
        if (IsAiEnabled)
        {
            await NewAiDecisionLogAsync();
            return;
        }

        await CreateBlankDecisionLogAsync();
    }

    private async Task CreateBlankDecisionLogAsync()
    {
        if (SelectedProject == null) return;
        if (RequestNewDecisionLogName == null) return;

        var logName = await RequestNewDecisionLogName();
        if (string.IsNullOrWhiteSpace(logName)) return;

        var logDir = GetActiveDecisionLogDir();
        Directory.CreateDirectory(logDir);
        var fileName = $"{DateTime.Now:yyyy-MM-dd}_{logName.Trim()}.md";
        var filePath = Path.Combine(logDir, fileName);

        var template = $"""
# Decision: {logName.Trim()}

> Date: {DateTime.Now:yyyy-MM-dd}
> Status: Confirmed
> Trigger: Solo decision

## Context


## Options

### Option A: 
- Pros:
- Cons:

### Option B: 
- Pros:
- Cons:

## Chosen
Option : 

## Why


## Risk
-

## Revisit Trigger
-
""";
        await _encodingService.WriteFileAsync(filePath, template, "UTF8");

        if (SelectedProject != null)
            BuildFileTree(SelectedProject);

        await OpenFileAndSelectNodeAsync(filePath);
    }

    [RelayCommand]
    public async Task NewAiDecisionLogAsync()
    {
        if (SelectedProject == null) return;

        // API キー未設定チェック
        var settings = _configService.LoadSettings();
        if (string.IsNullOrWhiteSpace(settings.LlmApiKey))
        {
            MessageBox.Show(
                "LLM API key is not configured.\nPlease open Settings and set the API key under \"LLM API\".",
                "AI Decision Log",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // Step 1: 現在のファイルから workstream ID を判定
        var wsPath = !string.IsNullOrEmpty(CurrentFile) ? DetectWorkstreamPath(CurrentFile) : null;
        var wsId   = wsPath != null ? Path.GetFileName(wsPath) : null;

        // Step 2: 候補を検出 (失敗しても空リストで続行)
        IsLoading = true;
        _decisionLogCts = new CancellationTokenSource();
        List<DetectedDecision> candidates = [];
        try
        {
            candidates = await _decisionLogService.DetectCandidatesAsync(
                SelectedProject, wsId, _decisionLogCts.Token);
        }
        catch { /* 候補検出エラーは無視して続行 */ }
        finally
        {
            IsLoading = false;
        }

        // Step 3: 入力ダイアログを表示
        var prefillText = _capturedContextForDecisionLog;
        _capturedContextForDecisionLog = null;
        if (RequestAiDecisionLogInput == null) return;
        var input = await RequestAiDecisionLogInput(candidates, prefillText);
        if (input == null) return; // キャンセル

        if (input.UseBlankTemplate)
        {
            await CreateBlankDecisionLogAsync();
            return;
        }

        // Step 4: ドラフト生成
        IsLoading = true;
        _decisionLogCts = new CancellationTokenSource();
        try
        {
            var draft = await _decisionLogService.GenerateDraftAsync(
                input.UserInput,
                input.SelectedCandidates,
                input.Status,
                input.Trigger,
                SelectedProject,
                wsId,
                input.AttachedFilePaths,
                _decisionLogCts.Token);

            IsLoading = false;

            if (RequestDecisionLogPreview == null) return;

            var capturedDraft    = draft;
            var cts              = _decisionLogCts!;
            var refineHistory    = new List<(string instruction, string result)>();
            var initialUserPrompt = draft.DebugUserPrompt;
            var initialDraft     = draft.DraftContent;

            // Step 5: プレビューダイアログを表示
            var (save, content, fileName, removeTension) = await RequestDecisionLogPreview(
                draft,
                async (_, instructions) =>
                {
                    var refined = await _decisionLogService.RefineAsync(
                        initialUserPrompt, initialDraft, instructions, refineHistory, cts.Token);
                    refineHistory.Add((instructions, refined));
                    capturedDraft.DebugSystemPrompt = _llmClient.LastSystemPrompt;
                    capturedDraft.DebugUserPrompt   = BuildRefineDebugConversation(
                        initialUserPrompt, initialDraft, refineHistory);
                    capturedDraft.DebugResponse     = refined;
                    return refined;
                });

            if (!save) return;

            // Step 6: ファイル保存
            var logDir   = GetActiveDecisionLogDir();
            Directory.CreateDirectory(logDir);
            var topic        = (fileName ?? draft.SuggestedFileName).Trim();
            var baseFileName = $"{DateTime.Now:yyyy-MM-dd}_{topic}.md";
            var filePath     = GetUniqueDecisionLogPath(logDir, baseFileName);

            await _encodingService.WriteFileAsync(filePath, content ?? draft.DraftContent, "UTF8");

            // Step 7: open_issues.md 解決項目の削除 (ユーザーが承認した場合のみ)
            if (removeTension && !string.IsNullOrWhiteSpace(draft.ResolvedTension))
                await RemoveResolvedTensionAsync(draft.ResolvedTension, wsId);

            // Step 8: ツリー更新とエディタで開く
            if (SelectedProject != null)
                BuildFileTree(SelectedProject);
            await OpenFileAndSelectNodeAsync(filePath);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            IsLoading = false;
            if (ShowScrollableError != null)
                ShowScrollableError("AI Decision Log failed", ex.Message);
            else
                MessageBox.Show($"AI Decision Log failed:\n{ex.Message}", "AI Decision Log",
                    MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
            _decisionLogCts?.Dispose();
            _decisionLogCts = null;
        }
    }

    private static string GetUniqueDecisionLogPath(string logDir, string baseFileName)
    {
        var fullPath = Path.Combine(logDir, baseFileName);
        if (!File.Exists(fullPath)) return fullPath;

        var nameWithoutExt = Path.GetFileNameWithoutExtension(baseFileName);
        var ext            = Path.GetExtension(baseFileName);
        foreach (var suffix in "abcdefghijklmnopqrstuvwxyz")
        {
            var candidate = Path.Combine(logDir, $"{nameWithoutExt}_{suffix}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(logDir, $"{nameWithoutExt}_{DateTime.Now:HHmmss}{ext}");
    }

    private async Task RemoveResolvedTensionAsync(string tensionText, string? workstreamId)
    {
        if (SelectedProject == null) return;

        string? openIssuesPath = null;
        if (!string.IsNullOrWhiteSpace(workstreamId))
        {
            var wsPath = Path.Combine(
                SelectedProject.AiContextContentPath, "workstreams", workstreamId, "open_issues.md");
            if (File.Exists(wsPath)) openIssuesPath = wsPath;
        }
        if (openIssuesPath == null)
        {
            var rootPath = Path.Combine(SelectedProject.AiContextContentPath, "open_issues.md");
            if (File.Exists(rootPath)) openIssuesPath = rootPath;
        }
        if (openIssuesPath == null) return;

        try
        {
            var (content, enc) = await _encodingService.ReadFileAsync(openIssuesPath);
            var lines = content.Split('\n').ToList();
            var filtered = lines.Where(l =>
                !l.Trim().Contains(tensionText.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (filtered.Count < lines.Count)
                await _encodingService.WriteFileAsync(openIssuesPath, string.Join('\n', filtered), enc);
        }
        catch { /* エラーは無視 */ }
    }

    private string GetActiveDecisionLogDir()
    {
        if (!string.IsNullOrEmpty(CurrentFile))
        {
            var wsPath = DetectWorkstreamPath(CurrentFile);
            if (wsPath != null)
                return Path.Combine(wsPath, "decision_log");
        }
        return Path.Combine(SelectedProject!.AiContextContentPath, "decision_log");
    }

    private string? DetectWorkstreamPath(string filePath)
    {
        if (SelectedProject == null) return null;
        var wsBase = Path.Combine(SelectedProject.AiContextContentPath, "workstreams");
        if (!filePath.StartsWith(wsBase, StringComparison.OrdinalIgnoreCase)) return null;

        var relative = filePath[wsBase.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sep = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        var wsId = sep < 0 ? relative : relative[..sep];
        return string.IsNullOrEmpty(wsId) ? null : Path.Combine(wsBase, wsId);
    }

    // =====================================================================
    // decision_log 削除 (右クリック)
    // =====================================================================
    [RelayCommand]
    public void DeleteDecisionLog(FileTreeNode? node)
    {
        if (node == null || node.IsDirectory) return;
        if (!node.FullPath.Contains("decision_log")) return;

        var result = MessageBox.Show(
            $"Delete '{node.Name}'?",
            "Delete decision_log",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            File.Delete(node.FullPath);
            if (string.Equals(CurrentFile, node.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                EditorText = "";
                _originalContent = "";
                CurrentFile = "";
                IsDirty = false;
                UpdateStatus();
            }
            if (SelectedProject != null)
                BuildFileTree(SelectedProject);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to delete:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // =====================================================================
    // obsidian_notes 追加/削除
    // =====================================================================
    public bool CanAddObsidianNote(FileTreeNode? node)
    {
        if (SelectedProject == null) return false;
        return TryResolveObsidianTargetDirectory(node, out _);
    }

    public bool CanDeleteObsidianNote(FileTreeNode? node)
    {
        if (node == null || node.IsDirectory) return false;
        if (!string.Equals(Path.GetExtension(node.FullPath), ".md", StringComparison.OrdinalIgnoreCase))
            return false;
        return IsPathUnderObsidianNotes(node.FullPath);
    }

    public async Task<(bool Ok, string? FilePath, string? Error)> CreateObsidianNoteAsync(FileTreeNode? node, string requestedName)
    {
        if (!TryResolveObsidianTargetDirectory(node, out var targetDir))
            return (false, null, "Select a folder under obsidian_notes first.");

        var rawName = (requestedName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(rawName))
            return (false, null, "File name is required.");

        if (rawName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            return (false, null, "Nested paths are not supported. Enter only a file name.");

        var fileName = SanitizeFileName(rawName);
        if (string.IsNullOrWhiteSpace(fileName))
            return (false, null, "File name is invalid.");

        if (!fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            fileName += ".md";

        var filePath = Path.Combine(targetDir, fileName);
        if (File.Exists(filePath))
            return (false, null, $"'{fileName}' already exists.");

        var title = Path.GetFileNameWithoutExtension(fileName);
        var template = $"# {title}{Environment.NewLine}{Environment.NewLine}";

        try
        {
            await _encodingService.WriteFileAsync(filePath, template, "UTF8");

            if (SelectedProject != null)
                BuildFileTree(SelectedProject);

            await OpenFileAndSelectNodeAsync(filePath);
            return (true, filePath, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    public (bool Ok, string? Error) DeleteObsidianNote(FileTreeNode? node)
    {
        if (!CanDeleteObsidianNote(node))
            return (false, "Only Markdown files under obsidian_notes can be deleted.");

        try
        {
            File.Delete(node!.FullPath);
            if (string.Equals(CurrentFile, node.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                EditorText = "";
                _originalContent = "";
                CurrentFile = "";
                IsDirty = false;
                UpdateStatus();
            }

            if (SelectedProject != null)
                BuildFileTree(SelectedProject);

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private bool TryResolveObsidianTargetDirectory(FileTreeNode? node, out string directory)
    {
        directory = string.Empty;
        if (SelectedProject == null) return false;

        if (node == null)
        {
            directory = Path.Combine(SelectedProject.AiContextPath, "obsidian_notes");
            return Directory.Exists(directory);
        }

        directory = node.IsDirectory
            ? node.FullPath
            : Path.GetDirectoryName(node.FullPath) ?? string.Empty;

        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return false;

        return IsPathUnderObsidianNotes(directory);
    }

    private bool IsPathUnderObsidianNotes(string path)
    {
        if (SelectedProject == null) return false;
        var obsidianRoot = Path.Combine(SelectedProject.AiContextPath, "obsidian_notes");
        return IsPathUnderDirectory(path, obsidianRoot);
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var cleaned = new string(fileName
            .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
            .ToArray())
            .Trim();
        return cleaned.Trim('.');
    }

    // =====================================================================
    // Diff ビュートグル
    // =====================================================================
    [RelayCommand(CanExecute = nameof(CanToggleDiffView))]
    public void ToggleDiffView() => IsDiffViewActive = !IsDiffViewActive;

    private bool CanToggleDiffView() => CanUseDiff;

    // =====================================================================
    // 検索バートグル
    // =====================================================================
    [RelayCommand]
    public void ToggleSearchBar() => IsSearchBarVisible = !IsSearchBarVisible;

    // =====================================================================
    // Update Focus from Asana
    // =====================================================================

    /// <summary>
    /// Quick Capture の focus_update ルーティングから呼ばれる。
    /// 次に current_focus.md が開かれたタイミングで UpdateFocusAsync を自動発火し、
    /// キャプチャ内容を LLM プロンプトの追加コンテキストとして渡す。
    /// </summary>
    public void RequestFocusUpdateOnOpen(string capturedText)
        => _capturedContextForFocusUpdate = capturedText;

    public void RequestDecisionLogOnOpen(string capturedText)
        => _capturedContextForDecisionLog = capturedText;

    public async Task TriggerDecisionLogIfPendingAsync()
    {
        if (_capturedContextForDecisionLog != null && SelectedProject != null)
            await NewAiDecisionLogAsync();
    }

    /// <summary>
    /// Quick Capture focus_update ルーティング後、ApplicationIdle/Background タイミングから呼ばれる。
    /// current_focus.md が既に開かれていれば UpdateFocusAsync を発火する。
    /// </summary>
    public void TriggerFocusUpdateIfPending()
    {
        if (_capturedContextForFocusUpdate != null
            && SelectedProject != null
            && IsCurrentFocusFile(CurrentFile))
        {
            _ = UpdateFocusAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanUpdateFocus))]
    public async Task UpdateFocusAsync()
    {
        // Quick Capture コンテキストを即座に取得・クリア (多重発火防止)
        var capturedContext = _capturedContextForFocusUpdate;
        _capturedContextForFocusUpdate = null;

        if (SelectedProject == null) return;

        // API キー未設定チェック
        var settings = _configService.LoadSettings();
        if (string.IsNullOrWhiteSpace(settings.LlmApiKey))
        {
            MessageBox.Show(
                "LLM API key is not configured.\nPlease open Settings and set the API key under \"LLM API\".",
                "Update Focus",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // LLM 処理開始前にローディング開始 (LoadProjectsAsync の finally より先に CTS をセット)
        IsLoading = true;
        _focusUpdateCts = new CancellationTokenSource();

        // Workstream 選択: 1件でも必ずダイアログで確認 (general or workstream)
        string? workstreamId = null;
        var activeWorkstreams = SelectedProject.Workstreams.Where(w => !w.IsClosed).ToList();
        if (activeWorkstreams.Count > 0 && RequestWorkstreamSelection != null)
        {
            var (ok, wsId) = await RequestWorkstreamSelection(activeWorkstreams);
            if (!ok)
            {
                // キャンセル時はローディングを解除して終了
                IsLoading = false;
                _focusUpdateCts.Dispose();
                _focusUpdateCts = null;
                return;
            }
            workstreamId = wsId; // null = general
        }
        try
        {
            var result = await _focusUpdateService.GenerateProposalAsync(
                SelectedProject, workstreamId, _focusUpdateCts.Token, capturedContext);

            if (RequestFocusUpdateApproval == null) return;
            var capturedResult = result;
            var cts = _focusUpdateCts!;

            _trayService.ShowBalloonTip("Focus Update Ready", "Proposal is ready for review.");

            // Refine 会話履歴 (instruction → refined result の積み上げ)
            var refineHistory = new List<(string instruction, string result)>();
            var initialUserPrompt = capturedResult.DebugUserPrompt;
            var initialProposed   = capturedResult.ProposedContent;

            var (apply, content) = await RequestFocusUpdateApproval(
                result,
                async (_, instructions) =>
                {
                    var refined = await _focusUpdateService.RefineAsync(
                        initialUserPrompt, initialProposed, instructions, refineHistory, cts.Token);
                    refineHistory.Add((instructions, refined));
                    // Refine 後のデバッグ情報を更新 (View Debug で送信した全会話が見えるようにする)
                    capturedResult.DebugSystemPrompt = _llmClient.LastSystemPrompt;
                    capturedResult.DebugUserPrompt   = BuildRefineDebugConversation(
                        initialUserPrompt, initialProposed, refineHistory);
                    capturedResult.DebugResponse     = refined;
                    return refined;
                });
            if (!apply) return;

            // 適用
            var finalContent = content ?? result.ProposedContent;

            // 更新日付を今日で上書き
            finalContent = UpdateDateLine(finalContent);

            await _encodingService.WriteFileAsync(result.TargetFocusPath, finalContent, "UTF8");

            // focus_history スナップショット
            var histDir = Path.Combine(Path.GetDirectoryName(result.TargetFocusPath)!, "focus_history");
            Directory.CreateDirectory(histDir);
            var snapPath = Path.Combine(histDir, $"{DateTime.Now:yyyy-MM-dd}.md");
            await _encodingService.WriteFileAsync(snapPath, finalContent, "UTF8");

            // Editor で current_focus.md を開く
            if (SelectedProject != null)
                BuildFileTree(SelectedProject);
            await OpenFileAndSelectNodeAsync(result.TargetFocusPath);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ShowScrollableError != null)
                ShowScrollableError("Update Focus failed", ex.Message);
            else
                MessageBox.Show($"Update Focus failed:\n{ex.Message}", "Update Focus",
                    MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
            _focusUpdateCts?.Dispose();
            _focusUpdateCts = null;
        }
    }

    private bool CanUpdateFocus() => SelectedProject != null && IsCurrentFocusFile(CurrentFile);

    private static string BuildRefineDebugConversation(
        string initialUserPrompt,
        string initialProposed,
        IReadOnlyList<(string instruction, string result)> history)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[user — initial prompt]");
        sb.AppendLine(initialUserPrompt);
        sb.AppendLine();
        sb.AppendLine("[assistant — initial proposal]");
        sb.AppendLine(initialProposed);
        foreach (var (instr, res) in history.Take(history.Count - 1))
        {
            sb.AppendLine();
            sb.AppendLine("[user — refine instruction]");
            sb.AppendLine(instr);
            sb.AppendLine();
            sb.AppendLine("[assistant — refined result]");
            sb.AppendLine(res);
        }
        if (history.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("[user — latest refine instruction]");
            sb.AppendLine(history[^1].instruction);
        }
        return sb.ToString();
    }

    private static string UpdateDateLine(string content)
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var datePattern = new System.Text.RegularExpressions.Regex(
            @"(更新:|Last Updated:)\s*\d{4}-\d{2}-\d{2}",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (datePattern.IsMatch(content))
            return datePattern.Replace(content, m => $"{m.Groups[1].Value} {today}");

        // どちらのパターンもなければ変更しない (LLM が既に処理済みとみなす)
        return content;
    }

    // =====================================================================
    // ステータスバー更新 (メッセンジャー経由)
    // =====================================================================
    private void UpdateStatus()
    {
        var nextCanUseDiff = IsCurrentFocusFile(CurrentFile);
        if (CanUseDiff != nextCanUseDiff)
        {
            CanUseDiff = nextCanUseDiff;
            if (!nextCanUseDiff)
                IsDiffViewActive = false;
            ToggleDiffViewCommand.NotifyCanExecuteChanged();
        }

        UpdateFocusCommand.NotifyCanExecuteChanged();

        var projectLabel = SelectedProject?.Name ?? "";
        var fileLabel = string.IsNullOrEmpty(CurrentFile) ? "" : Path.GetFileName(CurrentFile);

        // メッセンジャー経由で MainWindowViewModel へ通知
        WeakReferenceMessenger.Default.Send(new StatusUpdateMessage(projectLabel, fileLabel, Encoding, IsDirty));

    }

    private static bool IsCurrentFocusFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return string.Equals(Path.GetFileName(path), "current_focus.md", StringComparison.OrdinalIgnoreCase);
    }
}
