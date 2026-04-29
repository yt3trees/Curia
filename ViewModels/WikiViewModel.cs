using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Curia.Models;
using Curia.Services;
using Curia.Views;

namespace Curia.ViewModels;

public enum WikiTab { Pages, Query, Lint, Prompts }

// ────────────────────────────────────────────────────
// カテゴリ表示用アイテム
// ────────────────────────────────────────────────────

public class WikiCategoryItem
{
    public string Name { get; set; } = "";
    public bool IsDeletable { get; set; }
}

// ────────────────────────────────────────────────────
// ページツリー用アイテム
// ────────────────────────────────────────────────────

public partial class WikiTreeItem : ObservableObject
{
    public string Title { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public bool IsCategory { get; set; }
    public ObservableCollection<WikiTreeItem> Children { get; } = [];

    [ObservableProperty] private bool isExpanded = true;
}

// ────────────────────────────────────────────────────
// Lint Issue 表示用
// ────────────────────────────────────────────────────

public class WikiLintIssueViewModel
{
    public string Category { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Description { get; set; } = "";
    public string? PagePath { get; set; }
    public string SeverityColor => Severity switch
    {
        "High"   => "#F38BA8",
        "Medium" => "#FAB387",
        _        => "#A6E3A1"
    };
}

// ────────────────────────────────────────────────────
// WikiViewModel
// ────────────────────────────────────────────────────

public partial class WikiViewModel : ObservableObject
{
    private readonly ProjectDiscoveryService _discovery;
    private readonly ConfigService _config;
    private readonly WikiService _wikiService;
    private readonly WikiIngestService _ingestService;
    private readonly WikiQueryService _queryService;
    private readonly WikiLintService _lintService;
    private readonly LlmClientService _llmClient;
    private readonly TrayService _trayService;

    private CancellationTokenSource? _cts;
    private bool _suppressDirtyTracking;
    private string _loadedPageContent = "";

    // ── プロジェクト選択 ──────────────────────────────
    [ObservableProperty] private ObservableCollection<ProjectInfo> projects = [];
    [ObservableProperty] private ProjectInfo? selectedProject;
    [ObservableProperty] private ObservableCollection<string> domains = [];
    [ObservableProperty] private string? selectedDomain;
    [ObservableProperty] private bool hasWiki;
    [ObservableProperty] private string wikiRoot = "";

    // ── タブ切替 ──────────────────────────────────────
    [ObservableProperty] private WikiTab activeTab = WikiTab.Pages;
    public bool IsPagesTab   => ActiveTab == WikiTab.Pages;
    public bool IsQueryTab   => ActiveTab == WikiTab.Query;
    public bool IsLintTab    => ActiveTab == WikiTab.Lint;
    public bool IsPromptsTab => ActiveTab == WikiTab.Prompts;

    // ── カテゴリ管理 ─────────────────────────────────
    private static readonly HashSet<string> DefaultCategoryNames =
        new(["sources", "entities", "concepts", "analysis"], StringComparer.OrdinalIgnoreCase);

    [ObservableProperty] private bool isWikiReadOnly;
    [ObservableProperty] private string wikiReadOnlyReason = "";
    [ObservableProperty] private ObservableCollection<WikiCategoryItem> definedCategories = [];
    [ObservableProperty] private ObservableCollection<string> undefinedCategories = [];
    [ObservableProperty] private string agentsMdCategoryWarning = "";
    [ObservableProperty] private bool hasAgentsMdWarning;
    [ObservableProperty] private string newCategoryName = "";
    private WikiCategoriesConfig _currentCategoriesConfig = new();

    // ── Prompt Settings ───────────────────────────────
    [ObservableProperty] private string promptImportSystemPrefix = "";
    [ObservableProperty] private string promptImportSystemSuffix = "";
    [ObservableProperty] private string promptImportUserSuffix = "";
    [ObservableProperty] private string promptQuerySystemPrefix = "";
    [ObservableProperty] private string promptQuerySystemSuffix = "";
    [ObservableProperty] private string promptQueryUserSuffix = "";
    [ObservableProperty] private string promptLintSystemPrefix = "";
    [ObservableProperty] private string promptLintSystemSuffix = "";
    [ObservableProperty] private string promptLintUserSuffix = "";
    [ObservableProperty] private bool isPromptReadOnly;
    [ObservableProperty] private string promptStatusText = "";
    [ObservableProperty] private string defaultImportSystemPrompt = "";
    [ObservableProperty] private string defaultQuerySystemPrompt = "";
    [ObservableProperty] private string defaultLintSystemPrompt = "";

    // ── Wiki Schema ───────────────────────────────────
    [ObservableProperty] private string wikiSchemaContent = "";
    [ObservableProperty] private bool isWikiSchemaDirty;
    [ObservableProperty] private string wikiSchemaStatusText = "";
    private bool _suppressWikiSchemaDirtyTracking;

    // ── Pages タブ ───────────────────────────────────
    [ObservableProperty] private ObservableCollection<WikiTreeItem> pageTree = [];
    [ObservableProperty] private WikiTreeItem? selectedTreeItem;
    [ObservableProperty] private string previewContent = "";
    [ObservableProperty] private string selectedPagePath = "";
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private bool isPageDirty;
    [ObservableProperty] private bool isEditMode;
    public bool IsRenderMode => !IsEditMode;

    // ── Query タブ ───────────────────────────────────
    [ObservableProperty] private string queryText = "";
    [ObservableProperty] private string queryAnswer = "";
    [ObservableProperty] private ObservableCollection<string> queryReferencedPages = [];
    [ObservableProperty] private ObservableCollection<WikiQueryRecord> queryHistory = [];
    [ObservableProperty] private ObservableCollection<WikiQueryRecord> conversationLog = [];
    [ObservableProperty] private ObservableCollection<WikiQueryRecord> sessionPreviewRecords = [];
    [ObservableProperty] private bool isQuerying;
    [ObservableProperty] private bool hasQueryAnswer;
    [ObservableProperty] private WikiQueryRecord? lastQueryRecord;
    [ObservableProperty] private bool hasMoreHistory;
    [ObservableProperty] private WikiQueryRecord? selectedQueryRecord;

