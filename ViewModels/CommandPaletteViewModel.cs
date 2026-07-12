using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Curia.Models;
using Curia.Services;
using Curia.Views.Pages;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Curia.ViewModels;

public partial class CommandPaletteViewModel : ObservableObject
{
    private readonly ConfigService _configService;
    private readonly ProjectDiscoveryService _discoveryService;
    private readonly SetupViewModel _setupViewModel;
    private readonly EditorViewModel _editorViewModel;
    private readonly TimelineViewModel _timelineViewModel;
    private readonly StandupGeneratorService _standupGeneratorService;
    private readonly IContentDialogService _contentDialogService;
    private readonly CuriaQueryService _curiaQueryService;

    [ObservableProperty]
    private string searchText = "";

    [ObservableProperty]
    private ObservableCollection<CommandItem> filteredCommands = [];

    [ObservableProperty]
    private CommandItem? selectedCommand;

    [ObservableProperty]
    private bool isAskMode;

    [ObservableProperty]
    private bool isAskLoading;

    [ObservableProperty]
    private CuriaAnswer? lastAnswer;

    [ObservableProperty]
    private bool isAiEnabled;

    public ObservableCollection<CuriaConversationTurn> ConversationTurns { get; } = [];

    private readonly List<(string role, string content)> _llmHistory = [];
    private CancellationTokenSource? _askCts;

    /// <summary>引用クリック時にエディタで開く。設定しない場合はクリップボードコピーにフォールバック。</summary>
    public Action<ProjectInfo, string>? OnOpenInEditor { get; set; }
    public Action<string>? OnAskAgent { get; set; }

    private List<CommandItem> _allCommands = [];
    private DateTime _commandsBuiltTime = DateTime.MinValue;
    private const int RebuildTtlSeconds = 290; // discovery cache TTL より少し短く

    public CommandPaletteViewModel(
        ConfigService configService,
        ProjectDiscoveryService discoveryService,
        SetupViewModel setupViewModel,
        EditorViewModel editorViewModel,
        TimelineViewModel timelineViewModel,
        StandupGeneratorService standupGeneratorService,
        IContentDialogService contentDialogService,
        CuriaQueryService curiaQueryService)
    {
        _configService = configService;
        _discoveryService = discoveryService;
        _setupViewModel = setupViewModel;
        _editorViewModel = editorViewModel;
        _timelineViewModel = timelineViewModel;
        _standupGeneratorService = standupGeneratorService;
        _contentDialogService = contentDialogService;
        _curiaQueryService = curiaQueryService;

        IsAiEnabled = _configService.LoadSettings().AiEnabled;
        WeakReferenceMessenger.Default.Register<AiEnabledChangedMessage>(this,
            (_, msg) => IsAiEnabled = msg.Enabled);
    }

    /// <summary>
    /// アプリ起動時にバックグラウンドでコマンド一覧を構築しておく。
    /// パレット初回起動を即時にするためのウォームアップ用。
    /// </summary>
    public async Task PreBuildAsync()
    {
        if ((DateTime.Now - _commandsBuiltTime).TotalSeconds < RebuildTtlSeconds)
            return;

        _allCommands = BuildStaticCommands();
        await RebuildWithProjectsAsync();
    }

    public void Prepare()
    {
        bool isFresh = (DateTime.Now - _commandsBuiltTime).TotalSeconds < RebuildTtlSeconds;

        // 既存の会話がある場合はそのまま Ask モードで再開
        if (ConversationTurns.Count > 0)
        {
            SearchText = "?";
            IsAskMode = true;
            return;
        }

        if (isFresh)
        {
            SearchText = "";
            UpdateFilter();
            return;
        }

        _allCommands = BuildStaticCommands();
        SearchText = "";
        UpdateFilter();

        _ = RebuildWithProjectsAsync();
    }

    public void ResetConversation()
    {
        _askCts?.Cancel();
        ConversationTurns.Clear();
        _llmHistory.Clear();
        LastAnswer = null;
        IsAskMode = false;
        IsAskLoading = false;
    }

    /// <summary>
    /// 選択中のコマンドを実行する。Window 側から呼ぶ。
    /// </summary>
    public void ExecuteSelected(Action<CommandItem> executor)
    {
        if (SelectedCommand != null)
            executor(SelectedCommand);
    }

