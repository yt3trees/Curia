using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wpf.Ui.Controls;
using Curia.Models;
using Curia.Services;
using Curia.ViewModels;
using WpfUserControl = System.Windows.Controls.UserControl;

namespace Curia.Views.Pages;

public partial class DashboardPage : WpfUserControl, INavigableView<DashboardViewModel>
{
    public DashboardViewModel ViewModel { get; }

    private readonly LlmClientService _llmClientService;
    private readonly FileEncodingService _fileEncodingService;
    private readonly ConfigService _configService;
    private readonly DecisionLogService _decisionLogService;
    private readonly AsanaSyncService _asanaSyncService;
    private readonly TeamTaskParser _teamTaskParser;
    private readonly AsanaTaskParser _asanaTaskParser;
    private readonly CaptureService _captureService;
    private readonly ProjectDiscoveryService _discoveryService;
    private readonly TrayService _trayService;
    private readonly ScheduleService _scheduleService;

    public DashboardPage(
        DashboardViewModel viewModel,
        LlmClientService llmClientService,
        FileEncodingService fileEncodingService,
        ConfigService configService,
        DecisionLogService decisionLogService,
        AsanaSyncService asanaSyncService,
        TeamTaskParser teamTaskParser,
        AsanaTaskParser asanaTaskParser,
        CaptureService captureService,
        ProjectDiscoveryService discoveryService,
        TrayService trayService,
        ScheduleService scheduleService)
    {
        ViewModel = viewModel;
        _llmClientService = llmClientService;
        _fileEncodingService = fileEncodingService;
        _configService = configService;
        _decisionLogService = decisionLogService;
        _asanaSyncService = asanaSyncService;
        _teamTaskParser = teamTaskParser;
        _asanaTaskParser = asanaTaskParser;
        _captureService = captureService;
        _discoveryService = discoveryService;
        _trayService = trayService;
        _scheduleService = scheduleService;
        DataContext = ViewModel;

        ViewModel.OnOpenInEditor = project =>
        {
            if (Window.GetWindow(this) is MainWindow mw)
                mw.NavigateToEditor(project);
        };

        ViewModel.OnOpenInTimeline = project =>
        {
            if (Window.GetWindow(this) is MainWindow mw)
                mw.NavigateToTimeline(project);
        };

        InitializeComponent();
        InitAutoRefreshCombo();
    }

    private void InitAutoRefreshCombo()
    {
        foreach (ComboBoxItem item in AutoRefreshComboBox.Items)
        {
            if (item.Tag is string s && s == ViewModel.AutoRefreshMinutes.ToString())
            {
                AutoRefreshComboBox.SelectedItem = item;
                return;
            }
        }
        AutoRefreshComboBox.SelectedIndex = 0; // Off
    }