    private string _currentSessionId = "";
    private bool _currentSessionHistoryAdded;
    private List<string> _pastSessionFiles = [];
    private int _nextSessionIndex;

    // ── Lint タブ ─────────────────────────────────────
    [ObservableProperty] private ObservableCollection<WikiLintIssueViewModel> lintIssues = [];
    [ObservableProperty] private string lintLastRun = "Not run yet";
    [ObservableProperty] private bool isLinting;
    [ObservableProperty] private int lintHighCount;
    [ObservableProperty] private int lintMediumCount;
    [ObservableProperty] private int lintLowCount;

    // ── 共通 ─────────────────────────────────────────
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool isImporting;
    [ObservableProperty] private bool isAiEnabled;
    [ObservableProperty] private string statusText = "";
    [ObservableProperty] private int editorFontSize = 14;
    [ObservableProperty] private int markdownRenderFontSize = 13;
    [ObservableProperty] private string editorTextColor = "";
    [ObservableProperty] private string markdownRenderTextColor = "";

    // ── Wiki 初期化 ───────────────────────────────────
    [ObservableProperty] private string newWikiDomain = "";
    [ObservableProperty] private bool isCreatingNewDomain;

    public bool IsProjectSelected => SelectedProject != null;
    public bool IsDomainSelected  => !string.IsNullOrEmpty(SelectedDomain);

    public WikiViewModel(
        ProjectDiscoveryService discovery,
        ConfigService config,
        WikiService wikiService,
        WikiIngestService ingestService,
        WikiQueryService queryService,
        WikiLintService lintService,
        LlmClientService llmClient,
        TrayService trayService)
    {
        _discovery    = discovery;
        _config       = config;
        _wikiService  = wikiService;
        _ingestService = ingestService;
        _queryService = queryService;
        _lintService  = lintService;
        _llmClient    = llmClient;
        _trayService  = trayService;

        var settings = _config.LoadSettings();
        IsAiEnabled              = settings.AiEnabled;
        EditorFontSize           = settings.EditorFontSize;
        MarkdownRenderFontSize   = settings.MarkdownRenderFontSize;
        EditorTextColor          = settings.EditorTextColor;
        MarkdownRenderTextColor  = settings.MarkdownRenderTextColor;
        WeakReferenceMessenger.Default.Register<AiEnabledChangedMessage>(this,
            (_, msg) => IsAiEnabled = msg.Enabled);
        WeakReferenceMessenger.Default.Register<FontSizeChangedMessage>(this, (_, msg) =>
        {
            EditorFontSize         = msg.EditorFontSize;
            MarkdownRenderFontSize = msg.MarkdownRenderFontSize;
        });
        WeakReferenceMessenger.Default.Register<TextColorChangedMessage>(this, (_, msg) =>
        {
            EditorTextColor         = msg.EditorTextColor;
            MarkdownRenderTextColor = msg.MarkdownRenderTextColor;
        });
    }

    // ────────────────────────────────────────────────
    // 初期化
    // ────────────────────────────────────────────────

    public async Task InitAsync()
    {
        IsLoading = true;
        try
        {
            var all = await _discovery.GetProjectInfoListAsync(force: false);
            var hidden = _config.LoadHiddenProjects();
            var visible = all.Where(p => !hidden.Contains(p.HiddenKey)).ToList();

            Projects.Clear();
            foreach (var p in visible) Projects.Add(p);
            // SelectedProject = Projects[0]; // 自動選択を停止 (AC-68)
        }
        finally { IsLoading = false; }
    }

    // ────────────────────────────────────────────────
    // プロジェクト変更
    // ────────────────────────────────────────────────

    partial void OnSelectedProjectChanged(ProjectInfo? value)
    {
        OnPropertyChanged(nameof(IsProjectSelected));
        if (value == null) { HasWiki = false; WikiRoot = ""; Domains.Clear(); SelectedDomain = null; return; }

        var contextPath = GetContextPath(value);
        if (contextPath == null) { HasWiki = false; WikiRoot = ""; Domains.Clear(); SelectedDomain = null; return; }

        var domainList = WikiService.GetDomains(contextPath);
        Domains.Clear();
        foreach (var d in domainList) Domains.Add(d);

        // 常に SelectedDomain を null にして、ユーザーに選択させる (AC-68)
        SelectedDomain = null;
        HasWiki = false;
        WikiRoot = "";
    }

    partial void OnSelectedDomainChanged(string? value)
    {
        if (SelectedProject == null || string.IsNullOrEmpty(value))
        {
            HasWiki = false;
            WikiRoot = "";
            return;
        }

        var contextPath = GetContextPath(SelectedProject);
        if (contextPath == null) return;

        var root = WikiService.GetWikiRoot(contextPath, value);
        WikiRoot = root;
        HasWiki = Directory.Exists(root);

        if (HasWiki)
        {
            _currentSessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _queryService.StartNewSession(WikiRoot, _currentSessionId);
            QueryHistory.Clear();
            ConversationLog.Clear();
            SessionPreviewRecords.Clear();
            _currentSessionHistoryAdded = false;
            HasQueryAnswer = false;
            LastQueryRecord = null;
            QueryReferencedPages.Clear();
            HasMoreHistory = false;
            _ = LoadInitialHistoryAsync();
            _ = InitializeWikiDomainAsync();
        }
    }

    // ────────────────────────────────────────────────
    // タブ切替
    // ────────────────────────────────────────────────

    partial void OnActiveTabChanged(WikiTab value)
    {
        OnPropertyChanged(nameof(IsPagesTab));
        OnPropertyChanged(nameof(IsQueryTab));
        OnPropertyChanged(nameof(IsLintTab));
        OnPropertyChanged(nameof(IsPromptsTab));
    }