    partial void OnSearchTextChanged(string value)
    {
        if (!value.StartsWith("?") && IsAskMode)
        {
            // Ask モードを抜けたらロード中のリクエストをキャンセル (会話履歴は保持)
            _askCts?.Cancel();
            IsAskMode = false;
            IsAskLoading = false;
        }

        if (value.StartsWith("?"))
        {
            // Ask Curia UI is replaced by the Agent Chat handoff in CommandPaletteWindow.
            IsAskMode = false;
            FilteredCommands.Clear();
            SelectedCommand = null;
            return;
        }

        IsAskMode = false;
        UpdateFilter();
    }

    public async Task AskAsync(string rawQuestion)
    {
        var question = rawQuestion.TrimStart('?').Trim();
        if (string.IsNullOrWhiteSpace(question)) return;

        if (OnAskAgent != null)
        {
            OnAskAgent(question);
            return;
        }

        _askCts?.Cancel();
        _askCts = new CancellationTokenSource();
        var ct = _askCts.Token;

        IsAskLoading = true;

        try
        {
            // 会話履歴を渡してマルチターン対応
            var history = _llmHistory.Count > 0
                ? (IReadOnlyList<(string, string)>)_llmHistory.AsReadOnly()
                : null;

            var answer = await _curiaQueryService.AskAsync(question, null, history, ct);

            // 会話ターンとして追加
            ConversationTurns.Add(new CuriaConversationTurn
            {
                Question = answer.Question,
                AnswerText = answer.AnswerText,
                Citations = answer.Citations,
            });

            // LLM コンテキスト更新 (ドキュメント無しのQ&Aのみ保持してサイズ抑制)
            _llmHistory.Add(("user", question));
            _llmHistory.Add(("assistant", answer.AnswerText));

            LastAnswer = answer;

            // 次の質問入力を促すため検索ボックスを "?" にリセット
            SearchText = "?";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var errTurn = new CuriaConversationTurn
            {
                Question = question,
                AnswerText = $"Error: {ex.Message}",
            };
            ConversationTurns.Add(errTurn);
            LastAnswer = new CuriaAnswer
            {
                Question = question,
                AnswerText = errTurn.AnswerText,
                GeneratedAt = DateTime.Now,
            };
        }
        finally
        {
            IsAskLoading = false;
        }
    }

    public void CancelAsk()
    {
        _askCts?.Cancel();
        IsAskLoading = false;
    }

    public async Task OpenCitationAsync(CuriaCitation citation)
    {
        var hashIdx = citation.Path.LastIndexOf('#');
        var filePath = hashIdx >= 0 ? citation.Path[..hashIdx] : citation.Path;

        if (!File.Exists(filePath))
        {
            try { System.Windows.Clipboard.SetText(citation.Path); } catch { }
            return;
        }

        if (OnOpenInEditor == null)
        {
            try { System.Windows.Clipboard.SetText(filePath); } catch { }
            return;
        }

        var projects = await _discoveryService.GetProjectInfoListAsync();
        var project = projects
            .Where(p => !string.IsNullOrEmpty(p.Path))
            .OrderByDescending(p => p.Path.Length)
            .FirstOrDefault(p =>
                filePath.StartsWith(p.Path, StringComparison.OrdinalIgnoreCase) ||
                filePath.StartsWith(p.AiContextPath, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(p.AiContextContentPath) &&
                 filePath.StartsWith(p.AiContextContentPath, StringComparison.OrdinalIgnoreCase)));

        if (project != null)
            OnOpenInEditor(project, filePath);
        else
            try { System.Windows.Clipboard.SetText(filePath); } catch { }
    }

    /// <summary>
    /// タブコマンドのみ構築 (I/O なし・即時)。
    /// </summary>
    private List<CommandItem> BuildStaticCommands()
    {
        var commands = new List<CommandItem>();
        AddTabCommand(commands, "Dashboard", typeof(DashboardPage));
        AddTabCommand(commands, "Editor", typeof(EditorPage));
        AddTabCommand(commands, "Timeline", typeof(TimelinePage));
        AddTabCommand(commands, "Wiki", typeof(WikiPage));
        AddTabCommand(commands, "Git Repos", typeof(GitReposPage));
        AddTabCommand(commands, "Asana Sync", typeof(AsanaSyncPage));
        AddTabCommand(commands, "Agent Hub", typeof(AgentHubPage));
        AddTabCommand(commands, "Setup", typeof(SetupPage));
        AddTabCommand(commands, "Settings", typeof(SettingsPage));

        commands.Add(new CommandItem
        {
            Label    = "Add Task",
            Category = "task",
            Display  = "[+]  Add Task",
            Action   = (w) => w.ShowAddTaskDialog()
        });

        return commands;
    }