    private bool _isInitialized = false;
    private static DateTime? _planMyDayShownToday;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isInitialized) return;
        _isInitialized = true;
        await ViewModel.RefreshAsync();
        ViewModel.SetupAutoRefresh();

        // Morning autopilot (temporarily disabled — re-enable when dialog sizing is fixed)
        // if (ViewModel.IsAiEnabled && ShouldShowMorningAutopilot())
        // {
        //     _planMyDayShownToday = DateTime.Today;
        //     if (await ShowMorningConfirmDialogAsync())
        //         await RunPlanMyDayAsync();
        // }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
        => _ = ViewModel.RefreshAsync(force: true);

    private void OnAutoRefreshChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AutoRefreshComboBox.SelectedItem is ComboBoxItem item &&
            item.Tag is string s && int.TryParse(s, out int val))
        {
            ViewModel.AutoRefreshMinutes = val;
            ViewModel.SetupAutoRefresh();
        }
    }

    private void OnHideProject(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProjectCardViewModel card })
            ViewModel.HideProject(card);
    }

    private void OnUnhideProject(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProjectCardViewModel card })
            ViewModel.UnhideProject(card);
    }

    private void OnToggleShowHidden(object sender, RoutedEventArgs e)
        => ViewModel.ShowHidden = !ViewModel.ShowHidden;

    private void OnOpenDirClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProjectCardViewModel card })
            ViewModel.OpenDirectory(card);
    }

    private void OnDirMenuVSCode(object sender, RoutedEventArgs e)
    {
        if (GetCardFromMenuItem(sender) is { } card)
            ViewModel.OpenVSCode(card);
    }

    private void OnDirMenuWorkRoot(object sender, RoutedEventArgs e)
    {
        if (GetCardFromMenuItem(sender) is { } card)
            ViewModel.OpenWorkRoot(card);
    }

    private async void OnDirMenuCreateTodayGeneralWork(object sender, RoutedEventArgs e)
    {
        if (GetCardFromMenuItem(sender) is not { } card) return;
        var featureName = await ShowWorkFolderFeatureDialogAsync($"Create General Work Folder ({card.Info.Name})");
        if (string.IsNullOrWhiteSpace(featureName)) return;

        var folder = ViewModel.CreateTodayGeneralWorkFolder(card, featureName);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Dashboard] Failed to open folder: {ex}");
            }
        }
    }

    private void OnTermClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProjectCardViewModel card })
            ViewModel.OpenTerminal(card);
    }

    private void OnTermMenuAgent(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem mi) return;
        var agent = mi.Tag as string ?? "";
        if (GetCardFromMenuItem(sender) is { } card)
            ViewModel.OpenAgentTerminal(card, agent);
    }

    private static ProjectCardViewModel? GetCardFromMenuItem(object sender)
    {
        if (sender is System.Windows.Controls.MenuItem mi &&
            mi.Parent is System.Windows.Controls.ContextMenu cm &&
            cm.PlacementTarget is FrameworkElement fe &&
            fe.Tag is ProjectCardViewModel card)
            return card;
        return null;
    }

    private void OnOpenFocusClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProjectCardViewModel card }) return;
        if (string.IsNullOrWhiteSpace(card.FocusFile) || !File.Exists(card.FocusFile)) return;
        if (Window.GetWindow(this) is MainWindow mw)
            mw.NavigateToEditorAndOpenFile(card.Info, card.FocusFile);
    }

    private void OnOpenSummaryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProjectCardViewModel card }) return;
        if (string.IsNullOrWhiteSpace(card.SummaryFile) || !File.Exists(card.SummaryFile)) return;
        if (Window.GetWindow(this) is MainWindow mw)
            mw.NavigateToEditorAndOpenFile(card.Info, card.SummaryFile);
    }

    private void OnActivityBarClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProjectCardViewModel card })
            ViewModel.OpenInTimeline(card);
    }

    private async void OnUncommittedBadgeClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProjectCardViewModel card }) return;
        await ShowUncommittedDetailsDialogAsync(card);
    }

    private async void OnProjectDecisionLogClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProjectCardViewModel card }) return;
        var entries = await _decisionLogService.GetDecisionLogsAsync(card.Info.AiContextContentPath);
        DecisionLogViewerDialog.ShowDialog(Window.GetWindow(this), card.Info.Name, entries, file =>
        {
            if (Window.GetWindow(this) is MainWindow mw)
                mw.NavigateToEditorAndOpenFile(card.Info, file);
        });
    }

    private void OnTeamViewClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProjectCardViewModel card }) return;
        if (!card.HasTeamView) return;

        var asanaConfig = _configService.LoadAsanaProjectConfig(card.Info);
        var teamView = asanaConfig?.TeamView;
        if (teamView == null) return;

        var obsPath = _configService.GetObsidianProjectPath(card.Info);
        TeamViewDialog.ShowDialog(
            owner: Window.GetWindow(this),
            projectName: card.Info.Name,
            obsidianProjectPath: obsPath,
            teamView: teamView,
            asanaSyncService: _asanaSyncService,
            teamTaskParser: _teamTaskParser);
    }

    private void OnWorkstreamTeamViewClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WorkstreamCardItem ws }) return;
        if (!ws.HasTeamView || ws.WorkstreamTeamView == null) return;

        // 親 ProjectCardViewModel を VisualTree から取得
        var card = FindVisualParent<ItemsControl>(sender as DependencyObject)?
            .DataContext as ProjectCardViewModel
            ?? (FindVisualParent<Border>(sender as DependencyObject)?
                .DataContext as ProjectCardViewModel);
        if (card == null) return;

        var obsPath = _configService.GetObsidianProjectPath(card.Info);
        TeamViewDialog.ShowDialog(
            owner: Window.GetWindow(this),
            projectName: $"{card.Info.Name} / {ws.Label}",
            obsidianProjectPath: obsPath,
            teamView: ws.WorkstreamTeamView,
            asanaSyncService: _asanaSyncService,
            teamTaskParser: _teamTaskParser);
    }

    private async void OnWorkstreamDecisionLogClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not WorkstreamCardItem ws) return;
        
        var itemsControl = FindVisualParent<ItemsControl>(fe);
        if (itemsControl?.DataContext is not ProjectCardViewModel card) return;

        var entries = await _decisionLogService.GetDecisionLogsAsync(card.Info.AiContextContentPath, ws.Id);
        DecisionLogViewerDialog.ShowDialog(Window.GetWindow(this), $"{card.Info.Name} / {ws.Label}", entries, file =>
        {
            if (Window.GetWindow(this) is MainWindow mw)
                mw.NavigateToEditorAndOpenFile(card.Info, file);
        });
    }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject parentObject = VisualTreeHelper.GetParent(child);
        if (parentObject == null) return null;
        if (parentObject is T parent) return parent;
        return FindVisualParent<T>(parentObject);
    }

    private sealed class RepoStatusDialogItem
    {
        public string RelativePath { get; init; } = ".";
        public string FullPath { get; init; } = "";
        public int TotalCount { get; init; }
        public int StagedCount { get; init; }
        public int ModifiedCount { get; init; }
        public int UntrackedCount { get; init; }
        public int ConflictCount { get; init; }
        public string Details { get; init; } = "";

        public string SummaryText
        {
            get
            {
                var parts = new List<string> { $"{TotalCount} changes" };
                if (StagedCount > 0) parts.Add($"staged {StagedCount}");
                if (ModifiedCount > 0) parts.Add($"modified {ModifiedCount}");
                if (UntrackedCount > 0) parts.Add($"untracked {UntrackedCount}");
                if (ConflictCount > 0) parts.Add($"conflicts {ConflictCount}");
                return string.Join(" | ", parts);
            }
        }

        public string DisplayLabel => $"{RelativePath}  ({SummaryText})";
    }

    private async void OnShowCaptureLogClick(object sender, RoutedEventArgs e)
        => await ShowCaptureLogDialogAsync();

    private async Task ShowCaptureLogDialogAsync()
    {
        var logPath = Path.Combine(_configService.ConfigDir, "capture_log.md");

        List<CaptureLogEntry> entries;
        if (!File.Exists(logPath))
        {
            entries = [];
        }
        else
        {
            var (content, _) = await _fileEncodingService.ReadFileAsync(logPath);
            entries = ParseCaptureLog(content);
        }

        var appResources = Application.Current.Resources;
        var surface  = (System.Windows.Media.Brush)appResources["AppSurface0"];
        var surface1 = (System.Windows.Media.Brush)appResources["AppSurface1"];
        var surface2 = (System.Windows.Media.Brush)appResources["AppSurface2"];
        var text     = (System.Windows.Media.Brush)appResources["AppText"];
        var subtext  = (System.Windows.Media.Brush)appResources["AppSubtext0"];
        var accent   = appResources.Contains("AppPeach")
            ? (System.Windows.Media.Brush)appResources["AppPeach"]
            : text;

        // タイトルバー
        var titleBar = new Grid { Background = surface1, Height = 38 };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleIcon = new System.Windows.Controls.TextBlock
        {
            Text = "●",
            Foreground = accent,
            FontSize = 11,
            Margin = new Thickness(12, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleIcon, 0);

        var titleText = new System.Windows.Controls.TextBlock
        {
            Text = "Capture Log",
            Foreground = text,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleText, 1);

        var closeBtn = new System.Windows.Controls.Button
        {
            Content = "✕",
            Width = 34,
            Height = 26,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = subtext,
            FontSize = 13
        };
        Grid.SetColumn(closeBtn, 2);
        titleBar.Children.Add(titleIcon);
        titleBar.Children.Add(titleText);
        titleBar.Children.Add(closeBtn);

        // エントリリスト
        var helperText = new System.Windows.Controls.TextBlock
        {
            Text = entries.Count == 0
                ? "No captures yet."
                : $"{entries.Count} entries (newest first)",
            Foreground = subtext,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 6)
        };

        var entryList = new System.Windows.Controls.ListBox
        {
            MinHeight = 160,
            MaxHeight = 200,
            Background = surface1,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            FontSize = 12
        };
        entryList.ItemTemplate = new DataTemplate(typeof(CaptureLogEntry))
        {
            VisualTree = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock))
        };
        if (entryList.ItemTemplate.VisualTree is FrameworkElementFactory entryTextFactory)
        {
            entryTextFactory.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(CaptureLogEntry.DisplayLabel)));
            entryTextFactory.SetValue(System.Windows.Controls.TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
            entryTextFactory.SetValue(System.Windows.Controls.TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            entryTextFactory.SetValue(System.Windows.Controls.TextBlock.FontSizeProperty, 12.0);
        }
        entryList.ItemContainerStyle = new Style(typeof(System.Windows.Controls.ListBoxItem))
        {
            Setters =
            {
                new Setter(System.Windows.Controls.Control.HorizontalContentAlignmentProperty, System.Windows.HorizontalAlignment.Stretch),
                new Setter(System.Windows.Controls.Control.PaddingProperty, new Thickness(0)),
                new Setter(System.Windows.Controls.Control.BackgroundProperty, System.Windows.Media.Brushes.Transparent),
                new Setter(System.Windows.Controls.Control.TemplateProperty, new ControlTemplate(typeof(System.Windows.Controls.ListBoxItem))
                {
                    VisualTree = new FrameworkElementFactory(typeof(Border), "Bd")
                })
            }
        };
        if (entryList.ItemContainerStyle.Setters
            .OfType<Setter>()
            .FirstOrDefault(s => s.Property == System.Windows.Controls.Control.TemplateProperty)?.Value is ControlTemplate entryItemTemplate &&
            entryItemTemplate.VisualTree is FrameworkElementFactory itemBorderFactory)
        {
            itemBorderFactory.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
            itemBorderFactory.SetValue(Border.BorderBrushProperty, System.Windows.Media.Brushes.Transparent);
            itemBorderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            itemBorderFactory.SetValue(Border.MarginProperty, new Thickness(2, 1, 2, 1));
            itemBorderFactory.SetValue(Border.PaddingProperty, new Thickness(8, 4, 8, 4));
            itemBorderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));

            var presenterFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            presenterFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Stretch);
            presenterFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            itemBorderFactory.AppendChild(presenterFactory);

            entryItemTemplate.Triggers.Add(new Trigger
            {
                Property = System.Windows.Controls.ListBoxItem.IsSelectedProperty,
                Value = true,
                Setters =
                {
                    new Setter(Border.BackgroundProperty, surface1, "Bd"),
                    new Setter(Border.BorderBrushProperty, surface2, "Bd")
                }
            });

            var hoverTrigger = new MultiTrigger();
            hoverTrigger.Conditions.Add(new Condition(System.Windows.Controls.ListBoxItem.IsMouseOverProperty, true));
            hoverTrigger.Conditions.Add(new Condition(System.Windows.Controls.ListBoxItem.IsSelectedProperty, false));
            hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, surface, "Bd"));
            hoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, surface2, "Bd"));
            entryItemTemplate.Triggers.Add(hoverTrigger);
        }
        foreach (var entry in entries) entryList.Items.Add(entry);

        // フルコンテンツ表示
        var detailBox = new System.Windows.Controls.TextBox
        {
            Margin = new Thickness(0, 8, 0, 0),
            MinHeight = 160,
            MaxHeight = 200,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = surface1,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12,
            Text = entries.Count == 0 ? "(no captures)" : "(select an entry)"
        };

        entryList.SelectionChanged += (_, _) =>
        {
            if (entryList.SelectedItem is CaptureLogEntry sel)
                detailBox.Text = sel.Content;
        };

        var contentPanel = new StackPanel
        {
            Margin = new Thickness(16, 12, 16, 10),
            Children = { helperText, entryList, detailBox }
        };

        // フッター
        var openEditorBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Open in Editor",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            MinWidth = 130,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 0),
            IsEnabled = File.Exists(logPath)
        };
        var closeFooterBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Close",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            MinWidth = 100,
            Height = 32,
            IsCancel = true
        };
        var footer = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 12),
            Children = { openEditorBtn, closeFooterBtn }
        };

        var root = new Grid { Background = surface };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(contentPanel, 1);
        Grid.SetRow(footer, 2);
        root.Children.Add(titleBar);
        root.Children.Add(contentPanel);
        root.Children.Add(footer);

        var owner = Window.GetWindow(this);
        var dialogWindow = new Window
        {
            Title = "",
            Owner = owner,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            Width = 600,
            Background = surface,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            SizeToContent = SizeToContent.Height,
            Content = root
        };

        System.Windows.Shell.WindowChrome.SetWindowChrome(dialogWindow,
            new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(0),
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false
            });

        openEditorBtn.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(logPath) { UseShellExecute = true }); }
            catch { }
            dialogWindow.Close();
        };
        closeBtn.Click += (_, _) => dialogWindow.Close();
        closeFooterBtn.Click += (_, _) => dialogWindow.Close();
        titleBar.MouseLeftButtonDown += (_, ev) =>
        {
            if (ev.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
                dialogWindow.DragMove();
        };

        _ = dialogWindow.ShowDialog();
    }

    private sealed class CaptureLogEntry
    {
        public string Timestamp { get; init; } = "";
        public string Content { get; init; } = "";
        public string DisplayLabel
        {
            get
            {
                var first = Content.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
                var preview = first.Length > 60 ? first[..60] + "…" : first;
                return string.IsNullOrWhiteSpace(preview) ? $"{Timestamp}  (empty)" : $"{Timestamp}  {preview}";
            }
        }
    }

    private static List<CaptureLogEntry> ParseCaptureLog(string content)
    {
        var entries = new List<CaptureLogEntry>();
        string? ts = null;
        var body = new StringBuilder();

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("## "))
            {
                if (ts != null)
                    entries.Add(new CaptureLogEntry { Timestamp = ts, Content = body.ToString().Trim() });
                ts = line[3..].Trim();
                body.Clear();
            }
            else
            {
                body.AppendLine(line);
            }
        }
        if (ts != null)
            entries.Add(new CaptureLogEntry { Timestamp = ts, Content = body.ToString().Trim() });

        entries.Reverse(); // 新しい順
        return entries;
    }

    private async Task ShowUncommittedDetailsDialogAsync(ProjectCardViewModel card)
    {
        var items = await Task.Run(() => BuildUncommittedRepoStatusItems(card));

        var appResources = Application.Current.Resources;
        var surface = (System.Windows.Media.Brush)appResources["AppSurface0"];
        var surface1 = (System.Windows.Media.Brush)appResources["AppSurface1"];
        var surface2 = (System.Windows.Media.Brush)appResources["AppSurface2"];
        var text = (System.Windows.Media.Brush)appResources["AppText"];
        var subtext = (System.Windows.Media.Brush)appResources["AppSubtext0"];
        var accent = appResources.Contains("AppPeach")
            ? (System.Windows.Media.Brush)appResources["AppPeach"]
            : text;

        var repoList = new System.Windows.Controls.ListBox
        {
            Margin = new Thickness(0, 8, 0, 0),
            MinHeight = 120,
            MaxHeight = 240,
            Background = surface1,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            FontSize = 12,
            DisplayMemberPath = nameof(RepoStatusDialogItem.DisplayLabel)
        };
        foreach (var item in items) repoList.Items.Add(item);

        var detailsBox = new System.Windows.Controls.TextBox
        {
            Margin = new Thickness(0, 8, 0, 0),
            MinHeight = 210,
            MaxHeight = 290,
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = surface1,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12
        };

        var selectedRepoText = new System.Windows.Controls.TextBlock
        {
            Foreground = text,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 0)
        };

        var folderPathText = new System.Windows.Controls.TextBlock
        {
            Foreground = subtext,
            Margin = new Thickness(0, 6, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var helper = new System.Windows.Controls.TextBlock
        {
            Text = "Dirty repositories and current git status",
            Foreground = subtext,
            FontSize = 12
        };

        var titleBar = new Grid { Background = surface1, Height = 38 };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleIcon = new System.Windows.Controls.TextBlock
        {
            Text = "●",
            Foreground = accent,
            FontSize = 11,
            Margin = new Thickness(12, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleIcon, 0);

        var titleText = new System.Windows.Controls.TextBlock
        {
            Text = $"Uncommitted Changes ({card.Info.Name})",
            Foreground = text,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleText, 1);

        var closeButton = new System.Windows.Controls.Button
        {
            Content = "✕",
            Width = 34,
            Height = 26,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = subtext,
            FontSize = 13
        };
        Grid.SetColumn(closeButton, 2);

        titleBar.Children.Add(titleIcon);
        titleBar.Children.Add(titleText);
        titleBar.Children.Add(closeButton);

        var contentPanel = new System.Windows.Controls.StackPanel
        {
            Margin = new Thickness(16, 12, 16, 10),
            Children =
            {
                helper,
                repoList,
                selectedRepoText,
                detailsBox,
                folderPathText
            }
        };

        var openFolderButton = new Wpf.Ui.Controls.Button
        {
            Content = "Open Folder",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            MinWidth = 120,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 0)
        };

        var closeFooterButton = new Wpf.Ui.Controls.Button
        {
            Content = "Close",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            MinWidth = 110,
            Height = 32,
            IsCancel = true
        };

        var footer = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 12),
            Children = { openFolderButton, closeFooterButton }
        };

        var root = new Grid { Background = surface };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(contentPanel, 1);
        Grid.SetRow(footer, 2);
        root.Children.Add(titleBar);
        root.Children.Add(contentPanel);
        root.Children.Add(footer);

        var owner = Window.GetWindow(this);
        var dialogWindow = new Window
        {
            Title = "",
            Owner = owner,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            MinWidth = 760,
            Width = 760,
            Height = 670,
            Background = surface,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            Content = root
        };

        RepoStatusDialogItem? selectedItem = null;

        void ApplySelection(RepoStatusDialogItem? item)
        {
            selectedItem = item;
            if (item == null)
            {
                detailsBox.Text = "No dirty repositories found.";
                selectedRepoText.Text = "No repositories";
                folderPathText.Text = "";
                openFolderButton.IsEnabled = false;
                return;
            }

            selectedRepoText.Text = item.DisplayLabel;
            detailsBox.Text = item.Details;
            folderPathText.Text = item.FullPath;
            openFolderButton.IsEnabled = Directory.Exists(item.FullPath);
        }

        repoList.SelectionChanged += (_, _) =>
        {
            ApplySelection(repoList.SelectedItem as RepoStatusDialogItem);
        };

        openFolderButton.Click += (_, _) =>
        {
            if (selectedItem == null) return;
            if (!Directory.Exists(selectedItem.FullPath)) return;
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{selectedItem.FullPath}\"")
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Dashboard] Failed to open repo folder: {ex.Message}");
            }
        };

        closeButton.Click += (_, _) => dialogWindow.Close();
        closeFooterButton.Click += (_, _) => dialogWindow.Close();
        titleBar.MouseLeftButtonDown += (_, ev) =>
        {
            if (ev.LeftButton == MouseButtonState.Pressed) dialogWindow.DragMove();
        };

        if (items.Count > 0)
        {
            repoList.SelectedIndex = 0;
        }
        else
        {
            ApplySelection(null);
        }

        _ = dialogWindow.ShowDialog();
    }

    private static List<RepoStatusDialogItem> BuildUncommittedRepoStatusItems(ProjectCardViewModel card)
    {
        var devSource = Path.Combine(card.Info.Path, "development", "source");
        var relativePaths = card.Info.UncommittedRepoPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p)
            .ToList();

        var results = new List<RepoStatusDialogItem>();
        foreach (var relative in relativePaths)
        {
            var fullPath = relative == "."
                ? devSource
                : Path.Combine(devSource, relative.Replace('/', '\\'));

            var porcelainLines = RunGitStatusPorcelain(fullPath);
            var staged = 0;
            var modified = 0;
            var untracked = 0;
            var conflicts = 0;

            foreach (var line in porcelainLines)
            {
                if (line.Length < 2) continue;
                var x = line[0];
                var y = line[1];

                if (x == '?' && y == '?')
                {
                    untracked++;
                    continue;
                }

                if (x == 'U' || y == 'U')
                    conflicts++;

                if (x != ' ' && x != '?')
                    staged++;

                if (y != ' ' && y != '?')
                    modified++;
            }

            var details = porcelainLines.Count > 0
                ? string.Join(Environment.NewLine, porcelainLines)
                : "(clean now)";

            results.Add(new RepoStatusDialogItem
            {
                RelativePath = relative,
                FullPath = fullPath,
                TotalCount = porcelainLines.Count,
                StagedCount = staged,
                ModifiedCount = modified,
                UntrackedCount = untracked,
                ConflictCount = conflicts,
                Details = details
            });
        }

        return results;
    }

    private static List<string> RunGitStatusPorcelain(string repoPath)
    {
        if (!Directory.Exists(repoPath)) return [];

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"-c core.quotepath=false -C \"{repoPath}\" status --porcelain=1 --untracked-files=normal",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };

            process.Start();
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                return [$"[git status error] {stderr.Trim()}"];
            }

            return stdout
                .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        }
        catch (Exception ex)
        {
            return [$"[git status exception] {ex.Message}"];
        }
    }

    private void OnTodayQueueRefreshClick(object sender, RoutedEventArgs e)
        => _ = ViewModel.LoadTodayQueueAsync();

    private void OnSilenceAlertOpenClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SilenceAlert alert })
            ViewModel.OpenSilenceAlertProject(alert);
    }

    private void OnSilenceAlertSnoozeClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SilenceAlert alert })
            ViewModel.SnoozeSilenceAlert(alert);
    }

    private void OnSilenceAlertDismissClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SilenceAlert alert })
            ViewModel.DismissSilenceAlert(alert);
    }

    private void OnSilenceAlertForceRefreshClick(object sender, RoutedEventArgs e)
        => _ = ViewModel.ForceRefreshSilenceAlertsAsync();

    private void OnTodayQueueOpenAsana(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TodayQueueTask task } &&
            !string.IsNullOrWhiteSpace(task.AsanaUrl))
        {
            try { Process.Start(new ProcessStartInfo(task.AsanaUrl) { UseShellExecute = true }); }
            catch { }
        }
    }

    private async void OnAddTaskClick(object sender, RoutedEventArgs e)
        => await ShowAddTaskDialogAsync();

    internal async Task ShowAddTaskDialogAsync(string? initialProjectName = null)
    {
        var appResources = Application.Current.Resources;
        var surface  = (System.Windows.Media.Brush)appResources["AppSurface0"];
        var surface1 = (System.Windows.Media.Brush)appResources["AppSurface1"];
        var surface2 = (System.Windows.Media.Brush)appResources["AppSurface2"];
        var text     = (System.Windows.Media.Brush)appResources["AppText"];
        var subtext  = (System.Windows.Media.Brush)appResources["AppSubtext0"];
        var accent   = appResources.Contains("AppPeach")
            ? (System.Windows.Media.Brush)appResources["AppPeach"]
            : text;

        var asanaGlobal = _configService.LoadAsanaGlobalConfig();
        bool asanaEnabled = !string.IsNullOrWhiteSpace(asanaGlobal.AsanaToken);

        // ── Controls ──────────────────────────────────────────────────────
        System.Windows.Controls.TextBlock MakeLabel(string t) => new()
        {
            Text = t, Foreground = subtext, FontSize = 11,
            Margin = new Thickness(0, 0, 0, 2)
        };

        var taskNameBox = new System.Windows.Controls.TextBox
        {
            Background = surface1, Foreground = text,
            BorderBrush = surface2, BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 4, 6, 4),
            FontSize = 13, Margin = new Thickness(0, 4, 0, 0)
        };

        var notesBox = new System.Windows.Controls.TextBox
        {
            Background = surface1, Foreground = text,
            BorderBrush = surface2, BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 4, 6, 4),
            FontSize = 12, Margin = new Thickness(0, 4, 0, 0),
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            Height = 80, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        var projectCombo = new System.Windows.Controls.ComboBox
        {
            Background = surface1, Foreground = text,
            BorderBrush = surface2, FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0), IsEnabled = false
        };

        var asanaProjectCombo = new System.Windows.Controls.ComboBox
        {
            Background = surface1, Foreground = text,
            BorderBrush = surface2, FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0), IsEnabled = false
        };
        var sectionCombo = new System.Windows.Controls.ComboBox
        {
            Background = surface1, Foreground = text,
            BorderBrush = surface2, FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0), IsEnabled = false
        };

        var dueDatePicker = new System.Windows.Controls.DatePicker
        {
            Background = surface1, Foreground = text,
            FontSize = 12, Margin = new Thickness(0, 4, 0, 0),
            Width = 160
        };
        var setTimeCheckBox = new System.Windows.Controls.CheckBox
        {
            Content = "Set time",
            Foreground = subtext, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        var dueDateRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { dueDatePicker, setTimeCheckBox }
        };

        System.Windows.Controls.ComboBox BuildTimeCombo(int width) =>
            new()
            {
                Background = surface1, Foreground = text,
                BorderBrush = surface2, FontSize = 12,
                Width = width
            };

        var hourCombo   = BuildTimeCombo(80);
        var minuteCombo = BuildTimeCombo(80);
        foreach (var h in Enumerable.Range(0, 24)) hourCombo.Items.Add(h.ToString("00"));
        foreach (var m in new[] { "00", "15", "30", "45" }) minuteCombo.Items.Add(m);
        hourCombo.SelectedIndex   = 9;  // default 09:00
        minuteCombo.SelectedIndex = 0;

        var timePickerRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4, 0, 0),
            Visibility = Visibility.Collapsed,
            Children =
            {
                hourCombo,
                new System.Windows.Controls.TextBlock
                {
                    Text = ":", Foreground = text, FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 4, 0)
                },
                minuteCombo
            }
        };

        setTimeCheckBox.Checked   += (_, _) => timePickerRow.Visibility = Visibility.Visible;
        setTimeCheckBox.Unchecked += (_, _) => timePickerRow.Visibility = Visibility.Collapsed;

        // ── Quick date buttons ────────────────────────────────────────────
        static DateTime NextWeekday(DayOfWeek day)
        {
            int delta = ((int)day - (int)DateTime.Today.DayOfWeek + 7) % 7;
            return DateTime.Today.AddDays(delta);
        }
        Wpf.Ui.Controls.Button MakeQuickBtn(string label) => new()
        {
            Content = label,
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            Padding = new Thickness(8, 0, 8, 0),
            Height = 22, FontSize = 11,
            Margin = new Thickness(0, 0, 4, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        var todayBtn    = MakeQuickBtn("Today");
        var tomorrowBtn = MakeQuickBtn("Tomorrow");
        var thisFriBtn  = MakeQuickBtn("This Fri");
        todayBtn.Click    += (_, _) => dueDatePicker.SelectedDate = DateTime.Today;
        tomorrowBtn.Click += (_, _) => dueDatePicker.SelectedDate = DateTime.Today.AddDays(1);
        thisFriBtn.Click  += (_, _) => dueDatePicker.SelectedDate = NextWeekday(DayOfWeek.Friday);
        var quickDateRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4, 0, 0),
            Children = { todayBtn, tomorrowBtn, thisFriBtn }
        };

        var errorText = new System.Windows.Controls.TextBlock
        {
            Foreground = accent, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed
        };

        var contentPanel = new StackPanel { Margin = new Thickness(16, 12, 16, 8) };
        contentPanel.Children.Add(MakeLabel("Task name"));
        contentPanel.Children.Add(taskNameBox);
        contentPanel.Children.Add(new StackPanel
        {
            Margin = new Thickness(0, 8, 0, 0),
            Children = { MakeLabel("Notes (optional)"), notesBox }
        });
        contentPanel.Children.Add(new StackPanel
        {
            Margin = new Thickness(0, 8, 0, 0),
            Children = { MakeLabel("Project"), projectCombo }
        });
        if (asanaEnabled)
        {
            contentPanel.Children.Add(new StackPanel
            {
                Margin = new Thickness(0, 8, 0, 0),
                Children = { MakeLabel("Asana project"), asanaProjectCombo }
            });
            contentPanel.Children.Add(new StackPanel
            {
                Margin = new Thickness(0, 8, 0, 0),
                Children = { MakeLabel("Section"), sectionCombo }
            });
        }
        contentPanel.Children.Add(new StackPanel
        {
            Margin = new Thickness(0, 8, 0, 0),
            Children = { MakeLabel("Due date (optional)"), dueDateRow, quickDateRow, timePickerRow }
        });
        contentPanel.Children.Add(errorText);

        // ── Title bar ─────────────────────────────────────────────────────
        var titleBar = new Grid { Background = surface1, Height = 38 };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleText = new System.Windows.Controls.TextBlock
        {
            Text = "Add Task", Foreground = text, FontSize = 14,
            FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0)
        };
        var closeTitleBtn = new System.Windows.Controls.Button
        {
            Content = "✕", Width = 34, Height = 26,
            Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0), Foreground = subtext, FontSize = 13
        };
        Grid.SetColumn(titleText, 0);
        Grid.SetColumn(closeTitleBtn, 1);
        titleBar.Children.Add(titleText);
        titleBar.Children.Add(closeTitleBtn);

        // ── Footer ────────────────────────────────────────────────────────
        var createBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Create",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            MinWidth = 100, Height = 32, Margin = new Thickness(0, 0, 8, 0), IsDefault = true
        };
        var cancelBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Cancel",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            MinWidth = 100, Height = 32, IsCancel = true
        };
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 12),
            Children = { createBtn, cancelBtn }
        };

        var root = new Grid { Background = surface };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(contentPanel, 1);
        Grid.SetRow(footer, 2);
        root.Children.Add(titleBar);
        root.Children.Add(contentPanel);
        root.Children.Add(footer);

        var dialogWindow = new Window
        {
            Title = "",
            Owner = Window.GetWindow(this),
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            MinHeight = 0,
            MinWidth = 420, Width = 420,
            Background = surface,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            Content = root
        };

        titleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
                dialogWindow.DragMove();
        };

        // ── Project loading ───────────────────────────────────────────────
        List<AsanaProjectMeta> allAsanaProjects = [];

        _ = Task.Run(async () =>
        {
            var projects = await _discoveryService.GetProjectInfoListAsync();
            Application.Current.Dispatcher.Invoke(() =>
            {
                projectCombo.Items.Clear();
                foreach (var p in projects)
                    projectCombo.Items.Add(p);
                projectCombo.DisplayMemberPath = "DisplayName";
                if (!string.IsNullOrEmpty(initialProjectName))
                {
                    var match = projects.FirstOrDefault(p =>
                        p.Name.Equals(initialProjectName, StringComparison.OrdinalIgnoreCase));
                    projectCombo.SelectedItem = match ?? (projectCombo.Items.Count > 0 ? projects[0] : null);
                }
                else if (projectCombo.Items.Count > 0)
                    projectCombo.SelectedIndex = 0;
                projectCombo.IsEnabled = true;
            });
        });

        // On Curia project change → load Asana projects
        projectCombo.SelectionChanged += async (_, _) =>
        {
            if (!asanaEnabled) return;
            if (projectCombo.SelectedItem is not ProjectInfo selectedProject) return;

            asanaProjectCombo.IsEnabled = false;
            asanaProjectCombo.Items.Clear();
            sectionCombo.IsEnabled = false;
            sectionCombo.Items.Clear();

            var (gids, _) = _captureService.LoadAsanaProjectGids(selectedProject);
            if (gids.Count == 0)
            {
                asanaProjectCombo.Items.Add("(No Asana project configured)");
                asanaProjectCombo.SelectedIndex = 0;
                return;
            }

            asanaProjectCombo.Items.Add("Loading...");
            asanaProjectCombo.SelectedIndex = 0;

            var metas = new List<AsanaProjectMeta>();
            foreach (var gid in gids)
            {
                var meta = await _captureService.FetchProjectMetaAsync(gid);
                if (meta != null) metas.Add(meta);
            }

            asanaProjectCombo.Items.Clear();
            allAsanaProjects = metas;
            foreach (var m in metas)
                asanaProjectCombo.Items.Add(m);
            asanaProjectCombo.DisplayMemberPath = "Name";
            if (asanaProjectCombo.Items.Count > 0)
                asanaProjectCombo.SelectedIndex = 0;
            asanaProjectCombo.IsEnabled = metas.Count > 0;
        };

        // On Asana project change → load sections
        asanaProjectCombo.SelectionChanged += async (_, _) =>
        {
            if (asanaProjectCombo.SelectedItem is not AsanaProjectMeta selectedAsanaProject) return;

            sectionCombo.IsEnabled = false;
            sectionCombo.Items.Clear();

            var sections = await _captureService.FetchSectionsAsync(selectedAsanaProject.Gid);

            sectionCombo.Items.Add(new AsanaSectionMeta { Gid = "", Name = "(None)" });
            foreach (var s in sections)
                sectionCombo.Items.Add(s);
            sectionCombo.DisplayMemberPath = "Name";
            sectionCombo.SelectedIndex = 0;
            sectionCombo.IsEnabled = true;
        };

        // ── Create handler ────────────────────────────────────────────────
        createBtn.Click += async (_, _) =>
        {
            var taskName = taskNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(taskName))
            {
                errorText.Text = "Task name is required.";
                errorText.Visibility = Visibility.Visible;
                return;
            }

            createBtn.IsEnabled = false;
            cancelBtn.IsEnabled = false;
            errorText.Visibility = Visibility.Collapsed;

            try
            {
                CaptureRouteResult result;

                // ── Due date / time 計算 ──────────────────────────────────────
                var selectedDate = dueDatePicker.SelectedDate;
                var hasTime = setTimeCheckBox.IsChecked == true;
                if (hasTime && selectedDate == null)
                {
                    errorText.Text = "Select a due date when setting time.";
                    errorText.Visibility = Visibility.Visible;
                    createBtn.IsEnabled = true;
                    cancelBtn.IsEnabled = true;
                    return;
                }
                string dueOn = "", dueAt = "";
                if (selectedDate.HasValue)
                {
                    if (hasTime)
                    {
                        var h   = int.Parse((string)hourCombo.SelectedItem!);
                        var m   = int.Parse((string)minuteCombo.SelectedItem!);
                        var dto = new DateTimeOffset(
                            selectedDate.Value.Year, selectedDate.Value.Month, selectedDate.Value.Day,
                            h, m, 0, DateTimeOffset.Now.Offset);
                        dueAt = dto.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz");
                    }
                    else
                    {
                        dueOn = selectedDate.Value.ToString("yyyy-MM-dd");
                    }
                }

                if (asanaEnabled && asanaProjectCombo.SelectedItem is AsanaProjectMeta asanaProject
                    && !string.IsNullOrWhiteSpace(asanaProject.Gid))
                {
                    var selectedSection = sectionCombo.SelectedItem as AsanaSectionMeta;
                    var sectionGid  = selectedSection?.Gid ?? "";
                    var sectionName = string.IsNullOrEmpty(sectionGid) ? "" : (selectedSection?.Name ?? "");

                    var preview = new AsanaTaskCreatePreview
                    {
                        ProjectName = asanaProject.Name,
                        ProjectGid  = asanaProject.Gid,
                        SectionName = sectionName,
                        SectionGid  = sectionGid,
                        TaskName    = taskName,
                        Notes       = notesBox.Text.Trim(),
                        DueOn       = dueOn,
                        DueAt       = dueAt
                    };

                    result = await _captureService.CreateAsanaTaskAsync(
                        preview, $"addtask:{taskName}:{DateTime.Now:yyyyMMddHHmmss}");
                }
                else
                {
                    // Asana 未設定またはプロジェクト未選択 → ローカルタスク
                    if (projectCombo.SelectedItem is not ProjectInfo localProject)
                    {
                        errorText.Text = "Select a project.";
                        errorText.Visibility = Visibility.Visible;
                        createBtn.IsEnabled = true;
                        cancelBtn.IsEnabled = true;
                        return;
                    }

                    var classification = new CaptureClassification
                    {
                        Category    = "task",
                        ProjectName = localProject.Name,
                        Summary     = taskName,
                        DueOn       = dueOn
                    };
                    result = await _captureService.CreateLocalTaskAsync(classification, taskName);
                }

                if (result.Success)
                {
                    dialogWindow.DialogResult = true;
                    dialogWindow.Close();
                    await ViewModel.LoadTodayQueueAsync();
                    var tipBody = string.IsNullOrWhiteSpace(result.AsanaTaskUrl)
                        ? taskName
                        : $"{taskName}\n{result.AsanaTaskUrl}";
                    _trayService.ShowBalloonTip("Task Created", tipBody);
                }
                else
                {
                    errorText.Text = result.Message;
                    errorText.Visibility = Visibility.Visible;
                    createBtn.IsEnabled = true;
                    cancelBtn.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                errorText.Text = ex.Message;
                errorText.Visibility = Visibility.Visible;
                createBtn.IsEnabled = true;
                cancelBtn.IsEnabled = true;
            }
        };

        cancelBtn.Click     += (_, _) => { dialogWindow.DialogResult = false; dialogWindow.Close(); };
        closeTitleBtn.Click += (_, _) => { dialogWindow.DialogResult = false; dialogWindow.Close(); };

        dialogWindow.Loaded += (_, _) => taskNameBox.Focus();
        dialogWindow.ShowDialog();
    }

    private async void OnTodayQueueDoneClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TodayQueueTask task }) return;

        var confirmMsg = task.IsLocalOnly
            ? $"Mark as done?\n[{task.ProjectShortName}] {task.Title}"
            : $"Mark as done in Asana?\n[{task.ProjectShortName}] {task.Title}";
        var confirm = System.Windows.MessageBox.Show(
            confirmMsg,
            "Confirm Done",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        if (sender is UIElement btn) btn.IsEnabled = false;
        await ViewModel.CompleteTaskAsync(task);
    }

    private void OnTodayQueueSnoozeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TodayQueueTask task }) return;
        _ = ViewModel.SnoozeTaskAsync(task);
    }

    private async void OnTodayQueueChangeDueDateClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: TodayQueueTask task }) return;
        if (task.IsLocalOnly)
        {
            System.Windows.MessageBox.Show(
                "Due date update is only available for Asana tasks.",
                "Not Supported", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await ShowChangeDueDateDialogAsync(task);
    }

    private async Task ShowChangeDueDateDialogAsync(TodayQueueTask task)
    {
        var appResources = Application.Current.Resources;
        var surface  = (System.Windows.Media.Brush)appResources["AppSurface0"];
        var surface1 = (System.Windows.Media.Brush)appResources["AppSurface1"];
        var surface2 = (System.Windows.Media.Brush)appResources["AppSurface2"];
        var text     = (System.Windows.Media.Brush)appResources["AppText"];
        var subtext  = (System.Windows.Media.Brush)appResources["AppSubtext0"];
        var accent   = appResources.Contains("AppPeach")
            ? (System.Windows.Media.Brush)appResources["AppPeach"] : text;

        System.Windows.Controls.TextBlock MakeLabel(string t) => new()
        {
            Text = t, Foreground = subtext, FontSize = 11, Margin = new Thickness(0, 0, 0, 2)
        };

        // ── Date / time controls ──────────────────────────────────────────
        var picker = new System.Windows.Controls.DatePicker
        {
            Background = surface1, Foreground = text,
            FontSize = 12, Width = 160,
            SelectedDate = task.DueDate?.Date
        };
        var setTimeChk = new System.Windows.Controls.CheckBox
        {
            Content = "Set time", Foreground = subtext, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0)
        };
        var pickerRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4, 0, 0),
            Children = { picker, setTimeChk }
        };

        static DateTime NextWeekday(DayOfWeek day)
        {
            int delta = ((int)day - (int)DateTime.Today.DayOfWeek + 7) % 7;
            return DateTime.Today.AddDays(delta);
        }
        Wpf.Ui.Controls.Button MakeQuickBtn(string label) => new()
        {
            Content = label,
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            Padding = new Thickness(8, 0, 8, 0),
            Height = 22, FontSize = 11, Margin = new Thickness(0, 0, 4, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        var todayBtn    = MakeQuickBtn("Today");
        var tomorrowBtn = MakeQuickBtn("Tomorrow");
        var thisFriBtn  = MakeQuickBtn("This Fri");
        todayBtn.Click    += (_, _) => picker.SelectedDate = DateTime.Today;
        tomorrowBtn.Click += (_, _) => picker.SelectedDate = DateTime.Today.AddDays(1);
        thisFriBtn.Click  += (_, _) => picker.SelectedDate = NextWeekday(DayOfWeek.Friday);
        var quickRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4, 0, 0),
            Children = { todayBtn, tomorrowBtn, thisFriBtn }
        };

        System.Windows.Controls.ComboBox BuildTimeCombo(int width) => new()
        {
            Background = surface1, Foreground = text,
            BorderBrush = surface2, FontSize = 12, Width = width
        };
        var hourCombo   = BuildTimeCombo(80);
        var minuteCombo = BuildTimeCombo(80);
        foreach (var h in Enumerable.Range(0, 24)) hourCombo.Items.Add(h.ToString("00"));
        foreach (var m in new[] { "00", "15", "30", "45" }) minuteCombo.Items.Add(m);

        // pre-populate time if task has an exact time
        if (task.DueDate.HasValue && task.DueDate.Value.TimeOfDay != TimeSpan.Zero)
        {
            setTimeChk.IsChecked    = true;
            hourCombo.SelectedIndex = task.DueDate.Value.Hour;
            minuteCombo.SelectedIndex = new[] { 0, 15, 30, 45 }
                .Select((v, i) => (v, i))
                .OrderBy(x => Math.Abs(x.v - task.DueDate.Value.Minute))
                .First().i;
        }
        else
        {
            hourCombo.SelectedIndex   = 9;
            minuteCombo.SelectedIndex = 0;
        }

        var timeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4, 0, 0),
            Visibility = setTimeChk.IsChecked == true ? Visibility.Visible : Visibility.Collapsed,
            Children =
            {
                hourCombo,
                new System.Windows.Controls.TextBlock
                {
                    Text = ":", Foreground = text, FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 4, 0)
                },
                minuteCombo
            }
        };
        setTimeChk.Checked   += (_, _) => timeRow.Visibility = Visibility.Visible;
        setTimeChk.Unchecked += (_, _) => timeRow.Visibility = Visibility.Collapsed;

        var errorText = new System.Windows.Controls.TextBlock
        {
            Foreground = accent, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed
        };

        // ── Content panel ─────────────────────────────────────────────────
        var taskLabel = new System.Windows.Controls.TextBlock
        {
            Text = task.DisplayMainTitle, Foreground = text, FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 8)
        };
        var contentPanel = new StackPanel { Margin = new Thickness(16, 12, 16, 8) };
        contentPanel.Children.Add(taskLabel);
        contentPanel.Children.Add(MakeLabel("Due date"));
        contentPanel.Children.Add(pickerRow);
        contentPanel.Children.Add(quickRow);
        contentPanel.Children.Add(timeRow);
        contentPanel.Children.Add(errorText);

        // ── Title bar ─────────────────────────────────────────────────────
        var titleBar = new Grid { Background = surface1, Height = 38 };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleText = new System.Windows.Controls.TextBlock
        {
            Text = "Change Due Date", Foreground = text, FontSize = 14,
            FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0)
        };
        var closeTitleBtn = new System.Windows.Controls.Button
        {
            Content = "✕", Width = 34, Height = 26,
            Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0), Foreground = subtext, FontSize = 13
        };
        Grid.SetColumn(titleText, 0); Grid.SetColumn(closeTitleBtn, 1);
        titleBar.Children.Add(titleText); titleBar.Children.Add(closeTitleBtn);

        // ── Footer ────────────────────────────────────────────────────────
        var saveBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Save",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            MinWidth = 100, Height = 32, Margin = new Thickness(0, 0, 8, 0), IsDefault = true
        };
        var cancelBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Cancel",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            MinWidth = 100, Height = 32, IsCancel = true
        };
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 12),
            Children = { saveBtn, cancelBtn }
        };

        var root = new Grid { Background = surface };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titleBar, 0); Grid.SetRow(contentPanel, 1); Grid.SetRow(footer, 2);
        root.Children.Add(titleBar); root.Children.Add(contentPanel); root.Children.Add(footer);

        var dialogWindow = new Window
        {
            Title = "", Owner = Window.GetWindow(this), ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None, MinHeight = 0,
            MinWidth = 380, Width = 380,
            Background = surface, Foreground = text,
            BorderBrush = surface2, BorderThickness = new Thickness(1),
            Content = root
        };
        titleBar.MouseLeftButtonDown += (_, e2) =>
        {
            if (e2.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
                dialogWindow.DragMove();
        };

        saveBtn.Click += async (_, _) =>
        {
            var hasTime = setTimeChk.IsChecked == true;
            if (hasTime && picker.SelectedDate == null)
            {
                errorText.Text = "Select a due date when setting time.";
                errorText.Visibility = Visibility.Visible;
                return;
            }
            saveBtn.IsEnabled = false; cancelBtn.IsEnabled = false;
            errorText.Visibility = Visibility.Collapsed;

            string dueOn = "", dueAt = "";
            if (picker.SelectedDate.HasValue)
            {
                if (hasTime)
                {
                    var h = int.Parse((string)hourCombo.SelectedItem!);
                    var m = int.Parse((string)minuteCombo.SelectedItem!);
                    var dto = new DateTimeOffset(
                        picker.SelectedDate.Value.Year, picker.SelectedDate.Value.Month,
                        picker.SelectedDate.Value.Day, h, m, 0, DateTimeOffset.Now.Offset);
                    dueAt = dto.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz");
                }
                else
                {
                    dueOn = picker.SelectedDate.Value.ToString("yyyy-MM-dd");
                }
            }

            var (ok, msg) = await ViewModel.ChangeDueDateAsync(task, dueOn, dueAt);
            if (ok)
            {
                dialogWindow.DialogResult = true;
                dialogWindow.Close();
            }
            else
            {
                errorText.Text = msg;
                errorText.Visibility = Visibility.Visible;
                saveBtn.IsEnabled = true; cancelBtn.IsEnabled = true;
            }
        };
        cancelBtn.Click     += (_, _) => { dialogWindow.DialogResult = false; dialogWindow.Close(); };
        closeTitleBtn.Click += (_, _) => { dialogWindow.DialogResult = false; dialogWindow.Close(); };
        dialogWindow.ShowDialog();
    }

    private void OnUnsnoozeAllClick(object sender, RoutedEventArgs e)
        => _ = ViewModel.UnsnoozeAllAsync();

    private void OnToggleWorkstream(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProjectCardViewModel card })
            card.IsWorkstreamExpanded = !card.IsWorkstreamExpanded;
    }

    private void OnToggleShowClosedWorkstreams(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProjectCardViewModel card })
            card.ShowClosedWorkstreams = !card.ShowClosedWorkstreams;
    }

    private void OnOpenWorkstreamFocusClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not WorkstreamCardItem ws) return;
        if (string.IsNullOrWhiteSpace(ws.FocusFile) || !File.Exists(ws.FocusFile)) return;

        var card = FindAncestorDataContext<ProjectCardViewModel>(fe);
        if (card == null) return;

        if (Window.GetWindow(this) is MainWindow mw)
            mw.NavigateToEditorAndOpenFile(card.Info, ws.FocusFile);
    }

    private void OnOpenWorkstreamWorkRootClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not WorkstreamCardItem ws) return;
        var card = FindAncestorDataContext<ProjectCardViewModel>(fe);
        if (card == null) return;
        ViewModel.OpenWorkstreamWorkRoot(card, ws);
    }

    private async void OnCreateWorkstreamTodayFolderClick(object sender, RoutedEventArgs e)
    {
        FrameworkElement? fe = sender as FrameworkElement;
        if (sender is System.Windows.Controls.MenuItem mi
            && mi.Parent is System.Windows.Controls.ContextMenu cm
            && cm.PlacementTarget is FrameworkElement pt)
            fe = pt;
        if (fe is null) return;
        if (fe.DataContext is not WorkstreamCardItem ws) return;
        var card = FindAncestorDataContext<ProjectCardViewModel>(fe);
        if (card == null) return;

        var featureName = await ShowWorkFolderFeatureDialogAsync($"Create Workstream Work Folder ({ws.Label})");
        if (string.IsNullOrWhiteSpace(featureName)) return;

        var folder = ViewModel.CreateTodayWorkstreamWorkFolder(card, ws, featureName);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Dashboard] Failed to open folder: {ex}");
            }
        }
    }

    private Task<string?> ShowWorkFolderFeatureDialogAsync(string title)
    {
        var appResources = Application.Current.Resources;
        var surface = (System.Windows.Media.Brush)appResources["AppSurface0"];
        var surface1 = (System.Windows.Media.Brush)appResources["AppSurface1"];
        var surface2 = (System.Windows.Media.Brush)appResources["AppSurface2"];
        var text = (System.Windows.Media.Brush)appResources["AppText"];
        var subtext = (System.Windows.Media.Brush)appResources["AppSubtext0"];
        var accent = appResources.Contains("AppBlue")
            ? (System.Windows.Media.Brush)appResources["AppBlue"]
            : text;

        var input = new System.Windows.Controls.TextBox
        {
            Text = "",
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            FontSize = 14,
            Padding = new Thickness(10, 7, 10, 7),
            Background = surface1,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1)
        };

        var helper = new System.Windows.Controls.TextBlock
        {
            Text = "Enter the suffix for folder name format yyyyMMdd_xxx:",
            Foreground = subtext,
            Margin = new Thickness(0, 0, 0, 0),
            FontSize = 13
        };

        var titleBar = new Grid
        {
            Background = surface1,
            Height = 38
        };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleIcon = new System.Windows.Controls.TextBlock
        {
            Text = "◆",
            Foreground = accent,
            FontSize = 12,
            Margin = new Thickness(12, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleIcon, 0);

        var titleText = new System.Windows.Controls.TextBlock
        {
            Text = title,
            Foreground = text,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleText, 1);

        var closeButton = new System.Windows.Controls.Button
        {
            Content = "✕",
            Width = 34,
            Height = 26,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = subtext,
            FontSize = 13
        };
        Grid.SetColumn(closeButton, 2);

        titleBar.Children.Add(titleIcon);
        titleBar.Children.Add(titleText);
        titleBar.Children.Add(closeButton);

        var contentPanel = new StackPanel
        {
            Margin = new Thickness(18, 14, 18, 10),
            Children =
            {
                helper,
                input
            }
        };

        var createButton = new Wpf.Ui.Controls.Button
        {
            Content = "Create",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            MinWidth = 120,
            Height = 34,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };

        var cancelButton = new Wpf.Ui.Controls.Button
        {
            Content = "Cancel",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            MinWidth = 120,
            Height = 34,
            IsCancel = true
        };

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(18, 0, 18, 14),
            Children = { createButton, cancelButton }
        };

        var root = new Grid
        {
            Background = surface
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(contentPanel, 1);
        Grid.SetRow(footer, 2);
        root.Children.Add(titleBar);
        root.Children.Add(contentPanel);
        root.Children.Add(footer);

        var owner = Window.GetWindow(this);
        var dialogWindow = new Window
        {
            Title = "",
            Owner = owner,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            MinWidth = 500,
            MinHeight = 0,
            Width = 500,
            Background = surface,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            Content = root
        };

        string? value = null;
        createButton.Click += (_, _) =>
        {
            var trimmed = input.Text.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                input.Focus();
                return;
            }
            value = trimmed;
            dialogWindow.DialogResult = true;
            dialogWindow.Close();
        };

        cancelButton.Click += (_, _) =>
        {
            dialogWindow.DialogResult = false;
            dialogWindow.Close();
        };

        closeButton.Click += (_, _) =>
        {
            dialogWindow.DialogResult = false;
            dialogWindow.Close();
        };

        titleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                dialogWindow.DragMove();
        };

        _ = dialogWindow.ShowDialog();
        return Task.FromResult(value);
    }

    // ===== Pinned Folders ドラッグ&ドロップ =====

    private System.Windows.Point _pinDragStart;

    // ボタン上ではドラッグを開始しない
    private static bool IsOverButton(RoutedEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source != null)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void OnPinnedChipMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsOverButton(e)) return;
        _pinDragStart = e.GetPosition(null);
    }

    private void OnPinnedChipMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (IsOverButton(e)) return;
        if (sender is not FrameworkElement { DataContext: PinnedFolder pf }) return;
        var diff = e.GetPosition(null) - _pinDragStart;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop((DependencyObject)sender, pf, System.Windows.DragDropEffects.Move);
    }

    private void OnPinnedChipDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (sender is Border b) b.ClearValue(Border.BorderBrushProperty);
        if (sender is not FrameworkElement { DataContext: PinnedFolder target }) return;
        if (e.Data.GetData(typeof(PinnedFolder)) is not PinnedFolder source) return;
        if (ReferenceEquals(source, target)) return;
        ViewModel.MovePinnedFolder(source, target);
    }

    private void OnPinnedChipDragEnter(object sender, System.Windows.DragEventArgs e)
    {
        if (sender is Border b && e.Data.GetDataPresent(typeof(PinnedFolder)))
            b.BorderBrush = (System.Windows.Media.Brush)Application.Current.Resources["AppBlue"];
    }

    private void OnPinnedChipDragLeave(object sender, System.Windows.DragEventArgs e)
    {
        if (sender is Border b)
            b.ClearValue(Border.BorderBrushProperty);
    }

    // ===== Pinned Folders イベントハンドラ =====

    private void OnOpenPinnedFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PinnedFolder pf })
            ViewModel.OpenPinnedFolder(pf);
    }

    private void OnUnpinFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PinnedFolder pf })
            ViewModel.UnpinFolder(pf);
    }

    private void OnClearPinnedFolders(object sender, RoutedEventArgs e)
        => ViewModel.ClearPinnedFolders();

    private async void OnPinWorkstreamFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not WorkstreamCardItem ws) return;
        var card = FindAncestorDataContext<ProjectCardViewModel>(fe);
        if (card == null) return;

        var folders = await ViewModel.GetRecentWorkFoldersAsync(card.Info.Path, ws.Id);
        if (folders.Count == 0)
        {
            System.Windows.MessageBox.Show(
                "No recent work folders found.",
                "Pin Work Folder",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        var picked = await ShowPinFolderPickerDialogAsync(
            $"Pin Workstream Folder ({ws.Label})",
            folders,
            limit => ViewModel.GetRecentWorkFoldersAsync(card.Info.Path, ws.Id, limit));
        if (picked is null) return;

        ViewModel.PinFolder(card, ws, picked.Value.FolderName, picked.Value.FullPath);
    }

    private void OnPinWorkstreamFolderByPathClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem mi) return;
        if (mi.Parent is not System.Windows.Controls.ContextMenu cm) return;
        if (cm.PlacementTarget is not FrameworkElement fe) return;
        if (fe.DataContext is not WorkstreamCardItem ws) return;
        var card = FindAncestorDataContext<ProjectCardViewModel>(fe);
        if (card is null) return;

        var picked = ShowPinByFullPathDialog($"Pin Workstream Folder ({ws.Label})");
        if (picked is null) return;
        ViewModel.PinFolder(card, ws, picked.Value.FolderName, picked.Value.FullPath);
    }

    private async void OnPinGeneralWorkFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProjectCardViewModel card }) return;
        await PinGeneralWorkFolderAsync(card);
    }

    private async void OnDirMenuPinGeneralWorkFolder(object sender, RoutedEventArgs e)
    {
        if (GetCardFromMenuItem(sender) is { } card)
            await PinGeneralWorkFolderAsync(card);
    }

    private void OnPinGeneralWorkFolderByPathClick(object sender, RoutedEventArgs e)
    {
        if (GetCardFromMenuItem(sender) is not { } card) return;
        var picked = ShowPinByFullPathDialog($"Pin General Work Folder ({card.Info.Name})");
        if (picked is null) return;
        ViewModel.PinFolder(card, workstream: null, picked.Value.FolderName, picked.Value.FullPath);
    }

    private async Task PinGeneralWorkFolderAsync(ProjectCardViewModel card)
    {
        var folders = await ViewModel.GetRecentWorkFoldersAsync(card.Info.Path, workstreamId: null);
        if (folders.Count == 0)
        {
            System.Windows.MessageBox.Show(
                "No recent work folders found.",
                "Pin Work Folder",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        var picked = await ShowPinFolderPickerDialogAsync(
            $"Pin General Work Folder ({card.Info.Name})",
            folders,
            limit => ViewModel.GetRecentWorkFoldersAsync(card.Info.Path, null, limit));
        if (picked is null) return;

        ViewModel.PinFolder(card, workstream: null, picked.Value.FolderName, picked.Value.FullPath);
    }

    private Task<(string FolderName, string FullPath)?> ShowPinFolderPickerDialogAsync(
        string title,
        List<(string FolderName, string FullPath)> initialFolders,
        Func<int, Task<List<(string FolderName, string FullPath)>>>? loader = null)
    {
        var appResources = Application.Current.Resources;
        var surface = (System.Windows.Media.Brush)appResources["AppSurface0"];
        var surface1 = (System.Windows.Media.Brush)appResources["AppSurface1"];
        var surface2 = (System.Windows.Media.Brush)appResources["AppSurface2"];
        var text = (System.Windows.Media.Brush)appResources["AppText"];
        var subtext = (System.Windows.Media.Brush)appResources["AppSubtext0"];
        var accent = appResources.Contains("AppOverlay2")
            ? (System.Windows.Media.Brush)appResources["AppOverlay2"]
            : text;

        // 現在のフォルダリスト (Show more で上書きされる)
        var currentFolders = initialFolders;
        const int step = 10;

        var listBox = new System.Windows.Controls.ListBox
        {
            Margin = new Thickness(0, 10, 0, 0),
            MinHeight = 80,
            MaxHeight = 220,
            Background = surface1,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            FontSize = 12,
        };
        foreach (var (folderName, _) in currentFolders)
            listBox.Items.Add(folderName);
        if (listBox.Items.Count > 0) listBox.SelectedIndex = 0;

        var titleBar = new Grid { Background = surface1, Height = 38 };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleIcon = new System.Windows.Controls.TextBlock
        {
            Text = "★", Foreground = accent, FontSize = 13,
            Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleIcon, 0);

        var titleText = new System.Windows.Controls.TextBlock
        {
            Text = title, Foreground = text, FontSize = 14,
            FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleText, 1);

        var closeButton = new System.Windows.Controls.Button
        {
            Content = "✕", Width = 34, Height = 26,
            Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0), Foreground = subtext, FontSize = 13
        };
        Grid.SetColumn(closeButton, 2);

        titleBar.Children.Add(titleIcon);
        titleBar.Children.Add(titleText);
        titleBar.Children.Add(closeButton);

        var helper = new System.Windows.Controls.TextBlock
        {
            Text = "Select a folder to pin:",
            Foreground = subtext, FontSize = 12,
        };

        var showMoreBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Show more...",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            FontSize = 11,
            Height = 28,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            Visibility = loader != null ? Visibility.Visible : Visibility.Collapsed,
            IsEnabled = initialFolders.Count >= step,
        };

        var contentPanel = new StackPanel
        {
            Margin = new Thickness(16, 12, 16, 8),
            Children = { helper, listBox, showMoreBtn }
        };

        var pinButton = new Wpf.Ui.Controls.Button
        {
            Content = "Pin",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            MinWidth = 100, Height = 32,
            Margin = new Thickness(0, 0, 8, 0), IsDefault = true
        };
        var cancelButton = new Wpf.Ui.Controls.Button
        {
            Content = "Cancel",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            MinWidth = 100, Height = 32, IsCancel = true
        };

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 12),
            Children = { pinButton, cancelButton }
        };

        var root = new Grid { Background = surface };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(contentPanel, 1);
        Grid.SetRow(footer, 2);
        root.Children.Add(titleBar);
        root.Children.Add(contentPanel);
        root.Children.Add(footer);

        var owner = Window.GetWindow(this);
        var dialogWindow = new Window
        {
            Title = "",
            Owner = owner,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            MinWidth = 420, Width = 420,
            Background = surface,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            Content = root
        };

        (string FolderName, string FullPath)? result = null;

        pinButton.Click += (_, _) =>
        {
            if (listBox.SelectedIndex < 0) return;
            result = currentFolders[listBox.SelectedIndex];
            dialogWindow.DialogResult = true;
            dialogWindow.Close();
        };
        cancelButton.Click += (_, _) => { dialogWindow.DialogResult = false; dialogWindow.Close(); };
        closeButton.Click += (_, _) => { dialogWindow.DialogResult = false; dialogWindow.Close(); };
        listBox.MouseDoubleClick += (_, _) =>
        {
            if (listBox.SelectedIndex < 0) return;
            result = currentFolders[listBox.SelectedIndex];
            dialogWindow.DialogResult = true;
            dialogWindow.Close();
        };
        titleBar.MouseLeftButtonDown += (_, ev) =>
        {
            if (ev.LeftButton == MouseButtonState.Pressed) dialogWindow.DragMove();
        };

        showMoreBtn.Click += async (_, _) =>
        {
            if (loader is null) return;
            showMoreBtn.IsEnabled = false;
            var nextLimit = currentFolders.Count + step;
            var more = await loader(nextLimit);
            currentFolders = more;
            var prevIndex = listBox.SelectedIndex;
            listBox.Items.Clear();
            foreach (var (name, _) in currentFolders)
                listBox.Items.Add(name);
            listBox.SelectedIndex = prevIndex >= 0 ? prevIndex : 0;
            // まだ取得できる可能性があれば再度有効化
            showMoreBtn.IsEnabled = more.Count >= nextLimit;
        };

        _ = dialogWindow.ShowDialog();
        return Task.FromResult(result);
    }

    private (string FolderName, string FullPath)? ShowPinByFullPathDialog(string title)
    {
        var appResources = Application.Current.Resources;
        var surface  = (System.Windows.Media.Brush)appResources["AppSurface0"];
        var surface1 = (System.Windows.Media.Brush)appResources["AppSurface1"];
        var surface2 = (System.Windows.Media.Brush)appResources["AppSurface2"];
        var text     = (System.Windows.Media.Brush)appResources["AppText"];
        var subtext  = (System.Windows.Media.Brush)appResources["AppSubtext0"];
        var accent   = appResources.Contains("AppOverlay2")
            ? (System.Windows.Media.Brush)appResources["AppOverlay2"]
            : text;

        // --- タイトルバー ---
        var titleBar = new Grid { Background = surface1, Height = 38 };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleIcon = new System.Windows.Controls.TextBlock
        {
            Text = "★", Foreground = accent, FontSize = 13,
            Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleIcon, 0);

        var titleText = new System.Windows.Controls.TextBlock
        {
            Text = title, Foreground = text, FontSize = 14,
            FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleText, 1);

        var closeButton = new System.Windows.Controls.Button
        {
            Content = "✕", Width = 34, Height = 26,
            Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0), Foreground = subtext, FontSize = 13
        };
        Grid.SetColumn(closeButton, 2);

        titleBar.Children.Add(titleIcon);
        titleBar.Children.Add(titleText);
        titleBar.Children.Add(closeButton);

        // --- コンテンツ ---
        var helper = new System.Windows.Controls.TextBlock
        {
            Text = "Enter or browse for the folder to pin:",
            Foreground = subtext, FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8)
        };

        var pathBox = new System.Windows.Controls.TextBox
        {
            FontSize = 12,
            Height = 30,
            Background = surface1,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 4, 6, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        var browseButton = new Wpf.Ui.Controls.Button
        {
            Content = "Browse...",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            FontSize = 12,
            Height = 30,
            MinHeight = 0,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        var pathRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(browseButton, Dock.Right);
        pathRow.Children.Add(browseButton);
        pathRow.Children.Add(pathBox);

        var folderNameCaption = new System.Windows.Controls.TextBlock
        {
            Text = "Folder Name:",
            Foreground = subtext, FontSize = 11,
            Margin = new Thickness(0, 10, 0, 2)
        };

        var folderNameLabel = new System.Windows.Controls.TextBlock
        {
            Foreground = text, FontSize = 12,
            FontStyle = FontStyles.Italic
        };

        var contentPanel = new StackPanel
        {
            Margin = new Thickness(16, 12, 16, 8),
        };
        contentPanel.Children.Add(helper);
        contentPanel.Children.Add(pathRow);
        contentPanel.Children.Add(folderNameCaption);
        contentPanel.Children.Add(folderNameLabel);

        // --- フッター ---
        var pinButton = new Wpf.Ui.Controls.Button
        {
            Content = "Pin",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            MinWidth = 100, Height = 32,
            Margin = new Thickness(0, 0, 8, 0), IsDefault = true
        };
        var cancelButton = new Wpf.Ui.Controls.Button
        {
            Content = "Cancel",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            MinWidth = 100, Height = 32, IsCancel = true,
            Margin = new Thickness(0)
        };

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 12),
        };
        footer.Children.Add(pinButton);
        footer.Children.Add(cancelButton);

        var root = new Grid { Background = surface };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(contentPanel, 1);
        Grid.SetRow(footer, 2);
        root.Children.Add(titleBar);
        root.Children.Add(contentPanel);
        root.Children.Add(footer);

        var owner = Window.GetWindow(this);
        var dialogWindow = new Window
        {
            Title = "",
            Owner = owner,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            MinWidth = 480, MinHeight = 0, Width = 480,
            Background = surface,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            Content = root
        };
        (string FolderName, string FullPath)? result = null;

        // パス入力変更 → フォルダ名ラベル自動更新
        pathBox.TextChanged += (_, _) =>
        {
            var p = pathBox.Text.TrimEnd('\\', '/');
            folderNameLabel.Text = string.IsNullOrWhiteSpace(p) ? "" : Path.GetFileName(p);
        };

        browseButton.Click += (_, _) =>
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select folder to pin",
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(pathBox.Text) ? pathBox.Text : ""
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                pathBox.Text = dlg.SelectedPath;
        };

        pinButton.Click += (_, _) =>
        {
            var fullPath = pathBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(fullPath)) return;
            if (!Directory.Exists(fullPath))
            {
                System.Windows.MessageBox.Show(
                    $"Folder not found:\n{fullPath}",
                    "Pin Folder",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }
            var folderName = Path.GetFileName(fullPath.TrimEnd('\\', '/'));
            if (string.IsNullOrWhiteSpace(folderName)) folderName = fullPath;
            result = (folderName, fullPath);
            dialogWindow.DialogResult = true;
            dialogWindow.Close();
        };
        cancelButton.Click += (_, _) => { dialogWindow.DialogResult = false; dialogWindow.Close(); };
        closeButton.Click  += (_, _) => { dialogWindow.DialogResult = false; dialogWindow.Close(); };
        titleBar.MouseLeftButtonDown += (_, ev) =>
        {
            if (ev.LeftButton == MouseButtonState.Pressed) dialogWindow.DragMove();
        };

        _ = dialogWindow.ShowDialog();
        return result;
    }

    private static T? FindAncestorDataContext<T>(DependencyObject start)
        where T : class
    {
        DependencyObject? cur = start;
        while (cur != null)
        {
            if (cur is FrameworkElement f && f.DataContext is T found)
                return found;
            cur = VisualTreeHelper.GetParent(cur);
        }
        return null;
    }

    // Today Queue 高さモード切り替え (Fixed ⇔ Resizable)
    private bool _isQueueResizable = false;
    private const double FixedQueueHeight = 220;
    private bool _isDraggingSplitter = false;
    private System.Windows.Point _splitterDragStart;
    private double _splitterDragStartRow4Height;

    private void OnQueueSplitterMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isQueueResizable) return;
        _isDraggingSplitter = true;
        _splitterDragStart = e.GetPosition(RootGrid);
        _splitterDragStartRow4Height = RootGrid.RowDefinitions[4].ActualHeight;
        ((Border)sender).CaptureMouse();
        e.Handled = true;
    }

    private void OnQueueSplitterMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDraggingSplitter) return;
        var current = e.GetPosition(RootGrid);
        var deltaY = current.Y - _splitterDragStart.Y;
        // 上ドラッグ (deltaY < 0) → Queue が大きくなる
        var newHeight = _splitterDragStartRow4Height - deltaY;
        newHeight = Math.Clamp(newHeight, 60, RootGrid.ActualHeight - 100);
        RootGrid.RowDefinitions[4].Height = new GridLength(newHeight);
        e.Handled = true;
    }

    private void OnQueueSplitterMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingSplitter) return;
        _isDraggingSplitter = false;
        ((Border)sender).ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnToggleQueueResize(object sender, RoutedEventArgs e)
    {
        _isQueueResizable = !_isQueueResizable;

        if (_isQueueResizable)
        {
            // リサイズモード: GridSplitter を表示、高さを明示的に設定
            QueueSplitter.Visibility = Visibility.Visible;
            TodayQueueBorder.MaxHeight = double.PositiveInfinity;
            TodayQueueBorder.MinHeight = 60;
            RootGrid.RowDefinitions[4].Height = new GridLength(FixedQueueHeight);
            QueueResizeToggleBtn.Content = "📌";
            QueueResizeToggleBtn.ToolTip = "Switch to fixed height";
        }
        else
        {
            // 固定モード: GridSplitter を非表示、MaxHeight/MinHeight で高さを固定
            QueueSplitter.Visibility = Visibility.Collapsed;
            TodayQueueBorder.MaxHeight = FixedQueueHeight;
            TodayQueueBorder.MinHeight = FixedQueueHeight;
            RootGrid.RowDefinitions[4].Height = GridLength.Auto;
            QueueResizeToggleBtn.Content = "↕";
            QueueResizeToggleBtn.ToolTip = "Switch to resizable mode";
        }
    }

    // ===== Context Briefing =====

    private const string BriefingSystemPrompt = """
        You are a context-switching assistant for a professional managing multiple projects.
        The user is about to resume work on a specific project. Your job is to give them a quick briefing so they can get productive immediately.

        ## Output format
        Write exactly 3 sections in Markdown:

        ## Where you left off
        A 2-4 sentence narrative summary of the current state of the project. Connect the dots between focus, recent decisions, and open tensions. Do not just list facts.

        ## Suggested next steps
        3-5 numbered action items, ordered by priority. Each item should be specific and actionable. Include due labels if tasks are overdue or due today.

        ## Key context
        Bullet points of factual metadata that are relevant now.

        ## Rules
        - Be concise. The user wants to scan this quickly.
        - Prioritize what needs attention now: overdue tasks, stale focus, unresolved tensions, uncommitted changes.
        - If there is a conflict between focus plan and task progress, flag it.
        - If tensions relate to recent decisions, connect them explicitly.
        - Write in the same language as the project's context content when possible.
        - Output ONLY the 3 sections. No preamble, no closing.
        """;

    private static readonly Regex DecisionFileNameRx =
        new(@"^(?<date>\d{4}-\d{2}-\d{2})_(?<topic>.+)$", RegexOptions.Compiled);

    private static readonly Regex InProgressHeadingRx =
        new(@"^\s*#{2,3}\s*In\s+Progress\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DoneHeadingRx =
        new(@"^\s*#{2,3}\s*Completed\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AnyHeadingRx =
        new(@"^\s*#{2,3}\s+\S", RegexOptions.Compiled);

    private static readonly Regex UncheckedTaskRx =
        new(@"^\s*-\s+\[ \]\s+(.+)$", RegexOptions.Compiled);

    private static readonly Regex CompletedTaskRx =
        new(@"^\s*-\s+\[x\]\s+(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DueRx =
        new(@"\((?:Due|期限)\s*:\s*([^)]+)\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CompletedMarkerRx =
        new(@"<!--\s*completed:\s*(\d{4}-\d{2}-\d{2})\s*-->", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MarkdownNumberedRx =
        new(@"^\d+\.\s+.+$", RegexOptions.Compiled);

    private sealed class BriefingDecisionItem
    {
        public DateTime Date { get; init; }
        public string Topic { get; init; } = "";
        public string Preview { get; init; } = "";
    }

    private sealed class BriefingTaskItem
    {
        public string Name { get; init; } = "";
        public string DueLabel { get; init; } = "No due";
        public bool IsOverdue { get; init; }
    }

    private sealed class BriefingCompletedTaskItem
    {
        public string Name { get; init; } = "";
        public DateTime CompletedDate { get; init; }
    }

    private sealed class BriefingWorkstreamItem
    {
        public string Label { get; init; } = "";
        public int? FocusAge { get; init; }
        public int DecisionLogCount { get; init; }
        public bool IsClosed { get; init; }
    }

    private sealed class BriefingData
    {
        public required ProjectInfo Project { get; init; }
        public string FocusContent { get; set; } = "(no focus file)";
        public string OpenIssuesContent { get; set; } = "(no open issues file)";
        public List<string> WorkstreamFocusSnippets { get; } = [];
        public List<BriefingDecisionItem> Decisions { get; } = [];
        public List<BriefingTaskItem> ActiveTasks { get; } = [];
        public List<BriefingCompletedTaskItem> CompletedTasks { get; } = [];
        public List<BriefingWorkstreamItem> Workstreams { get; } = [];
        public int FocusAgeDays { get; set; } = -1;
        public int SummaryAgeDays { get; set; } = -1;
        public int UncommittedRepoCount { get; set; }
        public int DecisionLogThisMonth { get; set; }
        public int OpenIssuesCount { get; set; }
        public int OverdueTaskCount { get; set; }
        public bool HasCoreContext { get; set; }
    }

    public async Task ShowBriefingForProjectAsync(ProjectInfo project)
        => await ShowBriefingForProjectCoreAsync(project);

    private async void OnBriefingClickAsync(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProjectCardViewModel card })
            return;
        await ShowBriefingForProjectCoreAsync(card.Info);
    }

    private async Task ShowBriefingForProjectCoreAsync(ProjectInfo project)
    {
        if (!ViewModel.IsAiEnabled)
            return;

        using var cts = new System.Threading.CancellationTokenSource();
        var loadingWindow = BuildBriefingLoadingWindow(cts);
        loadingWindow.Show();

        try
        {
            var data = await CollectBriefingDataAsync(project, cts.Token);
            if (!data.HasCoreContext)
            {
                if (loadingWindow.IsVisible) loadingWindow.Close();
                MessageBox.Show(
                    "No context files found for this project. Create a current_focus.md to get started.",
                    "Briefing",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var userPrompt = BuildBriefingUserPrompt(data);
            var language = _configService.LoadSettings().LlmLanguage;
            var systemPrompt = language.Equals("English", StringComparison.OrdinalIgnoreCase)
                ? BriefingSystemPrompt
                : BriefingSystemPrompt + $"\n\nRespond in {language}.";

            var response = await _llmClientService.ChatCompletionAsync(systemPrompt, userPrompt, cts.Token);
            if (string.IsNullOrWhiteSpace(response))
            {
                if (loadingWindow.IsVisible) loadingWindow.Close();
                MessageBox.Show(
                    "Could not generate briefing. Please try again.",
                    "Briefing - Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (loadingWindow.IsVisible) loadingWindow.Close();
            ShowBriefingDialog(project, response.Trim());
        }
        catch (OperationCanceledException)
        {
            // cancelled
        }
        catch (InvalidOperationException ex)
        {
            if (loadingWindow.IsVisible) loadingWindow.Close();
            var result = MessageBox.Show(
                $"AI features are enabled but API key is not configured.\n\n{ex.Message}\n\nGo to Settings?",
                "Briefing - Error",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes && Window.GetWindow(this) is MainWindow mw)
                mw.NavigateToSettings();
        }
        catch (Exception ex)
        {
            if (loadingWindow.IsVisible) loadingWindow.Close();
            MessageBox.Show(
                $"Failed to generate briefing: {ex.Message}",
                "Briefing - Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task<BriefingData> CollectBriefingDataAsync(ProjectInfo project, System.Threading.CancellationToken ct)
    {
        var data = new BriefingData { Project = project };
        data.FocusAgeDays = project.FocusAge ?? -1;
        data.SummaryAgeDays = project.SummaryAge ?? -1;
        data.UncommittedRepoCount = project.UncommittedRepoStatuses.Count;
        data.DecisionLogThisMonth = project.DecisionLogDates.Count(d => d.Year == DateTime.Today.Year && d.Month == DateTime.Today.Month);

        ct.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(project.FocusFile) && File.Exists(project.FocusFile))
        {
            var (focusContent, _) = await _fileEncodingService.ReadFileAsync(project.FocusFile, ct);
            data.FocusContent = TrimForPrompt(focusContent, 3000);
            data.HasCoreContext = true;
        }

        ct.ThrowIfCancellationRequested();
        var openIssuesPath = Path.Combine(project.AiContextContentPath, "open_issues.md");
        if (File.Exists(openIssuesPath))
        {
            var (openIssuesContent, _) = await _fileEncodingService.ReadFileAsync(openIssuesPath, ct);
            data.OpenIssuesContent = TrimForPrompt(openIssuesContent, 2000);
            data.OpenIssuesCount = openIssuesContent
                .Split('\n')
                .Count(l => l.TrimStart().StartsWith("- ", StringComparison.Ordinal) || l.TrimStart().StartsWith("* ", StringComparison.Ordinal));
            data.HasCoreContext = true;
        }

        foreach (var ws in project.Workstreams)
        {
            data.Workstreams.Add(new BriefingWorkstreamItem
            {
                Label = string.IsNullOrWhiteSpace(ws.Label) ? ws.Id : ws.Label,
                FocusAge = ws.FocusAge,
                DecisionLogCount = ws.DecisionLogCount,
                IsClosed = ws.IsClosed,
            });

            if (string.IsNullOrWhiteSpace(ws.FocusFile) || !File.Exists(ws.FocusFile))
                continue;

            try
            {
                var (wsFocusContent, _) = await _fileEncodingService.ReadFileAsync(ws.FocusFile, ct);
                var snippet = TrimForPrompt(wsFocusContent, 500);
                if (!string.IsNullOrWhiteSpace(snippet))
                    data.WorkstreamFocusSnippets.Add($"{(string.IsNullOrWhiteSpace(ws.Label) ? ws.Id : ws.Label)}: {snippet}");
            }
            catch
            {
                // best effort
            }
        }

        var decisionFiles = new List<string>();
        var rootDecisionDir = Path.Combine(project.AiContextContentPath, "decision_log");
        if (Directory.Exists(rootDecisionDir))
            decisionFiles.AddRange(Directory.EnumerateFiles(rootDecisionDir, "*.md", SearchOption.TopDirectoryOnly));

        foreach (var ws in project.Workstreams)
        {
            var wsDecisionDir = Path.Combine(ws.Path, "decision_log");
            if (Directory.Exists(wsDecisionDir))
                decisionFiles.AddRange(Directory.EnumerateFiles(wsDecisionDir, "*.md", SearchOption.TopDirectoryOnly));
        }

        var decisionItems = new List<(DateTime date, string topic, string path)>();
        foreach (var file in decisionFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            var match = DecisionFileNameRx.Match(fileName);
            var date = match.Success && DateTime.TryParse(match.Groups["date"].Value, out var parsed)
                ? parsed
                : File.GetLastWriteTime(file).Date;
            var topic = match.Success ? match.Groups["topic"].Value : fileName;
            decisionItems.Add((date, topic, file));
        }

        foreach (var item in decisionItems
            .OrderByDescending(x => x.date)
            .ThenByDescending(x => File.GetLastWriteTime(x.path))
            .Take(3))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (content, _) = await _fileEncodingService.ReadFileAsync(item.path, ct);
                data.Decisions.Add(new BriefingDecisionItem
                {
                    Date = item.date,
                    Topic = item.topic,
                    Preview = TrimForPrompt(content, 300),
                });
                data.HasCoreContext = true;
            }
            catch
            {
                // best effort
            }
        }

        var asanaRootPath = Path.Combine(project.AiContextPath, "obsidian_notes", "tasks.md");
        if (File.Exists(asanaRootPath))
            await CollectAsanaTaskContextAsync(asanaRootPath, data, ct);

        var workstreamsAsanaRoot = Path.Combine(project.AiContextPath, "obsidian_notes", "workstreams");
        if (Directory.Exists(workstreamsAsanaRoot))
        {
            foreach (var ws in project.Workstreams.Where(w => !w.IsClosed))
            {
                ct.ThrowIfCancellationRequested();
                var wsAsana = Path.Combine(workstreamsAsanaRoot, ws.Id, "tasks.md");
                if (File.Exists(wsAsana))
                    await CollectAsanaTaskContextAsync(wsAsana, data, ct);
            }
        }

        data.ActiveTasks.Sort((a, b) =>
        {
            if (a.IsOverdue && !b.IsOverdue) return -1;
            if (!a.IsOverdue && b.IsOverdue) return 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        data.CompletedTasks.Sort((a, b) => b.CompletedDate.CompareTo(a.CompletedDate));

        data.OverdueTaskCount = data.ActiveTasks.Count(t => t.IsOverdue);
        if (data.ActiveTasks.Count > 20)
            data.ActiveTasks.RemoveRange(20, data.ActiveTasks.Count - 20);
        if (data.CompletedTasks.Count > 5)
            data.CompletedTasks.RemoveRange(5, data.CompletedTasks.Count - 5);

        return data;
    }

    private async Task CollectAsanaTaskContextAsync(string asanaPath, BriefingData data, System.Threading.CancellationToken ct)
    {
        var (content, _) = await _fileEncodingService.ReadFileAsync(asanaPath, ct);
        data.ActiveTasks.AddRange(ParseInProgressTasks(content));
        data.CompletedTasks.AddRange(ParseCompletedTasks(content, DateTime.Today.AddDays(-7)));
    }

    private static List<BriefingTaskItem> ParseInProgressTasks(string markdown)
    {
        var result = new List<BriefingTaskItem>();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var inProgress = false;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (InProgressHeadingRx.IsMatch(line))
            {
                inProgress = true;
                continue;
            }
            if (inProgress && AnyHeadingRx.IsMatch(line) && !InProgressHeadingRx.IsMatch(line))
            {
                inProgress = false;
                continue;
            }
            if (!inProgress) continue;

            var m = UncheckedTaskRx.Match(line);
            if (!m.Success) continue;

            var body = m.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(body) || body.StartsWith("<!--", StringComparison.Ordinal))
                continue;

            var dueLabel = BuildDueLabel(body, out var isOverdue);
            var name = NormalizeTaskName(body);
            if (string.IsNullOrWhiteSpace(name)) continue;

            result.Add(new BriefingTaskItem { Name = name, DueLabel = dueLabel, IsOverdue = isOverdue });
        }

        return result;
    }

    private static List<BriefingCompletedTaskItem> ParseCompletedTasks(string markdown, DateTime sinceDate)
    {
        var result = new List<BriefingCompletedTaskItem>();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var inDone = false;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (DoneHeadingRx.IsMatch(line))
            {
                inDone = true;
                continue;
            }
            if (inDone && AnyHeadingRx.IsMatch(line) && !DoneHeadingRx.IsMatch(line))
            {
                inDone = false;
                continue;
            }
            if (!inDone || !CompletedTaskRx.IsMatch(line)) continue;

            var completedMarker = CompletedMarkerRx.Match(line);
            if (!completedMarker.Success) continue;
            if (!DateTime.TryParse(completedMarker.Groups[1].Value, out var completedDate)) continue;
            if (completedDate.Date < sinceDate.Date) continue;

            var name = NormalizeTaskName(line);
            if (string.IsNullOrWhiteSpace(name)) continue;

            result.Add(new BriefingCompletedTaskItem { Name = name, CompletedDate = completedDate.Date });
        }

        return result;
    }

    private static string BuildDueLabel(string line, out bool isOverdue)
    {
        isOverdue = false;
        var m = DueRx.Match(line);
        if (!m.Success) return "No due";

        var rawDue = m.Groups[1].Value.Trim();
        if (!DateTime.TryParse(rawDue, out var dueDate))
            return rawDue;

        var delta = (dueDate.Date - DateTime.Today).Days;
        if (delta < 0)
        {
            isOverdue = true;
            return $"overdue {Math.Abs(delta)}d";
        }
        if (delta == 0) return "today";
        return $"in {delta}d";
    }

    private static string NormalizeTaskName(string raw)
    {
        var normalized = raw;
        normalized = Regex.Replace(normalized, @"<!--.*?-->", "", RegexOptions.Singleline);
        normalized = Regex.Replace(normalized, @"\[\[Asana\]\([^)]+\)\]", "", RegexOptions.IgnoreCase);
        normalized = DueRx.Replace(normalized, "");
        normalized = Regex.Replace(normalized, @"^\s*-\s+\[[ xX]\]\s+", "");
        normalized = Regex.Replace(normalized, @"^\s*\[(Owner|Collab|Other)\]\s*", "");
        return normalized.Trim();
    }

    private static string TrimForPrompt(string s, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var normalized = s.Replace("\r\n", "\n").Trim();
        if (normalized.Length <= maxChars) return normalized;
        return normalized[..maxChars] + "\n...(truncated)";
    }

    private static string BuildBriefingUserPrompt(BriefingData data)
    {
        var sb = new StringBuilder();
        var p = data.Project;

        sb.AppendLine($"## Project: {p.Name}");
        sb.AppendLine($"Date: {DateTime.Today:yyyy-MM-dd}");
        sb.AppendLine();

        sb.AppendLine("## Current Focus");
        sb.AppendLine(string.IsNullOrWhiteSpace(data.FocusContent) ? "(no focus file)" : data.FocusContent);
        if (data.WorkstreamFocusSnippets.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Workstream Focus Snippets");
            foreach (var wsFocus in data.WorkstreamFocusSnippets.Take(8))
                sb.AppendLine($"- {wsFocus}");
        }
        sb.AppendLine();

        sb.AppendLine("## Open Issues");
        sb.AppendLine(string.IsNullOrWhiteSpace(data.OpenIssuesContent) ? "(no open issues file)" : data.OpenIssuesContent);
        sb.AppendLine();

        sb.AppendLine("## Recent Decisions (latest 3)");
        if (data.Decisions.Count == 0)
        {
            sb.AppendLine("(no recent decisions)");
        }
        else
        {
            foreach (var d in data.Decisions)
            {
                sb.AppendLine($"### {d.Date:yyyy-MM-dd}: {d.Topic}");
                sb.AppendLine(string.IsNullOrWhiteSpace(d.Preview) ? "(no preview)" : d.Preview);
                sb.AppendLine();
            }
        }

        sb.AppendLine("## Active Tasks (in progress)");
        if (data.ActiveTasks.Count == 0)
            sb.AppendLine("(no active tasks)");
        else
            foreach (var t in data.ActiveTasks.Take(20))
                sb.AppendLine($"- {t.Name} (Due: {t.DueLabel})");
        sb.AppendLine();

        sb.AppendLine("## Recently Completed Tasks");
        if (data.CompletedTasks.Count == 0)
            sb.AppendLine("(none)");
        else
            foreach (var c in data.CompletedTasks.Take(5))
                sb.AppendLine($"- {c.Name} (completed: {c.CompletedDate:yyyy-MM-dd})");
        sb.AppendLine();

        var repoNames = data.Project.UncommittedRepoStatuses.Count == 0
            ? "none"
            : string.Join(", ", data.Project.UncommittedRepoStatuses.Select(r => r.RelativePath));

        sb.AppendLine("## Project Metrics");
        sb.AppendLine($"- Focus age: {(data.FocusAgeDays >= 0 ? $"{data.FocusAgeDays} days" : "missing")}");
        sb.AppendLine($"- Summary age: {(data.SummaryAgeDays >= 0 ? $"{data.SummaryAgeDays} days" : "missing")}");
        sb.AppendLine($"- Open issues: {data.OpenIssuesCount}");
        sb.AppendLine($"- Uncommitted repos: {data.UncommittedRepoCount} ({repoNames})");
        sb.AppendLine($"- Decision log entries this month: {data.DecisionLogThisMonth}");
        sb.AppendLine($"- Overdue tasks: {data.OverdueTaskCount}");
        sb.AppendLine();

        sb.AppendLine("## Workstreams");
        if (data.Workstreams.Count == 0)
            sb.AppendLine("(no workstreams)");
        else
            foreach (var ws in data.Workstreams.OrderBy(w => w.IsClosed).ThenBy(w => w.Label, StringComparer.OrdinalIgnoreCase))
            {
                var closedText = ws.IsClosed ? " (closed)" : "";
                var focusAge = ws.FocusAge.HasValue ? $"{ws.FocusAge}d" : "missing";
                sb.AppendLine($"{ws.Label}: Focus age {focusAge}, {ws.DecisionLogCount} decisions{closedText}");
            }

        return sb.ToString().TrimEnd();
    }

    private Window BuildBriefingLoadingWindow(System.Threading.CancellationTokenSource cts)
    {
        var appResources = Application.Current.Resources;
        var surface = (System.Windows.Media.Brush)appResources["AppSurface0"];
        var surface1 = (System.Windows.Media.Brush)appResources["AppSurface1"];
        var surface2 = (System.Windows.Media.Brush)appResources["AppSurface2"];
        var text = (System.Windows.Media.Brush)appResources["AppText"];
        var subtext = (System.Windows.Media.Brush)appResources["AppSubtext0"];

        var titleBar = new Grid { Background = surface1, Height = 38 };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var titleIcon = new System.Windows.Controls.TextBlock
        {
            Text = "💡",
            FontSize = 13,
            Margin = new Thickness(12, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleIcon, 0);

        var titleText = new System.Windows.Controls.TextBlock
        {
            Text = "Briefing",
            Foreground = text,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleText, 1);
        titleBar.Children.Add(titleIcon);
        titleBar.Children.Add(titleText);

        var loadingText = new System.Windows.Controls.TextBlock
        {
            Text = "Reading project context...",
            Foreground = subtext,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 14),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };

        var cancelBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Cancel",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            MinWidth = 100,
            Height = 32,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };

        var content = new StackPanel
        {
            Margin = new Thickness(24, 18, 24, 18),
            Children = { loadingText, cancelBtn }
        };

        var root = new Grid { Background = surface };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(content, 1);
        root.Children.Add(titleBar);
        root.Children.Add(content);

        var owner = Window.GetWindow(this);
        var win = new Window
        {
            Title = "",
            Owner = owner,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            Width = 360,
            Background = surface,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            Content = root
        };

        cancelBtn.Click += (_, _) => { cts.Cancel(); win.Close(); };
        titleBar.MouseLeftButtonDown += (_, ev) =>
        {
            if (ev.LeftButton == MouseButtonState.Pressed) win.DragMove();
        };

        return win;
    }

    private void ShowBriefingDialog(ProjectInfo project, string markdown)
    {
        var mw = Window.GetWindow(this) as MainWindow;
        var appResources = Application.Current.Resources;
        var surface = (System.Windows.Media.Brush)appResources["AppSurface0"];
        var surface1 = (System.Windows.Media.Brush)appResources["AppSurface1"];
        var surface2 = (System.Windows.Media.Brush)appResources["AppSurface2"];
        var (text, subtext) = ResolveDashboardMarkdownTextBrushes(surface, appResources);

        var titleBar = new Grid { Background = surface1, Height = 38 };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleIcon = new System.Windows.Controls.TextBlock
        {
            Text = "💡",
            FontSize = 13,
            Margin = new Thickness(12, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleIcon, 0);

        var titleText = new System.Windows.Controls.TextBlock
        {
            Text = $"Briefing: {project.Name}",
            Foreground = text,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(titleText, 1);

        var closeButton = new System.Windows.Controls.Button
        {
            Content = "✕",
            Width = 34,
            Height = 26,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = subtext,
            FontSize = 13
        };
        Grid.SetColumn(closeButton, 2);

        titleBar.Children.Add(titleIcon);
        titleBar.Children.Add(titleText);
        titleBar.Children.Add(closeButton);

        FrameworkElement contentBody;
        try
        {
            contentBody = BuildBriefingMarkdownPanel(markdown);
        }
        catch
        {
            contentBody = new System.Windows.Controls.TextBox
            {
                Text = markdown,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = surface1,
                Foreground = text,
                BorderBrush = surface2,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 12,
                Margin = new Thickness(16, 12, 16, 8)
            };
        }

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 4, 0, 0),
            Content = contentBody
        };

        var copyBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Copy",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            MinWidth = 90,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 0)
        };

        var debugBtn = new Wpf.Ui.Controls.Button
        {
            Content = "View Debug",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            Height = 32,
            Padding = new Thickness(10, 0, 10, 0),
            ToolTip = "Show LLM prompt/response"
        };

        var openEditorBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Open in Editor",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            MinWidth = 130,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 0)
        };

        var closeFooterBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Close",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            MinWidth = 90,
            Height = 32
        };

        var rightButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Children = { copyBtn, openEditorBtn, closeFooterBtn }
        };

        var footer = new Grid
        {
            Margin = new Thickness(16, 8, 16, 12)
        };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(debugBtn, 0);
        Grid.SetColumn(rightButtons, 2);
        footer.Children.Add(debugBtn);
        footer.Children.Add(rightButtons);

        var root = new Grid { Background = surface };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(scroller, 1);
        Grid.SetRow(footer, 2);
        root.Children.Add(titleBar);
        root.Children.Add(scroller);
        root.Children.Add(footer);

        var owner = Window.GetWindow(this);
        var dialog = new Window
        {
            Title = "",
            Owner = owner,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize,
            WindowStyle = WindowStyle.None,
            Width = 700,
            Height = 560,
            MinWidth = 620,
            MinHeight = 460,
            Background = surface,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            Content = root
        };

        System.Windows.Shell.WindowChrome.SetWindowChrome(dialog,
            new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(4),
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false
            });

        copyBtn.Click += (_, _) => System.Windows.Clipboard.SetText(markdown);
        debugBtn.Click += (_, _) =>
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== SYSTEM PROMPT ===");
            sb.AppendLine(_llmClientService.LastSystemPrompt);
            sb.AppendLine();
            sb.AppendLine("=== USER PROMPT ===");
            sb.AppendLine(_llmClientService.LastUserPrompt);
            sb.AppendLine();
            sb.AppendLine("=== RESPONSE ===");
            sb.AppendLine(_llmClientService.LastResponse);
            ShowWhatsNextLogDialog("Briefing Debug Log", sb.ToString());
        };
        openEditorBtn.Click += (_, _) =>
        {
            dialog.Close();
            mw?.NavigateToEditor(project);
            mw?.Activate();
        };
        closeButton.Click += (_, _) => dialog.Close();
        closeFooterBtn.Click += (_, _) => dialog.Close();
        titleBar.MouseLeftButtonDown += (_, ev) =>
        {
            if (ev.LeftButton == MouseButtonState.Pressed) dialog.DragMove();
        };

        _ = dialog.ShowDialog();
    }

    private FrameworkElement BuildBriefingMarkdownPanel(string markdown)
    {
        var appResources = Application.Current.Resources;
        var surface = (System.Windows.Media.Brush)appResources["AppSurface0"];
        var (text, subtext) = ResolveDashboardMarkdownTextBrushes(surface, appResources);
        var sectionBg = (System.Windows.Media.Brush)appResources["AppSurface1"];
        var sectionBorder = (System.Windows.Media.Brush)appResources["AppSurface2"];
        var itemBg = appResources.Contains("AppSurface0")
            ? (System.Windows.Media.Brush)appResources["AppSurface0"]
            : sectionBg;
        var red = appResources.Contains("AppRed")
            ? (System.Windows.Media.Brush)appResources["AppRed"]
            : text;
        var yellow = appResources.Contains("AppYellow")
            ? (System.Windows.Media.Brush)appResources["AppYellow"]
            : text;

        var panel = new StackPanel { Margin = new Thickness(16, 12, 16, 8) };
        var sectionStack = new StackPanel();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var hasSection = false;
        var currentSectionTitle = "";
        StackPanel? currentStepPanel = null;

        void FlushSection()
        {
            if (sectionStack.Children.Count == 0) return;
            panel.Children.Add(new Border
            {
                Background = sectionBg,
                BorderBrush = sectionBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 12, 14, 10),
                Margin = new Thickness(0, 0, 0, 12),
                Child = sectionStack
            });
            sectionStack = new StackPanel();
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            var cleanLine = CleanMarkdownLine(line);

            if (cleanLine.StartsWith("## ", StringComparison.Ordinal) || cleanLine.StartsWith("### ", StringComparison.Ordinal))
            {
                if (hasSection) FlushSection();
                hasSection = true;
                currentSectionTitle = cleanLine.TrimStart('#', ' ').Trim();
                currentStepPanel = null;
            }

            var isSuggestedStepsSection = currentSectionTitle.Contains("Suggested next steps", StringComparison.OrdinalIgnoreCase)
                || currentSectionTitle.Contains("次にやること", StringComparison.Ordinal)
                || currentSectionTitle.Contains("次のステップ", StringComparison.Ordinal);
            var isWhereOrKeyContextSection = currentSectionTitle.Contains("Where you left off", StringComparison.OrdinalIgnoreCase)
                || currentSectionTitle.Contains("Key context", StringComparison.OrdinalIgnoreCase)
                || currentSectionTitle.Contains("直近の状況", StringComparison.Ordinal)
                || currentSectionTitle.Contains("重要コンテキスト", StringComparison.Ordinal);

            if (isSuggestedStepsSection && MarkdownNumberedRx.IsMatch(cleanLine))
            {
                var stepText = FormatSuggestedStepLine(cleanLine);
                var stepPriority = GetDuePriority(stepText);
                var stepDisplay = stepPriority == 2 ? $"⚠ {stepText}" : stepText;
                var stepBlock = new System.Windows.Controls.TextBlock
                {
                    Text = stepDisplay,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    FontWeight = FontWeights.Normal,
                    LineHeight = 20,
                    Foreground = ResolveDueBrush(stepText, text, yellow, red),
                };

                currentStepPanel = new StackPanel();
                currentStepPanel.Children.Add(stepBlock);

                sectionStack.Children.Add(new Border
                {
                    Background = itemBg,
                    BorderBrush = sectionBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(10, 7, 10, 7),
                    Margin = new Thickness(6, 0, 0, 6),
                    Child = currentStepPanel
                });
                continue;
            }

            if (isSuggestedStepsSection && currentStepPanel != null && cleanLine.StartsWith("- ", StringComparison.Ordinal))
            {
                currentStepPanel.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = $"• {cleanLine[2..]}",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    LineHeight = 19,
                    Margin = new Thickness(0, 4, 0, 0),
                    Foreground = ResolveDueBrush(cleanLine, subtext, yellow, red)
                });
                continue;
            }

            var block = new System.Windows.Controls.TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                LineHeight = 19,
            };

            if (string.IsNullOrWhiteSpace(cleanLine))
            {
                currentStepPanel = null;
                block.Text = "";
                block.LineHeight = 8;
                block.Margin = new Thickness(0, 0, 0, 0);
                block.Foreground = text;
            }
            else if (cleanLine.StartsWith("## ", StringComparison.Ordinal) || cleanLine.StartsWith("### ", StringComparison.Ordinal))
            {
                block.Text = cleanLine.TrimStart('#', ' ');
                block.FontWeight = FontWeights.SemiBold;
                block.FontSize = 14;
                block.LineHeight = 20;
                block.Margin = new Thickness(0, 1, 0, 7);
                block.Foreground = text;
            }
            else if (cleanLine.StartsWith("- ", StringComparison.Ordinal))
            {
                block.Text = $"• {cleanLine[2..]}";
                block.Margin = new Thickness(10, 0, 0, 2);
                block.Foreground = ResolveDueBrush(cleanLine, text, yellow, red);
                if (isWhereOrKeyContextSection)
                {
                    block.FontSize = 13;
                    block.LineHeight = 20;
                }
            }
            else if (MarkdownNumberedRx.IsMatch(cleanLine))
            {
                currentStepPanel = null;
                block.Text = cleanLine;
                block.Margin = new Thickness(10, 0, 0, 2);
                block.Foreground = ResolveDueBrush(cleanLine, text, yellow, red);
                if (isWhereOrKeyContextSection)
                {
                    block.FontSize = 13;
                    block.LineHeight = 20;
                }
            }
            else
            {
                block.Text = cleanLine;
                block.Margin = new Thickness(0, 0, 0, 2);
                block.Foreground = text;
                if (isWhereOrKeyContextSection)
                {
                    block.FontSize = 13;
                    block.LineHeight = 20;
                }
            }

            sectionStack.Children.Add(block);
        }

        FlushSection();
        if (panel.Children.Count == 0)
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = markdown,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                LineHeight = 19,
                Foreground = text
            });
        }

        return panel;
    }

    private static string FormatSuggestedStepLine(string line)
    {
        var formatted = line.Trim();
        formatted = formatted.Replace("（Active:", "\n（Active:", StringComparison.Ordinal);
        formatted = formatted.Replace(" (Active:", "\n(Active:", StringComparison.Ordinal);
        formatted = formatted.Replace("）で、", "）\n- ", StringComparison.Ordinal);
        formatted = formatted.Replace(")で、", ")\n- ", StringComparison.Ordinal);
        return formatted;
    }

    private static string CleanMarkdownLine(string line)
    {
        var cleaned = line.Replace("**", "", StringComparison.Ordinal);
        cleaned = cleaned.Replace("__", "", StringComparison.Ordinal);
        cleaned = cleaned.Replace("`", "", StringComparison.Ordinal);
        return cleaned;
    }

    private static int GetDuePriority(string line)
    {
        var lower = line.ToLowerInvariant();
        if (lower.Contains("due: today") || lower.Contains("overdue"))
            return 2;
        if (lower.Contains("due: in ") || lower.Contains("due: tomorrow"))
            return 1;
        return 0;
    }

    private static System.Windows.Media.Brush ResolveDueBrush(
        string line,
        System.Windows.Media.Brush normal,
        System.Windows.Media.Brush yellow,
        System.Windows.Media.Brush _red)
    {
        return GetDuePriority(line) >= 1 ? yellow : normal;
    }

    // ===== What's Next =====

    private const string WhatsNextSystemPrompt = """
        You are a productivity assistant for a professional managing multiple parallel projects.
        Your job is to suggest the 3-5 most impactful actions they should take right now.

        ## Output rules
        - Return a JSON array of 3-5 suggestions, ordered by priority (highest first).
        - Each suggestion object:
          {
            "project": "exact project name",
            "action": "concise action (imperative, max 10 words)",
            "reason": "why this matters now (1-2 sentences)",
            "target_file": "relative file path if applicable, or null",
            "category": "task|focus|decision|commit|tension|review"
          }
        - Output ONLY the JSON array. No explanation, no markdown fences.

        ## Priority rules (in order)
        1. Overdue tasks - any task past its due date is highest priority
        2. Today-due tasks - tasks due today
        3. Stale focus - current_focus.md not updated in 7+ days with recent task activity
        4. Uncommitted changes - repos with changes that should be committed
        5. Unrecorded decisions - focus file mentions conclusions/choices without matching decision log
        6. Unresolved tensions - open items in open_issues.md
        7. Upcoming tasks (1-2 days) - tasks due soon

        ## Category mapping
        - "task": Complete an overdue or due-today Asana task
        - "focus": Update current_focus.md (stale or missing)
        - "decision": Record a decision in decision_log
        - "commit": Commit or review uncommitted changes
        - "tension": Address an item in open_issues.md
        - "review": Review or update project_summary.md

        ## Tone
        - Action-oriented, concise
        - Focus on outcomes ("Ship the migration script") not process ("Review the task list")
        - Include specific task/file names when available
        """;

    private sealed class WhatsNextSuggestion
    {
        public string Project { get; set; } = "";
        public string Action { get; set; } = "";
        public string Reason { get; set; } = "";
        public string? TargetFile { get; set; }
        public string Category { get; set; } = "";
    }

    private async void OnWhatsNextClickAsync(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsAiEnabled) return;

        var projects = ViewModel.Projects.Select(c => c.Info).ToList();
        if (projects.Count == 0)
        {
            System.Windows.MessageBox.Show(
                "No projects found. Please wait for the dashboard to load.",
                "What's Next",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        using var cts = new System.Threading.CancellationTokenSource();
        var loadingWindow = BuildWhatsNextLoadingWindow(cts);
        loadingWindow.Show();

        try
        {
            var focusPreviews = await CollectFocusPreviewsAsync(projects, cts.Token);
            var tasks = ViewModel.GetTopTasksForAi(30);
            var tasksDetail = await CollectAsanaTasksDetailAsync(projects, cts.Token);
            var userPrompt = BuildWhatsNextUserPrompt(projects, tasks, focusPreviews, tasksDetail);

            var language = _configService.LoadSettings().LlmLanguage;
            var systemPrompt = language.Equals("English", StringComparison.OrdinalIgnoreCase)
                ? WhatsNextSystemPrompt
                : WhatsNextSystemPrompt + $"\n\nRespond entirely in {language}.";

            var response = await _llmClientService.ChatCompletionAsync(
                systemPrompt, userPrompt, cts.Token);

            var suggestions = ParseWhatsNextResponse(response);

            if (loadingWindow.IsVisible) loadingWindow.Close();
            ShowWhatsNextResultsDialog(suggestions, response);
        }
        catch (OperationCanceledException)
        {
            // ユーザーキャンセル
        }
        catch (InvalidOperationException ex)
        {
            if (loadingWindow.IsVisible) loadingWindow.Close();
            System.Windows.MessageBox.Show(
                $"AI features error: {ex.Message}\n\nPlease check Settings > LLM API.",
                "What's Next - Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            if (loadingWindow.IsVisible) loadingWindow.Close();
            System.Windows.MessageBox.Show(
                $"Failed to get suggestions: {ex.Message}",
                "What's Next - Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private async Task<Dictionary<string, string>> CollectFocusPreviewsAsync(
        List<ProjectInfo> projects, System.Threading.CancellationToken ct)
    {
        var result = new Dictionary<string, string>();
        foreach (var proj in projects)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(proj.FocusFile) || !File.Exists(proj.FocusFile))
            {
                result[proj.Name] = "(no focus file)";
                continue;
            }
            try
            {
                var (content, _) = await _fileEncodingService.ReadFileAsync(proj.FocusFile, ct);
                var preview = content.Length > 500 ? content[..500] : content;
                result[proj.Name] = preview.Replace("\r\n", "\n").Trim();
            }
            catch
            {
                result[proj.Name] = "(error reading focus file)";
            }
        }
        return result;
    }

    /// <summary>
    /// プロジェクトごとに tasks.md を解析し、InProgress タスク (+ description) を返す。
    /// key = project.Name, value = 整形済みテキスト
    /// </summary>
    private async Task<Dictionary<string, string>> CollectAsanaTasksDetailAsync(
        List<ProjectInfo> projects, System.Threading.CancellationToken ct)
    {
        var result = new Dictionary<string, string>();
        foreach (var proj in projects)
        {
            ct.ThrowIfCancellationRequested();
            var obsidianNotes = Path.Combine(proj.AiContextPath, "obsidian_notes");
            var sb = new StringBuilder();

            // root tasks.md
            var rootTasksPath = Path.Combine(obsidianNotes, "tasks.md");
            if (File.Exists(rootTasksPath))
                AppendParsedInProgress(rootTasksPath, null, sb);

            // workstream tasks.md
            foreach (var ws in proj.Workstreams.Where(w => !w.IsClosed))
            {
                var wsTasksPath = Path.Combine(obsidianNotes, "workstreams", ws.Id, "tasks.md");
                if (File.Exists(wsTasksPath))
                    AppendParsedInProgress(wsTasksPath, string.IsNullOrWhiteSpace(ws.Label) ? ws.Id : ws.Label, sb);
            }

            var text = sb.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(text))
                result[proj.Name] = text;
        }
        return await Task.FromResult(result);
    }

    private void AppendParsedInProgress(string tasksPath, string? workstreamLabel, StringBuilder sb)
    {
        try
        {
            var parsed = _asanaTaskParser.ParseFile(tasksPath);
            if (parsed.InProgress.Count == 0) return;

            var header = workstreamLabel != null ? $"In Progress [{workstreamLabel}]:" : "In Progress:";
            sb.AppendLine(header);
            foreach (var t in parsed.InProgress)
            {
                var due = t.DueDate != null ? $"  [due: {t.DueDate}]" : "";
                sb.AppendLine($"  - {t.Title}{due}");
                if (!string.IsNullOrWhiteSpace(t.Description))
                    sb.AppendLine($"    (notes: {t.Description})");
            }
        }
        catch
        {
            // best effort
        }
    }

    private static string BuildWhatsNextUserPrompt(
        List<ProjectInfo> projects,
        List<TodayQueueTask> tasks,
        Dictionary<string, string> focusPreviews,
        Dictionary<string, string> tasksDetail)
    {
        var sb = new StringBuilder();
        var today = DateTime.Today;
        sb.AppendLine("## Date");
        sb.AppendLine($"{today:yyyy-MM-dd} ({today:dddd})");
        sb.AppendLine();

        sb.AppendLine("## Today Queue (sorted by priority)");
        if (tasks.Count == 0)
            sb.AppendLine("(no tasks)");
        else
            foreach (var t in tasks)
                sb.AppendLine($"- [{t.ProjectShortName}] {t.DisplayMainTitle} ({t.DueLabel})");
        sb.AppendLine();

        sb.AppendLine("## Project Signals");
        sb.AppendLine();
        foreach (var proj in projects)
        {
            var openIssuesPath = Path.Combine(proj.AiContextContentPath, "open_issues.md");
            int openIssuesCount = 0;
            bool openIssuesExists = File.Exists(openIssuesPath);
            if (openIssuesExists)
            {
                try
                {
                    openIssuesCount = File.ReadAllLines(openIssuesPath)
                        .Count(l => l.TrimStart().StartsWith("- ") || l.TrimStart().StartsWith("* "));
                }
                catch { }
            }

            var latestDecision = proj.DecisionLogDates.Count > 0
                ? proj.DecisionLogDates.Max().ToString("yyyy-MM-dd")
                : "none";

            var focusStale = proj.FocusAge.HasValue && proj.FocusAge > 7 ? " (stale)" : "";
            var uncommittedCount = proj.UncommittedRepoPaths.Count;

            sb.AppendLine($"### {proj.Name}");
            sb.AppendLine($"- Focus age: {(proj.FocusAge.HasValue ? $"{proj.FocusAge}d{focusStale}" : "missing")}");
            sb.AppendLine($"- Uncommitted repos: {uncommittedCount}");
            if (openIssuesExists)
                sb.AppendLine($"- Open issues: yes ({openIssuesCount} items)");
            else
                sb.AppendLine("- Open issues: no");
            sb.AppendLine($"- Recent decisions: {latestDecision}");
            if (focusPreviews.TryGetValue(proj.Name, out var preview) && !string.IsNullOrWhiteSpace(preview))
                sb.AppendLine($"- Focus preview: {preview}");
            if (tasksDetail.TryGetValue(proj.Name, out var detail) && !string.IsNullOrWhiteSpace(detail))
            {
                sb.AppendLine($"- Active tasks (from tasks.md):");
                foreach (var line in detail.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    sb.AppendLine($"  {line}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Instruction");
        sb.AppendLine("Suggest 3-5 highest-priority actions based on the data above.");
        sb.AppendLine("Return JSON array only.");

        return sb.ToString();
    }

    private static List<WhatsNextSuggestion> ParseWhatsNextResponse(string response)
    {
        var json = response.Trim();
        if (json.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
            json = json[7..];
        else if (json.StartsWith("```"))
            json = json[3..];
        if (json.EndsWith("```"))
            json = json[..^3];
        json = json.Trim();

        try
        {
            var doc = JsonDocument.Parse(json);
            var suggestions = new List<WhatsNextSuggestion>();
            foreach (var elem in doc.RootElement.EnumerateArray())
            {
                suggestions.Add(new WhatsNextSuggestion
                {
                    Project = elem.TryGetProperty("project", out var p) ? p.GetString() ?? "" : "",
                    Action = elem.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "",
                    Reason = elem.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "",
                    TargetFile = elem.TryGetProperty("target_file", out var tf) && tf.ValueKind != JsonValueKind.Null
                        ? tf.GetString() : null,
                    Category = elem.TryGetProperty("category", out var c) ? c.GetString() ?? "" : "",
                });
            }
            return suggestions;
        }
        catch
        {
            return [];
        }
    }

    private Window BuildWhatsNextLoadingWindow(System.Threading.CancellationTokenSource cts)
    {
        var appResources = Application.Current.Resources;
        var surface = (System.Windows.Media.Brush)appResources["AppSurface0"];
        var surface1 = (System.Windows.Media.Brush)appResources["AppSurface1"];
        var surface2 = (System.Windows.Media.Brush)appResources["AppSurface2"];
        var text = (System.Windows.Media.Brush)appResources["AppText"];
        var subtext = (System.Windows.Media.Brush)appResources["AppSubtext0"];

        var titleBar = new Grid { Background = surface1, Height = 38 };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var titleIcon = new System.Windows.Controls.TextBlock
        {
            Text = "✨",
            FontSize = 13,
            Margin = new Thickness(12, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleIcon, 0);

        var titleText = new System.Windows.Controls.TextBlock
        {
            Text = "What's Next",
            Foreground = text,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleText, 1);

        titleBar.Children.Add(titleIcon);
        titleBar.Children.Add(titleText);

        var loadingText = new System.Windows.Controls.TextBlock
        {
            Text = "Analyzing your projects...",
            Foreground = subtext,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 14),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };

        var cancelBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Cancel",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            MinWidth = 100,
            Height = 32,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };

        var content = new StackPanel
        {
            Margin = new Thickness(24, 18, 24, 18),
            Children = { loadingText, cancelBtn }
        };

        var root = new Grid { Background = surface };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(content, 1);
        root.Children.Add(titleBar);
        root.Children.Add(content);

        var owner = Window.GetWindow(this);
        var win = new Window
        {
            Title = "",
            Owner = owner,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            Width = 340,
            Background = surface,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            Content = root
        };

        cancelBtn.Click += (_, _) => { cts.Cancel(); win.Close(); };
        titleBar.MouseLeftButtonDown += (_, ev) =>
        {
            if (ev.LeftButton == MouseButtonState.Pressed) win.DragMove();
        };

        return win;
    }

    private void ShowWhatsNextResultsDialog(List<WhatsNextSuggestion> suggestions, string rawResponse)
    {
        var mw = Window.GetWindow(this) as MainWindow;

        var appResources = Application.Current.Resources;
        var surface = (System.Windows.Media.Brush)appResources["AppSurface0"];
        var surface1 = (System.Windows.Media.Brush)appResources["AppSurface1"];
        var surface2 = (System.Windows.Media.Brush)appResources["AppSurface2"];
        var (text, subtext) = ResolveDashboardMarkdownTextBrushes(surface, appResources);
        var accent = appResources.Contains("AppBlue")
            ? (System.Windows.Media.Brush)appResources["AppBlue"]
            : text;

        var titleBar = new Grid { Background = surface1, Height = 38 };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleIcon = new System.Windows.Controls.TextBlock
        {
            Text = "✨",
            FontSize = 13,
            Margin = new Thickness(12, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleIcon, 0);

        var titleText = new System.Windows.Controls.TextBlock
        {
            Text = "What's Next",
            Foreground = text,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleText, 1);

        var closeButton = new System.Windows.Controls.Button
        {
            Content = "✕",
            Width = 34,
            Height = 26,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = subtext,
            FontSize = 13
        };
        Grid.SetColumn(closeButton, 2);

        titleBar.Children.Add(titleIcon);
        titleBar.Children.Add(titleText);
        titleBar.Children.Add(closeButton);

        var suggestionsPanel = new StackPanel();
        Window? dialogWindow = null;

        if (suggestions.Count == 0)
        {
            var fallback = new System.Windows.Controls.TextBox
            {
                Text = string.IsNullOrWhiteSpace(rawResponse) ? "(No suggestions)" : rawResponse,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                Background = surface1,
                Foreground = text,
                BorderBrush = surface2,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8),
                FontSize = 12,
                Margin = new Thickness(16, 12, 16, 8),
                MaxHeight = 400
            };
            suggestionsPanel.Children.Add(fallback);
        }
        else
        {
            for (int i = 0; i < suggestions.Count; i++)
            {
                var suggestion = suggestions[i];

                if (i > 0)
                    suggestionsPanel.Children.Add(new Border
                    {
                        Height = 1,
                        Background = surface2,
                        Margin = new Thickness(16, 0, 16, 0)
                    });

                var cardGrid = new Grid { Margin = new Thickness(16, 10, 16, 10) };
                cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var numBlock = new System.Windows.Controls.TextBlock
                {
                    Text = $"{i + 1}.",
                    Foreground = subtext,
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2, 10, 0),
                    MinWidth = 22
                };
                Grid.SetColumn(numBlock, 0);

                var bodyPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Top };

                var projectHeader = new System.Windows.Controls.TextBlock
                {
                    Text = $"[{suggestion.Project}]",
                    Foreground = accent,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };

                var actionBlock = new System.Windows.Controls.TextBlock
                {
                    Text = suggestion.Action,
                    Foreground = text,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                };

                var reasonBlock = new System.Windows.Controls.TextBlock
                {
                    Text = suggestion.Reason,
                    Foreground = subtext,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 3, 0, 0)
                };

                bodyPanel.Children.Add(projectHeader);
                bodyPanel.Children.Add(actionBlock);
                bodyPanel.Children.Add(reasonBlock);
                Grid.SetColumn(bodyPanel, 1);

                var openBtn = new Wpf.Ui.Controls.Button
                {
                    Content = "Open ▶",
                    Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
                    FontSize = 11,
                    Height = 28,
                    Padding = new Thickness(10, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(10, 0, 0, 0)
                };
                Grid.SetColumn(openBtn, 2);

                var capturedSuggestion = suggestion;
                openBtn.Click += (_, _) =>
                {
                    NavigateToSuggestionDirect(mw, capturedSuggestion);
                    mw?.Activate();
                };

                cardGrid.Children.Add(numBlock);
                cardGrid.Children.Add(bodyPanel);
                cardGrid.Children.Add(openBtn);
                suggestionsPanel.Children.Add(cardGrid);
            }
        }

        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 480,
            Content = suggestionsPanel
        };

        var copyBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Copy",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            MinWidth = 90,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 0)
        };

        var logBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Debug Log",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            Height = 32,
            Padding = new Thickness(10, 0, 10, 0),
            ToolTip = "Show LLM prompt log"
        };

        var closeFooterBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Close",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            MinWidth = 90,
            Height = 32
        };

        var footerGrid = new Grid { Margin = new Thickness(16, 8, 16, 12) };
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(logBtn, 0);

        var rightBtns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Children = { copyBtn, closeFooterBtn }
        };
        Grid.SetColumn(rightBtns, 2);

        footerGrid.Children.Add(logBtn);
        footerGrid.Children.Add(rightBtns);

        var root = new Grid { Background = surface };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(scrollViewer, 1);
        Grid.SetRow(footerGrid, 2);
        root.Children.Add(titleBar);
        root.Children.Add(scrollViewer);
        root.Children.Add(footerGrid);

        var owner = Window.GetWindow(this);
        dialogWindow = new Window
        {
            Title = "",
            Owner = owner,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            MinWidth = 680,
            Width = 680,
            Background = surface,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            Content = root
        };

        copyBtn.Click += (_, _) =>
        {
            var clipText = BuildWhatsNextClipboardText(suggestions, rawResponse);
            System.Windows.Clipboard.SetText(clipText);
        };

        logBtn.Click += (_, _) =>
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== SYSTEM PROMPT ===");
            sb.AppendLine(_llmClientService.LastSystemPrompt);
            sb.AppendLine();
            sb.AppendLine("=== USER PROMPT ===");
            sb.AppendLine(_llmClientService.LastUserPrompt);
            sb.AppendLine();
            sb.AppendLine("=== RESPONSE ===");
            sb.AppendLine(_llmClientService.LastResponse);
            ShowWhatsNextLogDialog("LLM Debug Log", sb.ToString());
        };

        closeButton.Click += (_, _) => dialogWindow.Close();
        closeFooterBtn.Click += (_, _) => dialogWindow.Close();
        titleBar.MouseLeftButtonDown += (_, ev) =>
        {
            if (ev.LeftButton == MouseButtonState.Pressed) dialogWindow.DragMove();
        };

        dialogWindow.Show();
    }

    private void ShowWhatsNextLogDialog(string title, string message)
    {
        var appResources = Application.Current.Resources;
        var surface  = (System.Windows.Media.Brush)appResources["AppSurface0"];
        var surface1 = (System.Windows.Media.Brush)appResources["AppSurface1"];
        var surface2 = (System.Windows.Media.Brush)appResources["AppSurface2"];
        var text     = (System.Windows.Media.Brush)appResources["AppText"];
        var subtext  = (System.Windows.Media.Brush)appResources["AppSubtext0"];

        var titleBar = new Grid { Background = surface1, Height = 38 };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleIcon = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = Wpf.Ui.Controls.SymbolRegular.Bug24,
            FontSize = 16,
            Foreground = text,
            Margin = new Thickness(12, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleIcon, 0);

        var titleText = new System.Windows.Controls.TextBlock
        {
            Text = title,
            Foreground = text,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleText, 1);

        var closeBtn = new System.Windows.Controls.Button
        {
            Content = "✕", Width = 34, Height = 26,
            Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0), Foreground = subtext, FontSize = 13
        };
        Grid.SetColumn(closeBtn, 2);

        titleBar.Children.Add(titleIcon);
        titleBar.Children.Add(titleText);
        titleBar.Children.Add(closeBtn);

        var textBox = new System.Windows.Controls.TextBox
        {
            Text = message,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = surface1,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            FontFamily = new System.Windows.Media.FontFamily("Consolas, 'Courier New', monospace"),
            FontSize = 12,
            MinHeight = 80,
            MaxHeight = 400
        };

        var okButton = new Wpf.Ui.Controls.Button
        {
            Content = "OK",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            MinWidth = 80, Height = 32,
            IsDefault = true, IsCancel = true
        };

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 12),
            Children = { okButton }
        };

        var contentPanel = new StackPanel { Margin = new Thickness(16, 12, 16, 8) };
        contentPanel.Children.Add(textBox);

        var root = new Grid { Background = surface };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(contentPanel, 1);
        Grid.SetRow(footer, 2);
        root.Children.Add(titleBar);
        root.Children.Add(contentPanel);
        root.Children.Add(footer);

        var owner = Window.GetWindow(this);
        var logWindow = new Window
        {
            Title = "",
            Owner = owner,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            MinWidth = 680,
            Width = 680,
            Background = surface,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            Content = root
        };

        closeBtn.Click += (_, _) => logWindow.Close();
        okButton.Click += (_, _) => logWindow.Close();
        titleBar.MouseLeftButtonDown += (_, ev) =>
        {
            if (ev.LeftButton == MouseButtonState.Pressed) logWindow.DragMove();
        };

        _ = logWindow.ShowDialog();
    }

    private void NavigateToSuggestionDirect(MainWindow? mw, WhatsNextSuggestion suggestion)
    {
        if (mw == null) return;

        var card = ViewModel.Projects.FirstOrDefault(c =>
            string.Equals(c.Info.Name, suggestion.Project, StringComparison.OrdinalIgnoreCase));
        if (card == null) return;

        if (!string.IsNullOrWhiteSpace(suggestion.TargetFile))
        {
            var fullPath = ResolveWhatsNextTargetFile(card.Info, suggestion.TargetFile);
            if (!string.IsNullOrWhiteSpace(fullPath) && File.Exists(fullPath))
            {
                mw.NavigateToEditorAndOpenFile(card.Info, fullPath);
                return;
            }
        }

        mw.NavigateToEditor(card.Info);
    }

    private static string? ResolveWhatsNextTargetFile(ProjectInfo project, string targetFile)
    {
        if (Path.IsPathRooted(targetFile))
            return targetFile;

        var candidate1 = Path.Combine(project.AiContextContentPath, targetFile);
        if (File.Exists(candidate1)) return candidate1;

        var candidate2 = Path.Combine(project.Path, targetFile);
        if (File.Exists(candidate2)) return candidate2;

        return null;
    }

    private static string BuildWhatsNextClipboardText(List<WhatsNextSuggestion> suggestions, string rawResponse)
    {
        if (suggestions.Count == 0)
            return rawResponse;

        var sb = new StringBuilder();
        sb.AppendLine("What's Next:");
        for (int i = 0; i < suggestions.Count; i++)
        {
            var s = suggestions[i];
            sb.AppendLine($"{i + 1}. [{s.Project}] {s.Action}");
            if (!string.IsNullOrWhiteSpace(s.Reason))
                sb.AppendLine($"   {s.Reason}");
        }
        return sb.ToString().TrimEnd();
    }

    // ===== Plan My Day =====

    private const string PlanMyDaySystemPrompt = """
        You are a daily planning assistant for a professional managing multiple parallel projects.
        Your job is to create a time-blocked plan for today that maximizes productivity and prevents things from falling through the cracks.

        ## Output rules
        - Return a JSON object with this structure:
          {
            "blocks": [
              {
                "period": "morning" | "afternoon" | "late_afternoon",
                "items": [
                  {
                    "project": "exact project name",
                    "action": "concise action (imperative, max 10 words)",
                    "reason": "why today AND why this time slot (1-2 sentences)",
                    "target_file": "relative file path if applicable, or null",
                    "category": "task|focus|decision|commit|tension|meeting_prep|review"
                  }
                ]
              }
            ],
            "overall_advice": "one sentence of overall advice for the day, or null"
          }
        - Output ONLY the JSON object. No explanation, no markdown fences.
        - Generate 4-7 items total across all time blocks.
        - Every time block must have at least 1 item.

        ## Time block principles
        - morning: Deep work, critical decisions, creative tasks. Brain is freshest.
        - afternoon: Meetings, collaborative work, reviews. Lower cognitive demand tasks.
        - late_afternoon: Wrap-up tasks — commits, focus updates, quick maintenance. Closing the loop.

        ## Schedule hint handling
        - If the user provides meeting times, plan around them:
          - Place meeting prep BEFORE the meeting time
          - Place meeting follow-up (notes, decisions) AFTER
          - Protect deep work blocks from meeting interruption
        - If no schedule hint, assume a standard workday.

        ## Planning rules (in priority order)
        1. Overdue tasks — must be in morning block
        2. Today-due tasks — morning or early afternoon
        3. Meeting prep/follow-up — anchored to meeting times
        4. Stale focus (7+ days) — afternoon or late_afternoon
        5. Uncommitted changes — late_afternoon (end-of-day cleanup)
        6. Unresolved tensions (5+ days old) — morning (needs thinking)
        7. Upcoming tasks (1-2 days) — afternoon if time permits
        8. Decision recording — late_afternoon

        ## Day-of-week awareness
        - Monday: Include a "review weekly priorities" item if not already planned
        - Friday: Include a "review and close the week" item (commit, update focus, resolve quick tensions)

        ## Anti-patterns to avoid
        - Do not schedule more than 3 projects in morning (focus fragmentation)
        - Do not put deep thinking tasks after 15:00
        - Do not ignore overdue items regardless of other priorities

        ## Category mapping
        - "task": Complete an Asana task
        - "focus": Update current_focus.md
        - "decision": Record a decision in decision_log
        - "commit": Commit or review uncommitted changes
        - "tension": Address an item in open_issues.md
        - "meeting_prep": Prepare for an upcoming meeting
        - "review": Review status, summary, or weekly priorities

        ## Tone
        - Action-oriented: "Ship the migration script" not "Consider reviewing the task list"
        - Time-aware: explain WHY this time slot specifically
        - Encouraging but realistic: acknowledge heavy days, suggest what to defer if overloaded
        """;

    private sealed class DailyPlan
    {
        public List<DayPeriodBlock> Blocks { get; set; } = [];
        public string? OverallAdvice { get; set; }
    }

    private sealed class DayPeriodBlock
    {
        public string Period { get; set; } = "";   // "morning" | "afternoon" | "late_afternoon"
        public List<DayPlanItem> Items { get; set; } = [];
    }

    private sealed class DayPlanItem
    {
        public string Project { get; set; } = "";
        public string Action { get; set; } = "";
        public string Reason { get; set; } = "";
        public string? TargetFile { get; set; }
        public string Category { get; set; } = "";  // task|focus|decision|commit|tension|meeting_prep|review
    }

    private async void OnTimeBlockClickAsync(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsAiEnabled) return;
        await RunPlanMyDayAsync();
    }

    private async Task RunPlanMyDayAsync()
    {
        var scheduleHint = await ShowScheduleHintDialogAsync();
        if (scheduleHint == null) return;  // user cancelled

        using var cts = new System.Threading.CancellationTokenSource();
        var loadingWindow = BuildTimeBlockLoadingWindow(cts);
        loadingWindow.Show();

        var userPrompt = "";
        try
        {
            var (tasks, focusPreviews, projects, yesterdayStandup, tasksDetail) = await CollectPlanMyDayDataAsync(cts.Token);

            userPrompt = BuildPlanMyDayUserPrompt(tasks, focusPreviews, projects, scheduleHint, yesterdayStandup, tasksDetail);
            var response = await _llmClientService.ChatCompletionAsync(
                PlanMyDaySystemPrompt, userPrompt, cts.Token);

            var plan = ParseDailyPlanResponse(response);

            if (loadingWindow.IsVisible) loadingWindow.Close();
            ShowPlanMyDayResultDialog(plan, userPrompt, response);
        }
        catch (OperationCanceledException)
        {
            // user cancelled
        }
        catch (InvalidOperationException ex)
        {
            if (loadingWindow.IsVisible) loadingWindow.Close();
            System.Windows.MessageBox.Show(
                $"AI features error: {ex.Message}\n\nPlease check Settings > LLM API.",
                "Plan My Day - Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            if (loadingWindow.IsVisible) loadingWindow.Close();
            System.Windows.MessageBox.Show(
                $"Failed to generate plan: {ex.Message}",
                "Plan My Day - Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private async Task<(List<TodayQueueTask> Tasks, Dictionary<string, string> FocusPreviews, List<ProjectInfo> Projects, string YesterdayStandup, Dictionary<string, string> TasksDetail)> CollectPlanMyDayDataAsync(
        System.Threading.CancellationToken ct)
    {
        var tasks = ViewModel.GetTopTasksForAi(30);
        var projects = ViewModel.Projects.Select(c => c.Info).ToList();
        var focusPreviews = await CollectFocusPreviewsAsync(projects, ct);
        var yesterdayStandup = await ReadYesterdayStandupAsync(ct);
        var tasksDetail = await CollectAsanaTasksDetailAsync(projects, ct);
        return (tasks, focusPreviews, projects, yesterdayStandup, tasksDetail);
    }

    private async Task<string> ReadYesterdayStandupAsync(System.Threading.CancellationToken ct)
    {
        var settings = _configService.LoadSettings();
        if (string.IsNullOrWhiteSpace(settings.ObsidianVaultRoot))
            return "(no standup available)";

        var yesterday = DateTime.Today.AddDays(-1);
        var path = Path.Combine(settings.ObsidianVaultRoot, "standup", $"{yesterday:yyyyMMdd}_standup.md");
        if (!File.Exists(path))
            return "(no standup available)";

        try
        {
            var (content, _) = await _fileEncodingService.ReadFileAsync(path, ct);
            return content.Length > 1000 ? content[..1000] : content;
        }
        catch
        {
            return "(no standup available)";
        }
    }

    private static string BuildPlanMyDayUserPrompt(
        List<TodayQueueTask> tasks,
        Dictionary<string, string> focusPreviews,
        List<ProjectInfo> projects,
        string scheduleHint,
        string yesterdayStandup,
        Dictionary<string, string> tasksDetail)
    {
        var today = DateTime.Today;
        var sb = new StringBuilder();

        sb.AppendLine("## Date");
        sb.AppendLine($"{today:yyyy-MM-dd} ({today:dddd})");
        sb.AppendLine();

        sb.AppendLine("## Schedule hints");
        sb.AppendLine(string.IsNullOrWhiteSpace(scheduleHint) ? "(none)" : scheduleHint);
        sb.AppendLine();

        sb.AppendLine("## Yesterday's standup (for continuity)");
        sb.AppendLine(yesterdayStandup);
        sb.AppendLine();

        sb.AppendLine("## Today Queue (sorted by priority)");
        if (tasks.Count == 0)
            sb.AppendLine("(no tasks)");
        else
            foreach (var t in tasks)
                sb.AppendLine($"- [{t.ProjectShortName}] {t.DisplayMainTitle} ({t.DueLabel})");
        sb.AppendLine();

        sb.AppendLine("## Project Signals");
        sb.AppendLine();
        foreach (var proj in projects)
        {
            var openIssuesPath = Path.Combine(proj.AiContextContentPath, "open_issues.md");
            int openIssuesCount = 0;
            bool openIssuesExists = File.Exists(openIssuesPath);
            if (openIssuesExists)
            {
                try
                {
                    openIssuesCount = File.ReadAllLines(openIssuesPath)
                        .Count(l => l.TrimStart().StartsWith("- ") || l.TrimStart().StartsWith("* "));
                }
                catch { }
            }

            var latestDecision = proj.DecisionLogDates.Count > 0
                ? proj.DecisionLogDates.Max().ToString("yyyy-MM-dd")
                : "none";

            var focusStale = proj.FocusAge.HasValue && proj.FocusAge > 7 ? " (stale)" : "";
            var uncommittedCount = proj.UncommittedRepoPaths.Count;

            sb.AppendLine($"### {proj.Name}");
            sb.AppendLine($"- Focus age: {(proj.FocusAge.HasValue ? $"{proj.FocusAge}d{focusStale}" : "missing")}");
            sb.AppendLine($"- Uncommitted repos: {uncommittedCount}");
            if (openIssuesExists)
                sb.AppendLine($"- Open issues: yes ({openIssuesCount} items)");
            else
                sb.AppendLine("- Open issues: no");
            sb.AppendLine($"- Recent decisions: {latestDecision}");
            sb.AppendLine($"- Active workstreams: {proj.Workstreams.Count}");
            if (focusPreviews.TryGetValue(proj.Name, out var preview) && !string.IsNullOrWhiteSpace(preview))
                sb.AppendLine($"- Focus preview: {preview}");
            if (tasksDetail.TryGetValue(proj.Name, out var detail) && !string.IsNullOrWhiteSpace(detail))
            {
                sb.AppendLine($"- Active tasks (from tasks.md):");
                foreach (var line in detail.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    sb.AppendLine($"  {line}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Instruction");
        sb.AppendLine("Create a time-blocked daily plan based on the data above.");
        sb.AppendLine("Return JSON object only.");

        return sb.ToString();
    }

    private static DailyPlan ParseDailyPlanResponse(string response)
    {
        var json = response.Trim();
        if (json.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
            json = json[7..];
        else if (json.StartsWith("```"))
            json = json[3..];
        if (json.EndsWith("```"))
            json = json[..^3];
        json = json.Trim();

        try
        {
            var doc = JsonDocument.Parse(json);
            var plan = new DailyPlan();

            if (doc.RootElement.TryGetProperty("overall_advice", out var adviceEl) &&
                adviceEl.ValueKind == JsonValueKind.String)
                plan.OverallAdvice = adviceEl.GetString();

            if (doc.RootElement.TryGetProperty("blocks", out var blocksEl) &&
                blocksEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var blockEl in blocksEl.EnumerateArray())
                {
                    var block = new DayPeriodBlock();
                    if (blockEl.TryGetProperty("period", out var periodEl))
                        block.Period = periodEl.GetString() ?? "";

                    if (blockEl.TryGetProperty("items", out var itemsEl) &&
                        itemsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var itemEl in itemsEl.EnumerateArray())
                        {
                            block.Items.Add(new DayPlanItem
                            {
                                Project = itemEl.TryGetProperty("project", out var p) ? p.GetString() ?? "" : "",
                                Action = itemEl.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "",
                                Reason = itemEl.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "",
                                TargetFile = itemEl.TryGetProperty("target_file", out var tf) && tf.ValueKind == JsonValueKind.String ? tf.GetString() : null,
                                Category = itemEl.TryGetProperty("category", out var c) ? c.GetString() ?? "" : "",
                            });
                        }
                    }

                    plan.Blocks.Add(block);
                }
            }

            return plan;
        }
        catch
        {
            return new DailyPlan();
        }
    }

    private Window BuildTimeBlockLoadingWindow(System.Threading.CancellationTokenSource cts)
    {
        var appResources = Application.Current.Resources;
        var surface  = (System.Windows.Media.Brush)appResources["AppSurface0"];
        var surface1 = (System.Windows.Media.Brush)appResources["AppSurface1"];
        var surface2 = (System.Windows.Media.Brush)appResources["AppSurface2"];
        var text     = (System.Windows.Media.Brush)appResources["AppText"];
        var subtext  = (System.Windows.Media.Brush)appResources["AppSubtext0"];

        var titleBar = new Grid { Background = surface1, Height = 38 };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var titleIcon = new System.Windows.Controls.TextBlock
        {
            Text = "☀",
            FontSize = 13,
            Margin = new Thickness(12, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleIcon, 0);

        var titleText = new System.Windows.Controls.TextBlock
        {
            Text = "Plan My Day",
            Foreground = text,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleText, 1);

        titleBar.Children.Add(titleIcon);
        titleBar.Children.Add(titleText);

        var loadingText = new System.Windows.Controls.TextBlock
        {
            Text = "Planning your day...",
            Foreground = subtext,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 14),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };

        var cancelBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Cancel",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            MinWidth = 100,
            Height = 32,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };

        var content = new StackPanel
        {
            Margin = new Thickness(24, 18, 24, 18),
            Children = { loadingText, cancelBtn }
        };

        var root = new Grid { Background = surface };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(content, 1);
        root.Children.Add(titleBar);
        root.Children.Add(content);

        var owner = Window.GetWindow(this);
        var win = new Window
        {
            Title = "",
            Owner = owner,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            Width = 340,
            Background = surface,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            Content = root
        };

        cancelBtn.Click += (_, _) => { cts.Cancel(); win.Close(); };
        titleBar.MouseLeftButtonDown += (_, ev) =>
        {
            if (ev.LeftButton == MouseButtonState.Pressed) win.DragMove();
        };

        return win;
    }

    private void ShowPlanMyDayResultDialog(DailyPlan plan, string userPrompt, string rawResponse)
    {
        var appResources = Application.Current.Resources;
        var surface  = (System.Windows.Media.Brush)appResources["AppSurface0"];
        var surface1 = (System.Windows.Media.Brush)appResources["AppSurface1"];
        var surface2 = (System.Windows.Media.Brush)appResources["AppSurface2"];
        var (text, subtext) = ResolveDashboardMarkdownTextBrushes(surface, appResources);
        var accent   = appResources.Contains("AppPeach")
            ? (System.Windows.Media.Brush)appResources["AppPeach"]
            : text;

        var mw = Window.GetWindow(this) as MainWindow;

        // Title bar
        var titleBar = new Grid { Background = surface1, Height = 38 };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleIcon = new System.Windows.Controls.TextBlock
        {
            Text = "☀",
            FontSize = 13,
            Margin = new Thickness(12, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleIcon, 0);

        var now = DateTime.Now;
        var titleText = new System.Windows.Controls.TextBlock
        {
            Text = $"Today's Plan  —  {now:yyyy-MM-dd} ({now:dddd})",
            Foreground = text,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleText, 1);

        var closeBtnTitle = new System.Windows.Controls.Button
        {
            Content = "✕",
            Width = 34,
            Height = 26,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = subtext,
            FontSize = 13
        };
        Grid.SetColumn(closeBtnTitle, 2);

        titleBar.Children.Add(titleIcon);
        titleBar.Children.Add(titleText);
        titleBar.Children.Add(closeBtnTitle);

        // Content panel
        var contentPanel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };

        var periodOrder = new[] { "morning", "afternoon", "late_afternoon" };
        static string PeriodLabel(string period) => period switch
        {
            "morning" => "Morning (deep work)",
            "afternoon" => "Afternoon",
            "late_afternoon" => "Late afternoon (wrap-up)",
            _ => period
        };

        var allBlocks = plan.Blocks.OrderBy(b => Array.IndexOf(periodOrder, b.Period)).ToList();

        if (allBlocks.Count == 0 || allBlocks.All(b => b.Items.Count == 0))
        {
            var fallback = new System.Windows.Controls.TextBox
            {
                Text = string.IsNullOrWhiteSpace(rawResponse) ? "(No plan generated)" : rawResponse,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                Background = surface1,
                Foreground = text,
                BorderBrush = surface2,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8),
                FontSize = 12,
                Margin = new Thickness(14, 10, 14, 8),
                MaxHeight = 380
            };
            contentPanel.Children.Add(fallback);
        }
        else
        {
            foreach (var block in allBlocks)
            {
                var sectionBg = surface;

                var sectionHeader = new Border
                {
                    Background = surface2,
                    Padding = new Thickness(14, 5, 14, 5),
                    Margin = new Thickness(0, 6, 0, 0),
                    Child = new System.Windows.Controls.TextBlock
                    {
                        Text = PeriodLabel(block.Period),
                        Foreground = text,
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold
                    }
                };
                contentPanel.Children.Add(sectionHeader);

                var itemsPanel = new StackPanel { Background = sectionBg };

                for (int i = 0; i < block.Items.Count; i++)
                {
                    var item = block.Items[i];

                    var itemGrid = new Grid { Margin = new Thickness(14, 9, 14, 9) };
                    itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var categoryIcon = new Wpf.Ui.Controls.SymbolIcon
                    {
                        Symbol = GetCategorySymbol(item.Category),
                        FontSize = 16,
                        Foreground = accent,
                        Margin = new Thickness(0, 2, 10, 0),
                        VerticalAlignment = VerticalAlignment.Top
                    };
                    Grid.SetColumn(categoryIcon, 0);

                    var bodyPanel = new StackPanel();

                    var projectLabel = new System.Windows.Controls.TextBlock
                    {
                        Text = $"[{item.Project}]",
                        Foreground = subtext,
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold
                    };

                    var actionBlock = new System.Windows.Controls.TextBlock
                    {
                        Text = item.Action,
                        Foreground = text,
                        FontSize = 13,
                        FontWeight = FontWeights.SemiBold,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 1, 0, 0)
                    };

                    bodyPanel.Children.Add(projectLabel);
                    bodyPanel.Children.Add(actionBlock);

                    if (!string.IsNullOrWhiteSpace(item.Reason))
                        bodyPanel.Children.Add(new System.Windows.Controls.TextBlock
                        {
                            Text = item.Reason,
                            Foreground = subtext,
                            FontSize = 12,
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 3, 0, 0)
                        });

                    Grid.SetColumn(bodyPanel, 1);

                    var openBtn = new Wpf.Ui.Controls.Button
                    {
                        Content = "Open ▶",
                        Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
                        FontSize = 11,
                        Height = 28,
                        Padding = new Thickness(10, 0, 10, 0),
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(10, 0, 0, 0)
                    };
                    Grid.SetColumn(openBtn, 2);

                    var capturedItem = item;
                    openBtn.Click += (_, _) =>
                    {
                        NavigateToPlanItem(mw, capturedItem);
                        mw?.Activate();
                    };

                    itemGrid.Children.Add(categoryIcon);
                    itemGrid.Children.Add(bodyPanel);
                    itemGrid.Children.Add(openBtn);
                    itemsPanel.Children.Add(itemGrid);

                    if (i < block.Items.Count - 1)
                        itemsPanel.Children.Add(new Border
                        {
                            Height = 1,
                            Background = surface2,
                            Margin = new Thickness(40, 0, 14, 0)
                        });
                }

                contentPanel.Children.Add(itemsPanel);
            }

            if (!string.IsNullOrWhiteSpace(plan.OverallAdvice))
            {
                contentPanel.Children.Add(new Border
                {
                    Margin = new Thickness(14, 10, 14, 4),
                    Padding = new Thickness(12, 8, 12, 8),
                    Background = surface1,
                    BorderBrush = surface2,
                    BorderThickness = new Thickness(1),
                    Child = new System.Windows.Controls.TextBlock
                    {
                        Text = plan.OverallAdvice,
                        Foreground = subtext,
                        FontSize = 12,
                        FontStyle = FontStyles.Italic,
                        TextWrapping = TextWrapping.Wrap
                    }
                });
            }
        }

        var scrollViewer = new ScrollViewer
        {
            Content = contentPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 440
        };

        // Footer
        var debugBtn = new Wpf.Ui.Controls.Button
        {
            Content = "View Debug",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            Height = 32,
            Padding = new Thickness(10, 0, 10, 0),
            ToolTip = "Show LLM prompt/response"
        };

        var copyBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Copy",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            MinWidth = 80,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 0)
        };

        var settings = _configService.LoadSettings();
        var obsidianRoot = settings.ObsidianVaultRoot;
        var saveBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Save",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            MinWidth = 80,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 0),
            Visibility = string.IsNullOrWhiteSpace(obsidianRoot) ? Visibility.Collapsed : Visibility.Visible,
            ToolTip = "Save plan as Markdown to standup folder"
        };

        var closeBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Close",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            MinWidth = 80,
            Height = 32
        };

        var rightButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { copyBtn, saveBtn, closeBtn }
        };

        var footer = new Grid { Margin = new Thickness(16, 10, 16, 14) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(debugBtn, 0);
        Grid.SetColumn(rightButtons, 2);
        footer.Children.Add(debugBtn);
        footer.Children.Add(rightButtons);

        var root = new Grid { Background = surface };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(scrollViewer, 1);
        Grid.SetRow(footer, 2);
        root.Children.Add(titleBar);
        root.Children.Add(scrollViewer);
        root.Children.Add(footer);

        var win = new Window
        {
            Title = "",
            Owner = Window.GetWindow(this),
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize,
            WindowStyle = WindowStyle.None,
            SizeToContent = SizeToContent.Height,
            Width = 680,
            MaxHeight = 600,
            MinWidth = 460,
            MinHeight = 200,
            Background = surface,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            Content = root
        };

        System.Windows.Shell.WindowChrome.SetWindowChrome(win,
            new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(4),
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false
            });

        closeBtnTitle.Click += (_, _) => win.Close();
        closeBtn.Click += (_, _) => win.Close();
        titleBar.MouseLeftButtonDown += (_, ev) =>
        {
            if (ev.LeftButton == MouseButtonState.Pressed) win.DragMove();
        };

        copyBtn.Click += (_, _) =>
            System.Windows.Clipboard.SetText(BuildPlanMyDayClipboardText(plan));

        debugBtn.Click += (_, _) =>
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== USER PROMPT ===");
            sb.AppendLine(userPrompt);
            sb.AppendLine();
            sb.AppendLine("=== RESPONSE ===");
            sb.AppendLine(_llmClientService.LastResponse);
            ShowWhatsNextLogDialog("Plan My Day Debug Log", sb.ToString());
        };

        saveBtn.Click += (_, _) =>
        {
            try
            {
                var planPath = Path.Combine(obsidianRoot, "standup", $"{DateTime.Today:yyyy-MM-dd}_plan.md");
                _fileEncodingService.WriteFile(planPath, BuildPlanMyDayClipboardText(plan), "UTF-8BOM");
                saveBtn.Content = "Saved!";
                saveBtn.IsEnabled = false;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Failed to save: {ex.Message}",
                    "Plan My Day",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        };

        win.ShowDialog();
    }

    private static Wpf.Ui.Controls.SymbolRegular GetCategorySymbol(string category) => category switch
    {
        "task"         => Wpf.Ui.Controls.SymbolRegular.TaskListSquareLtr24,
        "focus"        => Wpf.Ui.Controls.SymbolRegular.DocumentEdit24,
        "decision"     => Wpf.Ui.Controls.SymbolRegular.Gavel24,
        "commit"       => Wpf.Ui.Controls.SymbolRegular.BranchFork24,
        "tension"      => Wpf.Ui.Controls.SymbolRegular.Important24,
        "meeting_prep" => Wpf.Ui.Controls.SymbolRegular.PeopleAudience24,
        "review"       => Wpf.Ui.Controls.SymbolRegular.Eye24,
        _              => Wpf.Ui.Controls.SymbolRegular.AlertBadge24,
    };

    private void NavigateToPlanItem(MainWindow? mw, DayPlanItem item)
    {
        if (mw == null) return;

        var card = ViewModel.Projects.FirstOrDefault(c =>
            string.Equals(c.Info.Name, item.Project, StringComparison.OrdinalIgnoreCase));
        if (card == null) return;

        if (!string.IsNullOrWhiteSpace(item.TargetFile))
        {
            var fullPath = ResolveWhatsNextTargetFile(card.Info, item.TargetFile);
            if (!string.IsNullOrWhiteSpace(fullPath) && File.Exists(fullPath))
            {
                mw.NavigateToEditorAndOpenFile(card.Info, fullPath);
                return;
            }
        }

        mw.NavigateToEditor(card.Info);
    }

    private static string BuildPlanMyDayClipboardText(DailyPlan plan)
    {
        var today = DateTime.Today;
        var sb = new StringBuilder();
        sb.AppendLine($"# Today's Plan — {today:yyyy-MM-dd} ({today:dddd})");
        sb.AppendLine();

        static string PeriodLabel(string period) => period switch
        {
            "morning" => "Morning (deep work)",
            "afternoon" => "Afternoon",
            "late_afternoon" => "Late afternoon (wrap-up)",
            _ => period
        };

        foreach (var block in plan.Blocks)
        {
            sb.AppendLine($"## {PeriodLabel(block.Period)}");
            sb.AppendLine();
            foreach (var item in block.Items)
            {
                sb.AppendLine($"- [{item.Project}] {item.Action}");
                if (!string.IsNullOrWhiteSpace(item.Reason))
                    sb.AppendLine($"  {item.Reason}");
            }
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(plan.OverallAdvice))
        {
            sb.AppendLine($"> {plan.OverallAdvice}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private (System.Windows.Media.Brush text, System.Windows.Media.Brush subtext) ResolveDashboardMarkdownTextBrushes(
        System.Windows.Media.Brush surfaceBrush,
        ResourceDictionary resources)
    {
        var fallbackText = (System.Windows.Media.Brush)resources["AppText"];
        var fallbackSubtext = (System.Windows.Media.Brush)resources["AppSubtext0"];

        var settings = _configService.LoadSettings();
        if (!TryParseHexColor(settings.MarkdownRenderTextColor, out var markdownColor))
            return (fallbackText, fallbackSubtext);

        var text = FreezeBrush(new SolidColorBrush(markdownColor));

        var backgroundColor = surfaceBrush is SolidColorBrush sb
            ? sb.Color
            : Colors.Black;
        var subtextColor = BuildRelativeSubtextColor(markdownColor, backgroundColor);
        var subtext = FreezeBrush(new SolidColorBrush(subtextColor));
        return (text, subtext);
    }

    private static SolidColorBrush FreezeBrush(SolidColorBrush brush)
    {
        if (brush.CanFreeze) brush.Freeze();
        return brush;
    }

    private static bool TryParseHexColor(string? hex, out System.Windows.Media.Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;

        var raw = hex.Trim();
        if (raw.StartsWith('#')) raw = raw[1..];
        if (raw.Length != 6) return false;

        if (!byte.TryParse(raw[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)) return false;
        if (!byte.TryParse(raw.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)) return false;
        if (!byte.TryParse(raw.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)) return false;

        color = System.Windows.Media.Color.FromRgb(r, g, b);
        return true;
    }

    private static System.Windows.Media.Color BuildRelativeSubtextColor(
        System.Windows.Media.Color baseTextColor,
        System.Windows.Media.Color backgroundColor)
    {
        const double initialBlend = 0.28; // 少しだけ背景側へ寄せる
        const double minContrast = 2.4;   // 薄すぎ回避

        var candidate = Blend(baseTextColor, backgroundColor, initialBlend);
        if (GetContrastRatio(candidate, backgroundColor) >= minContrast)
            return candidate;

        // 薄くなりすぎる場合はベース色へ戻してコントラストを確保
        for (var t = 0.24; t >= 0.08; t -= 0.04)
        {
            candidate = Blend(baseTextColor, backgroundColor, t);
            if (GetContrastRatio(candidate, backgroundColor) >= minContrast)
                return candidate;
        }

        return baseTextColor;
    }

    private static System.Windows.Media.Color Blend(
        System.Windows.Media.Color from,
        System.Windows.Media.Color to,
        double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        byte Mix(byte a, byte b) => (byte)Math.Clamp((int)Math.Round(a + ((b - a) * t)), 0, 255);
        return System.Windows.Media.Color.FromRgb(Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B));
    }

    private static double GetContrastRatio(System.Windows.Media.Color a, System.Windows.Media.Color b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        var bright = Math.Max(la, lb);
        var dark = Math.Min(la, lb);
        return (bright + 0.05) / (dark + 0.05);
    }

    private static double RelativeLuminance(System.Windows.Media.Color color)
    {
        static double Channel(byte c)
        {
            var v = c / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }

    private string BuildTodayScheduleHint()
    {
        var today = DateTime.Today;
        var dayOfWeek = (int)today.DayOfWeek;
        var daysFromMonday = dayOfWeek == 0 ? 6 : dayOfWeek - 1;
        var weekStart = today.AddDays(-daysFromMonday);

        var blocks = _scheduleService.GetBlocksForWeek(weekStart);
        var lines = new List<string>();

        foreach (var b in blocks
            .Where(b => b.Kind == ScheduleBlockKind.Timed
                     && b.StartAt.HasValue
                     && b.StartAt.Value.Date == today)
            .OrderBy(b => b.StartAt!.Value))
        {
            var start = b.StartAt!.Value;
            var end = start.AddMinutes(b.DurationSlots * 30);
            var label = string.IsNullOrWhiteSpace(b.TitleSnapshot)
                ? b.ProjectShortName
                : $"{b.ProjectShortName} {b.TitleSnapshot}";
            lines.Add($"{start:HH:mm}-{end:HH:mm} {label}");
        }

        foreach (var b in blocks
            .Where(b => b.Kind == ScheduleBlockKind.AllDay
                     && b.StartDate.HasValue && b.StartDate.Value.Date <= today
                     && b.EndDate.HasValue && b.EndDate.Value.Date >= today))
        {
            var label = string.IsNullOrWhiteSpace(b.TitleSnapshot)
                ? b.ProjectShortName
                : $"{b.ProjectShortName} {b.TitleSnapshot}";
            lines.Add($"(all day) {label}");
        }

        return string.Join("\n", lines);
    }

    private Task<string?> ShowScheduleHintDialogAsync()
    {
        var tcs = new TaskCompletionSource<string?>();
        var appResources = Application.Current.Resources;
        var surface  = (System.Windows.Media.Brush)appResources["AppSurface0"];
        var surface1 = (System.Windows.Media.Brush)appResources["AppSurface1"];
        var surface2 = (System.Windows.Media.Brush)appResources["AppSurface2"];
        var text     = (System.Windows.Media.Brush)appResources["AppText"];
        var subtext  = (System.Windows.Media.Brush)appResources["AppSubtext0"];

        var titleBar = new Grid { Background = surface1, Height = 38 };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleIcon = new System.Windows.Controls.TextBlock
        {
            Text = "☀",
            FontSize = 13,
            Margin = new Thickness(12, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleIcon, 0);

        var titleText = new System.Windows.Controls.TextBlock
        {
            Text = "Plan My Day",
            Foreground = text,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleText, 1);

        var closeBtnTitle = new System.Windows.Controls.Button
        {
            Content = "✕",
            Width = 34,
            Height = 26,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = subtext,
            FontSize = 13
        };
        Grid.SetColumn(closeBtnTitle, 2);

        titleBar.Children.Add(titleIcon);
        titleBar.Children.Add(titleText);
        titleBar.Children.Add(closeBtnTitle);

        var promptLabel = new System.Windows.Controls.TextBlock
        {
            Text = "Any meetings or time constraints today?",
            Foreground = text,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 4)
        };

        var promptSub = new System.Windows.Controls.TextBlock
        {
            Text = "(optional — press Plan to skip)",
            Foreground = subtext,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 10)
        };

        var inputBox = new System.Windows.Controls.TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 80,
            FontSize = 12,
            Padding = new Thickness(6),
            Background = surface1,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        var prefill = BuildTodayScheduleHint();
        if (!string.IsNullOrEmpty(prefill))
        {
            inputBox.Text = prefill;
            inputBox.CaretIndex = inputBox.Text.Length;
        }

        var cancelBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Cancel",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            MinWidth = 80,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 0)
        };

        var planBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Plan My Day ▶",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            MinWidth = 120,
            Height = 32
        };

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { cancelBtn, planBtn }
        };

        var body = new StackPanel
        {
            Margin = new Thickness(16, 14, 16, 16),
            Children = { promptLabel, promptSub, inputBox, btnRow }
        };

        var root = new Grid { Background = surface };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(body, 1);
        root.Children.Add(titleBar);
        root.Children.Add(body);

        var win = new Window
        {
            Title = "",
            Owner = Window.GetWindow(this),
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            Width = 500,
            Height = 265,
            Background = surface,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            Content = root
        };

        closeBtnTitle.Click += (_, _) => { tcs.TrySetResult(null); win.Close(); };
        cancelBtn.Click += (_, _) => { tcs.TrySetResult(null); win.Close(); };
        planBtn.Click += (_, _) => { tcs.TrySetResult(inputBox.Text); win.Close(); };
        titleBar.MouseLeftButtonDown += (_, ev) =>
        {
            if (ev.LeftButton == MouseButtonState.Pressed) win.DragMove();
        };
        win.KeyDown += (_, ev) =>
        {
            if (ev.Key == System.Windows.Input.Key.Escape) { tcs.TrySetResult(null); win.Close(); }
        };

        win.ShowDialog();
        return tcs.Task;
    }

    private Task<bool> ShowMorningConfirmDialogAsync()
    {
        var tcs = new TaskCompletionSource<bool>();
        var appResources = Application.Current.Resources;
        var surface  = (System.Windows.Media.Brush)appResources["AppSurface0"];
        var surface1 = (System.Windows.Media.Brush)appResources["AppSurface1"];
        var surface2 = (System.Windows.Media.Brush)appResources["AppSurface2"];
        var text     = (System.Windows.Media.Brush)appResources["AppText"];
        var subtext  = (System.Windows.Media.Brush)appResources["AppSubtext0"];

        var titleBar = new Grid { Background = surface1, Height = 38 };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var titleIcon = new System.Windows.Controls.TextBlock
        {
            Text = "☀",
            FontSize = 13,
            Margin = new Thickness(12, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleIcon, 0);

        var titleText = new System.Windows.Controls.TextBlock
        {
            Text = "Plan My Day",
            Foreground = text,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleText, 1);

        titleBar.Children.Add(titleIcon);
        titleBar.Children.Add(titleText);

        var greetingLabel = new System.Windows.Controls.TextBlock
        {
            Text = "Good morning!",
            Foreground = text,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        };

        var promptLabel = new System.Windows.Controls.TextBlock
        {
            Text = "Generate today's plan?",
            Foreground = subtext,
            FontSize = 13
        };

        var notTodayBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Not today",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            MinWidth = 90,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 0)
        };

        var letsGoBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Let's go ▶",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            MinWidth = 100,
            Height = 32
        };

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
            Children = { notTodayBtn, letsGoBtn }
        };

        var body = new StackPanel
        {
            Margin = new Thickness(20, 16, 20, 18),
            Children = { greetingLabel, promptLabel, btnRow }
        };

        var root = new Grid { Background = surface };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(body, 1);
        root.Children.Add(titleBar);
        root.Children.Add(body);

        var win = new Window
        {
            Title = "",
            Owner = Window.GetWindow(this),
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            Width = 360,
            Height = 175,
            Background = surface,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            Content = root
        };

        notTodayBtn.Click += (_, _) => { tcs.TrySetResult(false); win.Close(); };
        letsGoBtn.Click += (_, _) => { tcs.TrySetResult(true); win.Close(); };
        titleBar.MouseLeftButtonDown += (_, ev) =>
        {
            if (ev.LeftButton == MouseButtonState.Pressed) win.DragMove();
        };

        win.ShowDialog();
        return tcs.Task;
    }

    private static bool ShouldShowMorningAutopilot()
    {
        var now = DateTime.Now;
        if (now.Hour < 6) return false;
        if (now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday) return false;
        if (_planMyDayShownToday.HasValue && _planMyDayShownToday.Value.Date == now.Date) return false;
        return true;
    }
}
