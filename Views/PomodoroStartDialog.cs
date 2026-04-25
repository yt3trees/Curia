using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Curia.Models;
using Curia.Services;
using Curia.ViewModels;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfHA = System.Windows.HorizontalAlignment;
using WpfVA = System.Windows.VerticalAlignment;

namespace Curia.Views;

/// <summary>ポモドーロセッション開始ダイアログ。</summary>
public class PomodoroStartDialog : Window
{
    private readonly PomodoroViewModel _vm;

    private System.Windows.Media.Brush Surface0 => (System.Windows.Media.Brush)FindResource("AppSurface0");
    private System.Windows.Media.Brush Surface1 => (System.Windows.Media.Brush)FindResource("AppSurface1");
    private System.Windows.Media.Brush Surface2 => (System.Windows.Media.Brush)FindResource("AppSurface2");
    private System.Windows.Media.Brush Text => (System.Windows.Media.Brush)FindResource("AppText");
    private System.Windows.Media.Brush Subtext => (System.Windows.Media.Brush)FindResource("AppSubtext0");
    private System.Windows.Media.Brush Accent => Application.Current.Resources.Contains("AppPeach")
        ? (System.Windows.Media.Brush)Application.Current.Resources["AppPeach"]
        : (System.Windows.Media.Brush)FindResource("AppText");

    private System.Windows.Controls.ComboBox _projectCombo = null!;
    private System.Windows.Controls.ComboBox _taskCombo = null!;
    private System.Windows.Controls.TextBox _taskTextBox = null!;
    private System.Windows.Controls.RadioButton _rb25 = null!, _rb50 = null!, _rbCustom = null!;
    private System.Windows.Controls.TextBox _customMinBox = null!;
    private bool _isTextMode = false;

    public PomodoroSession? Result { get; private set; }