    /// <summary>
    /// プロジェクト一覧を非同期取得してフルコマンドセットを再構築する。
    /// 完了後、UIスレッドでフィルタを更新する。
    /// </summary>
    private async Task RebuildWithProjectsAsync()
    {
        var projects = await _discoveryService.GetProjectInfoListAsync();
        var commands = BuildStaticCommands();

        // --- Project commands ---
        foreach (var proj in projects)
        {
            string displayName = proj.DisplayName;
            string localName = proj.Name;
            string localPath = proj.Path;

            // > check ProjectName
            commands.Add(new CommandItem
            {
                Label = $"check {localName}",
                Category = "project",
                Display = $"[>]  check {displayName}",
                Action = (w) => {
                    _setupViewModel.SelectedTabIndex = 1; // Check tab
                    _setupViewModel.CheckProjectName = proj.Name;
                    w.RootNavigation.Navigate(typeof(SetupPage));
                }
            });

            // > edit ProjectName
            commands.Add(new CommandItem
            {
                Label = $"edit {localName}",
                Category = "project",
                Display = $"[>]  edit {displayName}",
                Action = (w) => {
                    _editorViewModel.SelectedProject = _editorViewModel.Projects.FirstOrDefault(p => p.Name == proj.Name && p.Tier == proj.Tier && p.Category == proj.Category);
                    w.RootNavigation.Navigate(typeof(EditorPage));
                }
            });

            // > term ProjectName
            commands.Add(new CommandItem
            {
                Label = $"term {localName}",
                Category = "project",
                Display = $"[>]  term {displayName}",
                Action = (w) => OpenTerminalAtPath(localPath)
            });

            // > dir ProjectName (root)
            commands.Add(new CommandItem
            {
                Label = $"dir {localName}",
                Category = "dir",
                Display = $"[dir]  {displayName}",
                Action = (_) => { if (Directory.Exists(localPath)) OpenExplorer(localPath); }
            });

            // dir <subfolder> ProjectName
            var subFolders = new (string Tag, string Path)[]
            {
                ("docs",    System.IO.Path.Combine(localPath, "shared", "docs")),
                ("work",    System.IO.Path.Combine(localPath, "shared", "_work")),
                ("develop", System.IO.Path.Combine(localPath, "development", "source")),
                ("shared",  System.IO.Path.Combine(localPath, "shared")),
                ("ai",      System.IO.Path.Combine(localPath, "_ai-context")),
            };
            foreach (var (tag, folderPath) in subFolders)
            {
                if (!Directory.Exists(folderPath)) continue;
                var capturedPath = folderPath;
                commands.Add(new CommandItem
                {
                    Label = $"dir {tag} {localName}",
                    Category = "dir",
                    Display = $"[dir]  {tag} / {displayName}",
                    Action = (_) => OpenExplorer(capturedPath)
                });
            }

            // @ ProjectName (editor shortcut)
            commands.Add(new CommandItem
            {
                Label = localName,
                Category = "editor",
                Display = $"[@]  {displayName}",
                Action = (w) => {
                    _editorViewModel.SelectedProject = _editorViewModel.Projects.FirstOrDefault(p => p.Name == proj.Name && p.Tier == proj.Tier && p.Category == proj.Category);
                    w.RootNavigation.Navigate(typeof(EditorPage));
                }
            });

            // > timeline ProjectName
            commands.Add(new CommandItem
            {
                Label = $"timeline {localName}",
                Category = "project",
                Display = $"[>]  timeline {displayName}",
                Action = (w) => {
                    _timelineViewModel.SelectedProject = _timelineViewModel.Projects.FirstOrDefault(p => p.Name == proj.Name && p.Tier == proj.Tier && p.Category == proj.Category);
                    w.RootNavigation.Navigate(typeof(TimelinePage));
                }
            });

            // > resume ProjectName
            commands.Add(new CommandItem
            {
                Label = $"resume {localName}",
                Category = "project",
                Display = $"[>]  resume {displayName}",
                Action = async (w) => {
                    var feature = await ShowResumeDialogAsync(localName);
                    if (!string.IsNullOrWhiteSpace(feature)) {
                        InvokeResumeWork(localPath, feature);
                    }
                }
            });

            // + add task ProjectName
            commands.Add(new CommandItem
            {
                Label    = $"add task {localName}",
                Category = "task",
                Display  = $"[+]  add task {displayName}",
                Action   = (w) => w.ShowAddTaskDialog(localName)
            });
        }

        _allCommands = commands;
        _commandsBuiltTime = DateTime.Now;

        // パレットが開いている場合にリストを更新する
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(UpdateFilter);
    }

