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

    [ObservableProperty]
    private string searchText = "";

    [ObservableProperty]
    private ObservableCollection<CommandItem> filteredCommands = [];

    [ObservableProperty]
    private CommandItem? selectedCommand;

    // CommandPaletteWindow の移行中レイアウトが参照する空の表示状態。
    // 旧 Ask Curia の問い合わせ処理・履歴・引用操作は保持しない。
    public bool IsAskMode => false;
    public bool IsAskLoading => false;
    public ObservableCollection<CuriaConversationTurn> ConversationTurns { get; } = [];

    [ObservableProperty]
    private bool isAiEnabled;

    public Func<string, Task>? OnAskAgent { get; set; }

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
        IContentDialogService contentDialogService)
    {
        _configService = configService;
        _discoveryService = discoveryService;
        _setupViewModel = setupViewModel;
        _editorViewModel = editorViewModel;
        _timelineViewModel = timelineViewModel;
        _standupGeneratorService = standupGeneratorService;
        _contentDialogService = contentDialogService;

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
        if (value.StartsWith("?"))
        {
            FilteredCommands.Clear();
            SelectedCommand = null;
            return;
        }
        UpdateFilter();
    }

    public async Task AskAsync(string rawQuestion)
    {
        var question = rawQuestion.TrimStart('?').Trim();
        if (string.IsNullOrWhiteSpace(question)) return;
        if (OnAskAgent != null) await OnAskAgent(question);
    }

    public void ResetConversation() => ConversationTurns.Clear();

    public Task OpenCitationAsync(CuriaCitation citation) => Task.CompletedTask;

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