    public PomodoroStartDialog(PomodoroViewModel vm, Window owner)
    {
        _vm = vm;
        Owner = owner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 400;
        MinHeight = 0;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid { Background = Surface0 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // タイトルバー
        var titleBar = BuildTitleBar("Start Pomodoro Session");
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        // コンテンツ
        var content = BuildContent();
        Grid.SetRow(content, 1);
        root.Children.Add(content);

        // ボーダー
        var border = new Border
        {
            BorderBrush = Surface2,
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false
        };
        Grid.SetRowSpan(border, 2);
        root.Children.Add(border);

        Content = root;
        Loaded += OnLoaded;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private Grid BuildTitleBar(string titleText)
    {
        var bar = new Grid
        {
            Height = 36,
            Background = Surface1,
            Cursor = System.Windows.Input.Cursors.SizeAll
        };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new System.Windows.Controls.TextBlock
        {
            Text = "🍅",
            FontSize = 12,
            VerticalAlignment = WpfVA.Center,
            Margin = new Thickness(12, 0, 6, 0)
        };
        Grid.SetColumn(dot, 0);

        var title = new System.Windows.Controls.TextBlock
        {
            Text = titleText,
            Foreground = Text,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = WpfVA.Center
        };
        Grid.SetColumn(title, 1);

        var closeBtn = new System.Windows.Controls.Button
        {
            Content = "✕",
            Width = 36,
            Height = 36,
            Background = WpfBrushes.Transparent,
            BorderBrush = WpfBrushes.Transparent,
            Foreground = Subtext,
            FontSize = 12,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        closeBtn.Click += (_, _) => Close();
        Grid.SetColumn(closeBtn, 2);

        bar.Children.Add(dot);
        bar.Children.Add(title);
        bar.Children.Add(closeBtn);
        bar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };

        return bar;
    }

    private StackPanel BuildContent()
    {
        var panel = new StackPanel { Margin = new Thickness(16, 12, 16, 16) };

        // Project
        panel.Children.Add(BuildLabel("Project"));
        _projectCombo = new System.Windows.Controls.ComboBox
        {
            Background = Surface1,
            Foreground = Text,
            BorderBrush = Surface2,
            Padding = new Thickness(6, 4, 4, 4),
            Margin = new Thickness(0, 4, 0, 12)
        };
        _projectCombo.DisplayMemberPath = nameof(ProjectInfo.Name);
        foreach (var p in _vm.Projects)
            _projectCombo.Items.Add(p);
        if (_vm.SelectedProject != null)
            _projectCombo.SelectedItem = _vm.SelectedProject;
        else if (_projectCombo.Items.Count > 0)
            _projectCombo.SelectedIndex = 0;
        _projectCombo.SelectionChanged += (_, _) => PopulateTaskCombo();
        panel.Children.Add(_projectCombo);

        // Task / Note ヘッダー行 (ラベル + モード切替ボタン)
        var taskHeaderRow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        taskHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        taskHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var taskLabel = BuildLabel("Task  (optional)");
        Grid.SetColumn(taskLabel, 0);
        taskHeaderRow.Children.Add(taskLabel);

        var modeToggleBtn = new System.Windows.Controls.Button
        {
            Content = "Type manually",
            FontSize = 10,
            Padding = new Thickness(6, 2, 6, 2),
            Background = Surface1,
            Foreground = Subtext,
            BorderBrush = Surface2,
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = WpfVA.Center
        };
        Grid.SetColumn(modeToggleBtn, 1);
        taskHeaderRow.Children.Add(modeToggleBtn);
        panel.Children.Add(taskHeaderRow);

        // Task 選択コンボ
        _taskCombo = new System.Windows.Controls.ComboBox
        {
            Background = Surface1,
            Foreground = Text,
            BorderBrush = Surface2,
            Padding = new Thickness(6, 4, 4, 4),
            Margin = new Thickness(0, 0, 0, 12)
        };
        panel.Children.Add(_taskCombo);

        // Task 自由入力テキストボックス (初期非表示)
        _taskTextBox = new System.Windows.Controls.TextBox
        {
            Background = Surface1,
            Foreground = Text,
            CaretBrush = Text,
            BorderBrush = Surface2,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 4, 6, 4),
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 12),
            Visibility = Visibility.Collapsed
        };
        panel.Children.Add(_taskTextBox);

        modeToggleBtn.Click += (_, _) =>
        {
            _isTextMode = !_isTextMode;
            _taskCombo.Visibility = _isTextMode ? Visibility.Collapsed : Visibility.Visible;
            _taskTextBox.Visibility = _isTextMode ? Visibility.Visible : Visibility.Collapsed;
            modeToggleBtn.Content = _isTextMode ? "Pick from list" : "Type manually";
            if (_isTextMode) _taskTextBox.Focus();
        };

        // Duration
        panel.Children.Add(BuildLabel("Duration"));

        // 1行目: 25 min / 50 min
        var durationRow1 = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 6, 0, 0)
        };
        _rb25 = BuildRadioButton("25 min", true);
        _rb50 = BuildRadioButton("50 min", false);
        durationRow1.Children.Add(_rb25);
        durationRow1.Children.Add(_rb50);
        panel.Children.Add(durationRow1);

        // 2行目: Custom
        var durationRow2 = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4, 0, 16)
        };
        _rbCustom = BuildRadioButton("Custom:", false);
        _customMinBox = new System.Windows.Controls.TextBox
        {
            Text = "25",
            Width = 44,
            Height = 24,
            Background = Surface1,
            Foreground = Text,
            BorderBrush = Surface2,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 2, 4, 2),
            FontSize = 12,
            VerticalContentAlignment = WpfVA.Center,
            Margin = new Thickness(4, 0, 4, 0),
            IsEnabled = false
        };
        var minLabel = new System.Windows.Controls.TextBlock
        {
            Text = "min",
            Foreground = Subtext,
            FontSize = 12,
            VerticalAlignment = WpfVA.Center
        };

        _rbCustom.Checked   += (_, _) => { _customMinBox.IsEnabled = true;  _customMinBox.Focus(); };
        _rb25.Checked       += (_, _) => _customMinBox.IsEnabled = false;
        _rb50.Checked       += (_, _) => _customMinBox.IsEnabled = false;

        durationRow2.Children.Add(_rbCustom);
        durationRow2.Children.Add(_customMinBox);
        durationRow2.Children.Add(minLabel);
        panel.Children.Add(durationRow2);

        // ボタン
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = WpfHA.Right
        };
        var cancelBtn = BuildButton("Cancel", false);
        cancelBtn.Click += (_, _) => Close();
        var startBtn = BuildButton("Start ▶", true);
        startBtn.Margin = new Thickness(8, 0, 0, 0);
        startBtn.Click += OnStartClick;
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(startBtn);
        panel.Children.Add(btnRow);

        return panel;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Background = Surface0;
        PopulateTaskCombo();
    }

    private void PopulateTaskCombo()
    {
        _taskCombo.Items.Clear();
        _taskCombo.Items.Add("(none)");
        _taskCombo.SelectedIndex = 0;

        if (_projectCombo.SelectedItem is not ProjectInfo proj) return;
        var tasksPath = System.IO.Path.Combine(proj.AiContextPath, "obsidian_notes", "tasks.md");
        if (!System.IO.File.Exists(tasksPath)) return;

        try
        {
            foreach (var line in System.IO.File.ReadAllLines(tasksPath))
            {
                if (!line.TrimStart().StartsWith("- [ ]", StringComparison.Ordinal)) continue;
                var title = System.Text.RegularExpressions.Regex.Replace(
                    line.Trim(), @"^-\s+\[ \]\s+(?:\[.+?\]\s+)*", "")
                    .Split("(Due:")[0]
                    .Split("[[Asana]")[0]
                    .Trim();
                if (!string.IsNullOrWhiteSpace(title) && title.Length <= 120)
                    _taskCombo.Items.Add(title);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PomodoroStartDialog] PopulateTaskCombo: {ex.Message}");
        }
    }

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (_projectCombo.SelectedItem is not ProjectInfo proj) { Close(); return; }

        int duration = 25;
        if (_rb50.IsChecked == true)
            duration = 50;
        else if (_rbCustom.IsChecked == true &&
                 int.TryParse(_customMinBox.Text.Trim(), out int custom) && custom is >= 1 and <= 120)
            duration = custom;

        string? task;
        if (_isTextMode)
            task = string.IsNullOrWhiteSpace(_taskTextBox.Text) ? null : _taskTextBox.Text.Trim();
        else
            task = _taskCombo.SelectedItem is string t && t != "(none)" ? t : null;

        Result = new PomodoroSession
        {
            DurationMinutes = duration,
            ProjectKey = proj.HiddenKey,
            ProjectName = proj.Name,
            TaskTitle = task
        };

        DialogResult = true;
        Close();
    }

    // ── ファクトリ ──────────────────────────────────────────────────────────

    private System.Windows.Controls.TextBlock BuildLabel(string text) =>
        new()
        {
            Text = text,
            Foreground = Subtext,
            FontSize = 12,
        };

    private System.Windows.Controls.RadioButton BuildRadioButton(string content, bool isChecked) =>
        new System.Windows.Controls.RadioButton
        {
            Content = content,
            IsChecked = isChecked,
            Foreground = Text,
            FontSize = 12,
            VerticalAlignment = WpfVA.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };

    private System.Windows.Controls.Button BuildButton(string text, bool isPrimary)
    {
        var bg = isPrimary ? Accent : Surface1;
        var fg = isPrimary ? WpfBrushes.Black : Text;
        return new System.Windows.Controls.Button
        {
            Content = text,
            MinWidth = 72,
            Height = 28,
            Padding = new Thickness(10, 0, 10, 0),
            FontSize = 12,
            Background = bg,
            Foreground = fg,
            BorderBrush = Surface2,
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand
        };
    }
}