    private void AddTabCommand(List<CommandItem> commands, string label, Type pageType)
    {
        commands.Add(new CommandItem
        {
            Label = label,
            Category = "tab",
            Display = $"[Tab]  {label}",
            Action = (w) =>
            {
                w.RootNavigation.Navigate(pageType);
                w.BringToFront();
            }
        });
    }

    private void UpdateFilter()
    {
        string rawText = SearchText.Trim();
        string? categoryFilter = null;
        string searchText = rawText.ToLower();

        if (rawText.StartsWith(">"))
        {
            categoryFilter = "project";
            searchText = rawText.Substring(1).Trim().ToLower();
        }
        else if (rawText.StartsWith("@"))
        {
            categoryFilter = "editor";
            searchText = rawText.Substring(1).Trim().ToLower();
        }
        else if (rawText.StartsWith("/"))
        {
            categoryFilter = "dir";
            searchText = rawText.Substring(1).Trim().ToLower();
        }
        else if (rawText.StartsWith("+"))
        {
            categoryFilter = "task";
            searchText = rawText.Substring(1).Trim().ToLower();
        }

        IEnumerable<CommandItem> filtered;
        if (string.IsNullOrEmpty(rawText))
        {
            filtered = _allCommands;
        }
        else if (categoryFilter != null && string.IsNullOrEmpty(searchText))
        {
            filtered = _allCommands.Where(c => c.Category == categoryFilter);
        }
        else
        {
            var tokens = searchText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            filtered = _allCommands.Where(c => {
                if (categoryFilter != null && c.Category != categoryFilter) return false;
                string label = c.Label.ToLower();
                return tokens.All(t => label.Contains(t));
            });

            // Sort: exact match > starts-with > contains
            filtered = filtered.OrderBy(c => {
                string label = c.Label.ToLower();
                if (label == searchText) return 0;
                if (label.StartsWith(searchText)) return 1;
                return 2;
            });
        }

        FilteredCommands.Clear();
        foreach (var cmd in filtered) FilteredCommands.Add(cmd);

        if (FilteredCommands.Count > 0)
        {
            SelectedCommand = FilteredCommands[0];
        }
        else
        {
            SelectedCommand = null;
        }
    }


    private static void OpenExplorer(string folderPath)
        => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folderPath}\"") { UseShellExecute = true });

    private void OpenTerminalAtPath(string path)
    {
        if (!Directory.Exists(path)) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "wt.exe",
                Arguments = $"-d \"{path}\"",
                UseShellExecute = true
            });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "pwsh.exe",
                    Arguments = $"-NoExit -Command \"Set-Location '{path}'\"",
                    UseShellExecute = true
                });
            }
            catch
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoExit -Command \"Set-Location '{path}'\"",
                        UseShellExecute = true
                    });
                }
                catch { }
            }
        }
    }

    private async Task<string?> ShowResumeDialogAsync(string projectName)
    {
        var inputControl = new Wpf.Ui.Controls.TextBox
        {
            PlaceholderText = "Feature name (e.g. fix-bug-123)",
            Margin = new Thickness(0, 8, 0, 0)
        };

        var dialog = new Wpf.Ui.Controls.ContentDialog
        {
            Title = $"Resume {projectName}",
            Content = new StackPanel
            {
                Children = {
                    new TextBlock { Text = "Enter feature name to create a new work directory:", Foreground = (System.Windows.Media.Brush)Application.Current.Resources["AppText"] },
                    inputControl
                }
            },
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = Wpf.Ui.Controls.ContentDialogButton.Primary
        };

        var result = await _contentDialogService.ShowAsync(dialog, default);
        if (result == Wpf.Ui.Controls.ContentDialogResult.Primary)
        {
            return inputControl.Text.Trim();
        }
        return null;
    }

    private void InvokeResumeWork(string projPath, string featureName)
    {
        string safeFeature = Regex.Replace(featureName.Trim(), @"[\\/:*?""<>|]", "_");
        DateTime now = DateTime.Now;
        string yearStr = now.ToString("yyyy");
        string monthStr = now.ToString("yyyyMM");
        string dayStr = now.ToString("yyyyMMdd");

        string workDir = Path.Combine(projPath, "shared", "_work", yearStr, monthStr, $"{dayStr}_{safeFeature}");
        if (!Directory.Exists(workDir))
        {
            Directory.CreateDirectory(workDir);
        }

        OpenExplorer(workDir);
        OpenTerminalAtPath(workDir);
    }
}