    partial void OnIsEditModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsRenderMode));
    }

    [RelayCommand] private void SwitchToPages()   => ActiveTab = WikiTab.Pages;
    [RelayCommand] private void SwitchToQuery()   => ActiveTab = WikiTab.Query;
    [RelayCommand] private void SwitchToLint()    => ActiveTab = WikiTab.Lint;
    [RelayCommand] private void SwitchToPrompts() => ActiveTab = WikiTab.Prompts;

    [RelayCommand] private void ShowCreateDomain() => IsCreatingNewDomain = true;
    [RelayCommand] private void CancelCreateDomain() => IsCreatingNewDomain = false;

    // ────────────────────────────────────────────────
    // Pages タブ
    // ────────────────────────────────────────────────

    /// <summary>Wiki ドメイン切替時の初期化（リカバリ → カテゴリロード → ページロード）。</summary>
    private async Task InitializeWikiDomainAsync()
    {
        if (!HasWiki) return;

        // 起動時トランザクション復旧 (AC-28, AC-29, AC-38, AC-39)
        try { await Task.Run(() => _wikiService.RecoverPendingTransactionAsync(WikiRoot)); } catch { }
        try { await Task.Run(() => _wikiService.RecoverPendingRenameAsync(WikiRoot)); }      catch { }

        // カテゴリ設定ロード
        await LoadCategoriesAsync();

        // Prompt 設定ロード
        LoadPromptSettings();

        // Wiki Schema ロード
        await LoadWikiSchemaAsync();

        // AGENTS.md 整合チェック (AC-44)
        CheckAgentsMdConsistency();

        await LoadPagesAsync();
    }

    private async Task LoadCategoriesAsync()
    {
        _currentCategoriesConfig = await Task.Run(() => _wikiService.LoadCategories(WikiRoot));

        // 読み取り専用判定 (AC-51, AC-67)
        if (_currentCategoriesConfig.IsUnknownVersion)
        {
            IsWikiReadOnly = true;
            WikiReadOnlyReason = "Unsupported .wiki-categories.json version. Wiki is in read-only mode.";
        }
        else if (_currentCategoriesConfig.HasNamingConflict)
        {
            IsWikiReadOnly = true;
            WikiReadOnlyReason = $"Category naming conflict: {_currentCategoriesConfig.ConflictDetail}. Resolve conflicts and reload.";
        }
        else
        {
            IsWikiReadOnly = false;
            WikiReadOnlyReason = "";
        }

        // カテゴリ表示リスト更新 (AC-1, AC-55)
        var (defined, undefined) = _wikiService.GetCategoryDisplayList(WikiRoot);
        DefinedCategories.Clear();
        foreach (var c in defined)
            DefinedCategories.Add(new WikiCategoryItem
            {
                Name = c,
                IsDeletable = !DefaultCategoryNames.Contains(c)
            });
        UndefinedCategories.Clear();
        foreach (var c in undefined) UndefinedCategories.Add(c);
    }

    [RelayCommand]
    private async Task ReloadCategories()
    {
        await LoadCategoriesAsync();
        CheckAgentsMdConsistency();
        await LoadPagesAsync();
        StatusText = "Categories reloaded.";
    }

    private void CheckAgentsMdConsistency()
    {
        if (!HasWiki) return;
        var (isValid, issue) = _wikiService.CheckAgentsMdCategoryBlock(WikiRoot, _currentCategoriesConfig.Categories);
        if (!isValid && issue != null)
        {
            AgentsMdCategoryWarning = issue;
            HasAgentsMdWarning = true;
        }
        else
        {
            AgentsMdCategoryWarning = "";
            HasAgentsMdWarning = false;
        }
    }

    [RelayCommand]
    private async Task RepairAgentsMd()
    {
        if (!HasWiki) return;
        try
        {
            await _wikiService.UpdateAgentsMdCategoryBlockAsync(WikiRoot, _currentCategoriesConfig.Categories);
            CheckAgentsMdConsistency();
            StatusText = "AGENTS.md category block repaired.";
        }
        catch (Exception ex) { StatusText = $"Repair failed: {ex.Message}"; }
    }

    private async Task LoadPagesAsync()
    {
        if (!HasWiki) return;
        IsLoading = true;
        try
        {
            var pages = await Task.Run(() => _wikiService.GetAllPages(WikiRoot));
            BuildPageTree(pages);
        }
        finally { IsLoading = false; }
    }

    private void BuildPageTree(IReadOnlyList<WikiPageItem> pages)
    {
        PageTree.Clear();

        // 1. ルートファイル (index.md / log.md)
        var rootItems = pages.Where(p => p.IsRoot).OrderBy(p => p.RelativePath).ToList();
        if (rootItems.Count > 0)
        {
            var rootNode = new WikiTreeItem { Title = "Wiki Files", IsCategory = true, IsExpanded = true };
            foreach (var p in rootItems)
                rootNode.Children.Add(new WikiTreeItem { Title = p.Title, RelativePath = p.RelativePath });
            PageTree.Add(rootNode);
        }

        // 2. カテゴリ別のツリー構築
        // 表示順: sources 先頭 → 設定ファイル順（DefinedCategories） → 未定義（名前昇順）
        var definedNames = DefinedCategories.Select(x => x.Name).ToList();
        var allCategoryNames = definedNames.Concat(UndefinedCategories).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var catName in allCategoryNames)
        {
            var catLower = catName.ToLowerInvariant();
            var catPages = pages.Where(p => p.Category.Equals(catLower, StringComparison.OrdinalIgnoreCase))
                                .OrderBy(p => p.RelativePath).ToList();

            if (catPages.Count == 0 && !Directory.Exists(Path.Combine(WikiService.GetPagesDir(WikiRoot), catLower)))
                continue;

            // カテゴリのトップレベルノード
            var isUndefined = UndefinedCategories.Contains(catName, StringComparer.OrdinalIgnoreCase);
            var displayTitle = isUndefined ? $"{catLower} (read-only)" : catLower;
            
            // sources はデフォルトで閉じる (AC-68)
            bool isSources = string.Equals(catLower, "sources", StringComparison.OrdinalIgnoreCase);
            var categoryNode = new WikiTreeItem 
            { 
                Title = displayTitle, 
                IsCategory = true, 
                IsExpanded = !isSources 
            };

            // 階層構造の構築
            BuildRecursiveTree(categoryNode, catPages, $"pages/{catLower}/");

            if (categoryNode.Children.Count > 0)
            {
                PageTree.Add(categoryNode);
            }
        }
    }

    /// <summary>
    /// ファイルパスのリストから再帰的なツリー構造を構築する。
    /// </summary>
    private void BuildRecursiveTree(WikiTreeItem parent, List<WikiPageItem> pages, string basePrefix)
    {
        var folders = new Dictionary<string, WikiTreeItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var page in pages)
        {
            var relPath = page.RelativePath;
            if (!relPath.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase)) continue;

            var subPath = relPath[basePrefix.Length..];
            var segments = subPath.Split('/');

            var currentParent = parent;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                var folderName = segments[i];
                if (!folders.TryGetValue(folderName, out var folderNode))
                {
                    folderNode = new WikiTreeItem { Title = folderName, IsCategory = true, IsExpanded = true };
                    currentParent.Children.Add(folderNode);
                    folders[folderName] = folderNode;
                }
                currentParent = folderNode;
            }

            // ファイルを追加
            currentParent.Children.Add(new WikiTreeItem 
            { 
                Title = page.Title, 
                RelativePath = page.RelativePath 
            });
        }
    }

    partial void OnSelectedTreeItemChanged(WikiTreeItem? value)
    {
        if (value == null || value.IsCategory || string.IsNullOrEmpty(value.RelativePath)) return;
        _ = LoadPreviewAsync(value.RelativePath);
    }

    private async Task LoadPreviewAsync(string relativePath)
    {
        SelectedPagePath = relativePath;
        var page = await Task.Run(() => _wikiService.GetPage(WikiRoot, relativePath));
        _suppressDirtyTracking = true;
        PreviewContent = page?.Content ?? "";
        _loadedPageContent = PreviewContent;
        IsPageDirty = false;
        IsEditMode = false;
        _suppressDirtyTracking = false;
    }

    partial void OnPreviewContentChanged(string value)
    {
        if (_suppressDirtyTracking || string.IsNullOrEmpty(SelectedPagePath))
            return;

        IsPageDirty = !string.Equals(value, _loadedPageContent, StringComparison.Ordinal);
    }

    partial void OnSearchTextChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            _ = LoadPagesAsync();
            return;
        }
        _ = SearchPagesAsync(value);
    }

    private async Task SearchPagesAsync(string query)
    {
        if (!HasWiki) return;
        var pages = await Task.Run(() => _wikiService.GetAllPages(WikiRoot)
            .Where(p => p.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        p.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList());
        BuildPageTree(pages);
    }

    [RelayCommand]
    private async Task NavigateToPage(string pageTitle)
    {
        if (string.IsNullOrEmpty(pageTitle) || !HasWiki) return;

        if (PageTree.Count == 0)
            await LoadPagesAsync();

        WikiTreeItem? found = null;
        foreach (var category in PageTree)
        {
            found = category.Children.FirstOrDefault(c =>
                string.Equals(c.Title, pageTitle, StringComparison.OrdinalIgnoreCase));
            if (found != null) break;
        }
        if (found == null) return;

        ActiveTab = WikiTab.Pages;
        SelectedTreeItem = found;
    }

    [RelayCommand]
    private void OpenPageInEditor()
    {
        if (string.IsNullOrEmpty(SelectedPagePath) || SelectedProject == null) return;
        var full = Path.Combine(WikiRoot, SelectedPagePath.Replace('/', Path.DirectorySeparatorChar));
        OnOpenInEditor?.Invoke(SelectedProject, full);
    }

    [RelayCommand]
    private async Task SaveCurrentPage()
    {
        if (!HasWiki || string.IsNullOrEmpty(SelectedPagePath)) return;
        if (IsWikiReadOnly) { StatusText = $"Save rejected: {WikiReadOnlyReason}"; return; }

        // ルートファイル (index.md / log.md) はバリデーションスキップ
        if (SelectedPagePath is not ("index.md" or "log.md"))
        {
            var cats = _currentCategoriesConfig.Categories;
            var vr = _wikiService.ValidatePagePath(WikiRoot, SelectedPagePath, cats);
            if (!vr.IsValid)
            {
                StatusText = $"Save rejected: {vr.ErrorReason}";
                return;
            }
        }

        try
        {
            await _wikiService.SavePage(WikiRoot, SelectedPagePath, PreviewContent, _currentCategoriesConfig.Categories);
            _loadedPageContent = PreviewContent;
            IsPageDirty = false;
            StatusText = $"Saved: {SelectedPagePath}";
        }
        catch (Exception ex) { StatusText = $"Save failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void SwitchToEditMode() => IsEditMode = true;

    [RelayCommand]
    private void SwitchToRenderMode() => IsEditMode = false;

    public Action<ProjectInfo, string>? OnOpenInEditor { get; set; }

    // ── Ingest ────────────────────────────────────

    [RelayCommand]
    private async Task IngestSource(object? parameter)
    {
        if (!HasWiki || parameter == null) return;

        List<string> files;
        string supplementaryPrompt = "";

        switch (parameter)
        {
            case (string[] arr, string prompt):
                files = arr.ToList();
                supplementaryPrompt = prompt;
                break;
            case IEnumerable<string> e:
                files = e.ToList();
                break;
            case string s:
                files = [s];
                break;
            default:
                files = [];
                break;
        }

        if (files.Count == 0) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        IsImporting = true;
        try
        {
            var wikiRoot = WikiRoot;
            var progress = new Progress<string>(msg =>
            {
                StatusText = msg;
                WikiIngestService.AppendLog(wikiRoot, msg);
            });
            int count = 0;

            foreach (var file in files)
            {
                count++;
                var statusMsg = $"Ingesting ({count}/{files.Count}): {Path.GetFileName(file)}...";
                StatusText = statusMsg;
                WikiIngestService.AppendLog(wikiRoot, $"--- Start: {file} ---");

                var result = await _ingestService.GenerateIngestProposal(WikiRoot, file, supplementaryPrompt, progress, _cts.Token);
                if (!result.Success)
                {
                    var errMsg = $"Error on {Path.GetFileName(file)}: {result.ErrorMessage}";
                    StatusText = errMsg;
                    WikiIngestService.AppendLog(wikiRoot, $"[GenerateProposal] FAILED: {result.ErrorMessage}");
                    var lowerError = result.ErrorMessage?.ToLowerInvariant() ?? "";
                    if (lowerError.Contains("cancelled"))
                    {
                        _trayService.ShowBalloonTip(
                            "Wiki Import Cancelled",
                            $"{Path.GetFileName(file)}: Import was cancelled.");
                    }
                    else if (lowerError.Contains("timed out"))
                    {
                        _trayService.ShowBalloonTip(
                            "Wiki Import Timeout",
                            $"{Path.GetFileName(file)}: LLM request timed out.");
                    }
                    // 1つ失敗しても次へ進む（必要に応じて break しても良い）
                    await Task.Delay(2000); // エラーメッセージを見せるため少し待機
                    continue;
                }

                // LLM がページ変更不要と判断した場合はバルーン通知して index/log のみ更新
                if (result.NewPages.Count == 0 && result.UpdatedPages.Count == 0)
                {
                    var noChangeSummary = string.IsNullOrWhiteSpace(result.Summary)
                        ? "No page changes needed."
                        : result.Summary;
                    StatusText = $"No page changes: {Path.GetFileName(file)} — {noChangeSummary}";
                    WikiIngestService.AppendLog(wikiRoot, $"[Review] No page changes needed: {noChangeSummary}");
                    _trayService.ShowBalloonTip(
                        "Wiki Ingest — No Changes",
                        $"{Path.GetFileName(file)}: {noChangeSummary}");
                    // index.md / log.md だけ更新する
                    await _ingestService.ApplyIngestResult(WikiRoot, result, progress, _cts.Token);
                    await Task.Delay(3000);
                    continue;
                }

                if (!await ReviewIngestChangesAsync(file, result, _cts.Token))
                {
                    StatusText = $"Skipped: {Path.GetFileName(file)} (no changes saved)";
                    WikiIngestService.AppendLog(wikiRoot, $"[Review] Skipped by user: {file}");
                    continue;
                }

                WikiIngestService.AppendLog(wikiRoot, $"[Review] Approved by user: {file}");
                await _ingestService.ApplyIngestResult(WikiRoot, result, progress, _cts.Token);
                if (!result.Success)
                {
                    var errMsg = $"Error saving {Path.GetFileName(file)}: {result.ErrorMessage}";
                    StatusText = errMsg;
                    WikiIngestService.AppendLog(wikiRoot, $"[ApplyIngest] FAILED: {result.ErrorMessage}");
                    await Task.Delay(3000);
                    continue;
                }
                StatusText = $"Saved: {Path.GetFileName(file)}";
                WikiIngestService.AppendLog(wikiRoot, $"--- Done: {file} ---");
            }

            StatusText = $"Ingest complete. Processed {files.Count} files.";
            await LoadPagesAsync();
            WikiIngestService.AppendLog(wikiRoot, $"[LoadPages] After ingest: pageTree count={PageTree.Count}, wikiRoot={WikiRoot}");
        }
        catch (OperationCanceledException)
        {
            StatusText = "Ingest cancelled.";
            _trayService.ShowBalloonTip("Wiki Import Cancelled", "Import was cancelled.");
        }
        finally { IsImporting = false; }
    }

    private async Task<bool> ReviewIngestChangesAsync(string sourceFilePath, IngestResult result, CancellationToken cancellationToken)
    {
        var reviewItems = BuildIngestReviewItems(result);
        if (reviewItems.Count == 0)
            return true;

        var owner = Application.Current?.MainWindow;
        if (owner == null)
            throw new InvalidOperationException("Main window is not available for review dialog.");

        _trayService.ShowBalloonTip(
            "Wiki Ingest Review",
            $"{Path.GetFileName(sourceFilePath)}: {reviewItems.Count} page(s) ready for review.");

        for (int i = 0; i < reviewItems.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = reviewItems[i];
            var proposal = new FileUpdateProposal
            {
                CurrentContent = item.CurrentContent,
                ProposedContent = item.ProposedContent,
                Summary = item.Summary,
                DebugSystemPrompt = result.DebugSystemPrompt,
                DebugUserPrompt = result.DebugUserPrompt,
                DebugResponse = result.DebugResponse
            };

            var indexLabel = $"{i + 1}/{reviewItems.Count}";
            var extraInfo = $"Source: {Path.GetFileName(sourceFilePath)}\nPath: {item.Path}\nType: {(item.IsNew ? "New page" : "Update page")}";
            var dialogTitle = item.IsNew
                ? $"Review New Wiki Page ({indexLabel})"
                : $"Review Updated Wiki Page ({indexLabel})";

            var dialogTask = await owner.Dispatcher.InvokeAsync(() =>
                ProposalReviewDialog.ShowAsync(
                    owner,
                    proposal,
                    dialogTitle,
                    titleIcon: item.IsNew ? "＋" : "⟳",
                    extraInfo: extraInfo));
            var (apply, _) = await dialogTask;

            if (!apply)
                return false;
        }

        return true;
    }

    private List<(string Path, bool IsNew, string CurrentContent, string ProposedContent, string Summary)> BuildIngestReviewItems(IngestResult result)
    {
        var items = new List<(string Path, bool IsNew, string CurrentContent, string ProposedContent, string Summary)>();

        foreach (var p in result.NewPages.Where(p => !string.IsNullOrWhiteSpace(p.Path)))
        {
            var path = NormalizeWikiPath(p.Path);
            var current = ReadWikiFileContent(path);
            var summary = string.IsNullOrWhiteSpace(result.Summary)
                ? $"Create new page: {path}"
                : result.Summary;
            items.Add((path, true, current, p.Content ?? "", summary));
        }

        foreach (var u in result.UpdatedPages.Where(u => !string.IsNullOrWhiteSpace(u.Path)))
        {
            var path = NormalizeWikiPath(u.Path);
            var current = ReadWikiFileContent(path);
            var summary = string.IsNullOrWhiteSpace(result.Summary)
                ? $"Update existing page: {path}"
                : result.Summary;
            items.Add((path, false, current, u.Diff ?? "", summary));
        }

        return items
            .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .OrderBy(x => x.IsNew ? 0 : 1)
            .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string ReadWikiFileContent(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return "";

        var fullPath = Path.Combine(WikiRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
            return "";

        try
        {
            return File.ReadAllText(fullPath);
        }
        catch
        {
            return "";
        }
    }

    private static string NormalizeWikiPath(string path)
        => path.Replace('\\', '/').Trim();

    // Wiki 初期化
    [RelayCommand]
    private async Task InitializeWiki()
    {
        if (SelectedProject == null) return;
        var contextPath = GetContextPath(SelectedProject);
        if (contextPath == null) { StatusText = "Could not find context path for project."; return; }

        if (string.IsNullOrWhiteSpace(NewWikiDomain)) { StatusText = "Domain name is required."; return; }

        IsLoading = true;
        try
        {
            var name   = SelectedProject.Name;
            var domain = NewWikiDomain.Trim();
            await _wikiService.InitializeWiki(contextPath, name, domain);

            var domainList = WikiService.GetDomains(contextPath);
            Domains.Clear();
            foreach (var d in domainList) Domains.Add(d);
            
            SelectedDomain = domain;
            NewWikiDomain = "";
            IsCreatingNewDomain = false;
            StatusText = $"Wiki domain '{domain}' initialized.";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally { IsLoading = false; }
    }

    // ────────────────────────────────────────────────
    // Query タブ
    // ────────────────────────────────────────────────

    [RelayCommand]
    private async Task RunQuery()
    {
        if (!HasWiki || string.IsNullOrWhiteSpace(QueryText) || !IsAiEnabled) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        IsQuerying = true;
        try
        {
            var record = await _queryService.Query(WikiRoot, QueryText, _cts.Token);
            QueryText = "";
            ConversationLog.Add(record);
            if (!_currentSessionHistoryAdded)
            {
                QueryHistory.Insert(0, record); // SessionFilePath = null → 現セッションのエントリー
                _currentSessionHistoryAdded = true;
            }
            SelectedQueryRecord = null;
            SessionPreviewRecords.Clear();
            QueryReferencedPages.Clear();
            foreach (var p in record.ReferencedPages)
                QueryReferencedPages.Add(p);
            HasQueryAnswer = true;
            LastQueryRecord = record;
            _trayService.ShowBalloonTip("Wiki Query", "Answer is ready.");
        }
        catch (Exception ex)
        {
            // エラーは一時的なレコードとして会話ログに追加
            ConversationLog.Add(new WikiQueryRecord
            {
                AskedAt = DateTime.Now,
                Question = QueryText,
                Answer = $"Error: {ex.Message}",
                ReferencedPages = []
            });
            HasQueryAnswer = true;
        }
        finally { IsQuerying = false; }
    }

    private const int HistoryBatchSize = 15;

    private async Task LoadInitialHistoryAsync()
    {
        _pastSessionFiles = WikiQueryService.GetPastSessionFiles(WikiRoot, _currentSessionId);
        _nextSessionIndex = 0;
        await LoadHistoryBatchAsync();
    }

    /// <summary>セッションファイルの先頭レコードをHistoryに1エントリーとして追加する。</summary>
    private async Task LoadSessionSummaryAsync(string filePath)
    {
        var records = await WikiQueryService.LoadSessionFileAsync(filePath);
        if (records.Count == 0) return;
        var first = records[0];
        first.SessionFilePath = filePath; // 過去セッション識別用
        QueryHistory.Add(first);
    }

    [RelayCommand]
    private void ResetConversation()
    {
        _currentSessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        _queryService.StartNewSession(WikiRoot, _currentSessionId);
        _currentSessionHistoryAdded = false;
        ConversationLog.Clear();
        SessionPreviewRecords.Clear();
        QueryReferencedPages.Clear();
        HasQueryAnswer = false;
        LastQueryRecord = null;
        SelectedQueryRecord = null;
    }

    [RelayCommand]
    private async Task LoadMoreHistory()
    {
        if (_nextSessionIndex >= _pastSessionFiles.Count) return;
        await LoadHistoryBatchAsync();
    }

    private async Task LoadHistoryBatchAsync()
    {
        int end = Math.Min(_nextSessionIndex + HistoryBatchSize, _pastSessionFiles.Count);
        for (int i = _nextSessionIndex; i < end; i++)
            await LoadSessionSummaryAsync(_pastSessionFiles[i]);
        _nextSessionIndex = end;
        HasMoreHistory = _nextSessionIndex < _pastSessionFiles.Count;
    }

    partial void OnSelectedQueryRecordChanged(WikiQueryRecord? value)
    {
        if (value == null) { SessionPreviewRecords.Clear(); return; }

        if (value.SessionFilePath == null)
        {
            // 現セッションのエントリーをクリック → ConversationLogを表示
            SessionPreviewRecords.Clear();
            return;
        }

        // 過去セッション → 全レコードを非同期ロード
        QueryReferencedPages.Clear();
        foreach (var p in value.ReferencedPages)
            QueryReferencedPages.Add(p);
        HasQueryAnswer = true;
        LastQueryRecord = value;
        _ = LoadSessionPreviewAsync(value.SessionFilePath);
    }

    private async Task LoadSessionPreviewAsync(string filePath)
    {
        var records = await WikiQueryService.LoadSessionFileAsync(filePath);
        SessionPreviewRecords.Clear();
        foreach (var r in records)
            SessionPreviewRecords.Add(r);
    }

    [RelayCommand]
    private async Task DeleteQueryFromHistory(WikiQueryRecord? record)
    {
        if (record == null || string.IsNullOrEmpty(WikiRoot)) return;
        QueryHistory.Remove(record);
        await _queryService.DeleteRecordAsync(WikiRoot, record);
    }

    // ── Folder ────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void OpenFolder()
    {
        var path = Directory.Exists(WikiRoot) ? WikiRoot : SelectedProject?.Path;
        if (path == null || !Directory.Exists(path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    // ── Terminal ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void OpenTerminal()
    {
        var path = Directory.Exists(WikiRoot) ? WikiRoot : SelectedProject?.Path;
        if (path == null || !Directory.Exists(path)) return;
        OpenTerminalAtPath(path);
    }

    public void OpenAgentTerminal(string agent)
    {
        var path = Directory.Exists(WikiRoot) ? WikiRoot : SelectedProject?.Path;
        if (path == null || !Directory.Exists(path)) return;
        OpenAgentAtPath(path, agent);
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
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
            return true;
        }
        catch { return false; }
    }

    [RelayCommand]
    private async Task SaveAnswerAsPage()
    {
        if (LastQueryRecord == null || !HasWiki) return;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        try
        {
            var path = await _queryService.SaveAnswerAsPage(WikiRoot, LastQueryRecord, _cts.Token);
            StatusText = $"Saved: {path}";
            await LoadPagesAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Error saving: {ex.Message}";
        }
    }

    // ────────────────────────────────────────────────
    // Lint タブ
    // ────────────────────────────────────────────────

    [RelayCommand]
    private async Task RunLint()
    {
        if (!HasWiki) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        IsLinting = true;
        LintIssues.Clear();
        StatusText = "Running lint...";
        try
        {
            var progress = new Progress<string>(msg => StatusText = msg);
            var result = await _lintService.RunLint(WikiRoot, IsAiEnabled, progress, _cts.Token);

            LintLastRun = result.RunAt.ToString("yyyy-MM-dd HH:mm");
            LintHighCount   = result.Issues.Count(i => i.Severity == WikiLintSeverity.High);
            LintMediumCount = result.Issues.Count(i => i.Severity == WikiLintSeverity.Medium);
            LintLowCount    = result.Issues.Count(i => i.Severity == WikiLintSeverity.Low);

            foreach (var issue in result.Issues)
            {
                LintIssues.Add(new WikiLintIssueViewModel
                {
                    Category    = issue.Category,
                    Severity    = issue.Severity.ToString(),
                    Description = issue.Description,
                    PagePath    = issue.PagePath
                });
            }

            StatusText = result.IsEmpty ? "Lint complete. No issues found." : $"Lint complete. {result.Issues.Count} issues found.";
        }
        catch (Exception ex)
        {
            StatusText = $"Lint error: {ex.Message}";
        }
        finally { IsLinting = false; }
    }

    // ────────────────────────────────────────────────
    // Prompt Settings タブ
    // ────────────────────────────────────────────────

    private void LoadPromptSettings()
    {
        if (!HasWiki) return;
        var cfg = _wikiService.LoadPrompts(WikiRoot);
        IsPromptReadOnly = cfg.IsUnknownVersion;
        PromptImportSystemPrefix = cfg.Import.SystemPrefix;
        PromptImportSystemSuffix = cfg.Import.SystemSuffix;
        PromptImportUserSuffix   = cfg.Import.UserSuffix;
        PromptQuerySystemPrefix  = cfg.Query.SystemPrefix;
        PromptQuerySystemSuffix  = cfg.Query.SystemSuffix;
        PromptQueryUserSuffix    = cfg.Query.UserSuffix;
        PromptLintSystemPrefix   = cfg.Lint.SystemPrefix;
        PromptLintSystemSuffix   = cfg.Lint.SystemSuffix;
        PromptLintUserSuffix     = cfg.Lint.UserSuffix;
        PromptStatusText = cfg.IsUnknownVersion
            ? "Unsupported .wiki-prompts.json version. Prompts are read-only."
            : "";

        var language = _config.LoadSettings().LlmLanguage;
        DefaultImportSystemPrompt = WikiIngestService.GetDefaultSystemPromptPreview(language);
        DefaultQuerySystemPrompt  = WikiQueryService.GetDefaultSystemPromptPreview(language);
        DefaultLintSystemPrompt   = WikiLintService.GetDefaultSystemPromptPreview(language);
    }

    [RelayCommand]
    private async Task SavePromptSettings()
    {
        if (!HasWiki) return;
        if (IsPromptReadOnly) { PromptStatusText = "Cannot save: unsupported version."; return; }

        const int MaxLength = 8000;
        var overLimit = new List<string>();
        if (PromptImportSystemPrefix.Length > MaxLength) overLimit.Add($"Import.SystemPrefix ({PromptImportSystemPrefix.Length} chars)");
        if (PromptImportSystemSuffix.Length > MaxLength) overLimit.Add($"Import.SystemSuffix ({PromptImportSystemSuffix.Length} chars)");
        if (PromptImportUserSuffix.Length   > MaxLength) overLimit.Add($"Import.UserSuffix ({PromptImportUserSuffix.Length} chars)");
        if (PromptQuerySystemPrefix.Length  > MaxLength) overLimit.Add($"Query.SystemPrefix ({PromptQuerySystemPrefix.Length} chars)");
        if (PromptQuerySystemSuffix.Length  > MaxLength) overLimit.Add($"Query.SystemSuffix ({PromptQuerySystemSuffix.Length} chars)");
        if (PromptQueryUserSuffix.Length    > MaxLength) overLimit.Add($"Query.UserSuffix ({PromptQueryUserSuffix.Length} chars)");
        if (PromptLintSystemPrefix.Length   > MaxLength) overLimit.Add($"Lint.SystemPrefix ({PromptLintSystemPrefix.Length} chars)");
        if (PromptLintSystemSuffix.Length   > MaxLength) overLimit.Add($"Lint.SystemSuffix ({PromptLintSystemSuffix.Length} chars)");
        if (PromptLintUserSuffix.Length     > MaxLength) overLimit.Add($"Lint.UserSuffix ({PromptLintUserSuffix.Length} chars)");
        if (overLimit.Count > 0) { PromptStatusText = $"Save rejected: fields exceed 8,000 char limit: {string.Join(", ", overLimit)}"; return; }

        var cfg = new WikiPromptConfig
        {
            Import = new WikiPromptOverrides { SystemPrefix = PromptImportSystemPrefix, SystemSuffix = PromptImportSystemSuffix, UserSuffix = PromptImportUserSuffix },
            Query  = new WikiPromptOverrides { SystemPrefix = PromptQuerySystemPrefix,  SystemSuffix = PromptQuerySystemSuffix,  UserSuffix = PromptQueryUserSuffix },
            Lint   = new WikiPromptOverrides { SystemPrefix = PromptLintSystemPrefix,   SystemSuffix = PromptLintSystemSuffix,   UserSuffix = PromptLintUserSuffix },
        };

        try
        {
            await _wikiService.SavePromptsAtomicAsync(WikiRoot, cfg);
            PromptStatusText = "Prompt settings saved.";
        }
        catch (Exception ex) { PromptStatusText = $"Save failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void ResetPromptSettings()
    {
        PromptImportSystemPrefix = PromptImportSystemSuffix = PromptImportUserSuffix = "";
        PromptQuerySystemPrefix  = PromptQuerySystemSuffix  = PromptQueryUserSuffix  = "";
        PromptLintSystemPrefix   = PromptLintSystemSuffix   = PromptLintUserSuffix   = "";
        PromptStatusText = "Reset to defaults (not saved). Click Save to apply.";
    }

    // ── Wiki Schema ───────────────────────────────────

    partial void OnWikiSchemaContentChanged(string value)
    {
        if (!_suppressWikiSchemaDirtyTracking)
            IsWikiSchemaDirty = true;
    }

    private async Task LoadWikiSchemaAsync()
    {
        if (!HasWiki) return;
        var path = WikiService.GetSchemaPath(WikiRoot);
        _suppressWikiSchemaDirtyTracking = true;
        try
        {
            WikiSchemaContent = File.Exists(path)
                ? await File.ReadAllTextAsync(path, System.Text.Encoding.UTF8)
                : "";
            IsWikiSchemaDirty = false;
            WikiSchemaStatusText = "";
        }
        catch (Exception ex) { WikiSchemaStatusText = $"Load failed: {ex.Message}"; }
        finally { _suppressWikiSchemaDirtyTracking = false; }
    }

    [RelayCommand]
    private async Task SaveWikiSchema()
    {
        if (!HasWiki) return;
        try
        {
            await WikiService.WriteFileAtomicAsync(WikiService.GetSchemaPath(WikiRoot), WikiSchemaContent);
            IsWikiSchemaDirty = false;
            WikiSchemaStatusText = "Saved.";
        }
        catch (Exception ex) { WikiSchemaStatusText = $"Save failed: {ex.Message}"; }
    }

    // ────────────────────────────────────────────────
    // カテゴリ管理
    // ────────────────────────────────────────────────

    [RelayCommand]
    private async Task AddCategory()
    {
        if (!HasWiki || string.IsNullOrWhiteSpace(NewCategoryName)) return;
        if (IsWikiReadOnly) { StatusText = $"Cannot modify categories: {WikiReadOnlyReason}"; return; }

        var (success, error) = await _wikiService.AddCategoryAsync(WikiRoot, NewCategoryName.Trim());
        if (success)
        {
            NewCategoryName = "";
            await LoadCategoriesAsync();
            StatusText = "Category added.";
        }
        else { StatusText = $"Cannot add category: {error}"; }
    }

    [RelayCommand]
    private async Task DeleteCategory(string? name)
    {
        if (!HasWiki || string.IsNullOrWhiteSpace(name)) return;
        if (IsWikiReadOnly) { StatusText = $"Cannot modify categories: {WikiReadOnlyReason}"; return; }

        var (success, error) = await _wikiService.DeleteCategoryAsync(WikiRoot, name);
        if (success) { await LoadCategoriesAsync(); StatusText = $"Category '{name}' removed from config."; }
        else         { StatusText = $"Cannot remove category: {error}"; }
    }

    // ────────────────────────────────────────────────
    // ヘルパー
    // ────────────────────────────────────────────────

    private static string? GetContextPath(ProjectInfo project)
    {
        // AiContextContentPath を最初に確認 (context/ junction)
        if (!string.IsNullOrEmpty(project.AiContextContentPath) && Directory.Exists(project.AiContextContentPath))
            return project.AiContextContentPath;

        // AiContextPath/_context フォールバック
        if (!string.IsNullOrEmpty(project.AiContextPath))
        {
            var candidate = Path.Combine(project.AiContextPath, "context");
            if (Directory.Exists(candidate)) return candidate;
        }

        // プロジェクトパスから推測
        if (!string.IsNullOrEmpty(project.Path))
        {
            var candidate = Path.Combine(project.Path, "_ai-context", "context");
            if (Directory.Exists(candidate)) return candidate;
        }

        return null;
    }
}
