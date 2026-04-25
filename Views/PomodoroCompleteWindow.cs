using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Curia.Services;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfHA = System.Windows.HorizontalAlignment;
using WpfVA = System.Windows.VerticalAlignment;

namespace Curia.Views;

/// <summary>ポモドーロセッション完了ポップアップ。右下に表示し、メモを受け付ける。</summary>
public class PomodoroCompleteWindow : Window
{
    private readonly PomodoroService _pomodoroService;
    private readonly PomodoroSession _session;

    private System.Windows.Media.Brush Surface0 => (System.Windows.Media.Brush)FindResource("AppSurface0");
    private System.Windows.Media.Brush Surface1 => (System.Windows.Media.Brush)FindResource("AppSurface1");
    private System.Windows.Media.Brush Surface2 => (System.Windows.Media.Brush)FindResource("AppSurface2");
    private System.Windows.Media.Brush AppText => (System.Windows.Media.Brush)FindResource("AppText");
    private System.Windows.Media.Brush Subtext => (System.Windows.Media.Brush)FindResource("AppSubtext0");
    private System.Windows.Media.Brush Accent => Application.Current.Resources.Contains("AppPeach")
        ? (System.Windows.Media.Brush)Application.Current.Resources["AppPeach"]
        : (System.Windows.Media.Brush)FindResource("AppText");
    private System.Windows.Media.Brush AccentGreen => Application.Current.Resources.Contains("AppGreen")
        ? (System.Windows.Media.Brush)Application.Current.Resources["AppGreen"]
        : (System.Windows.Media.Brush)FindResource("AppText");

    private System.Windows.Controls.TextBox _noteBox = null!;

    public Action? OnBreakRequested { get; set; }

    public PomodoroCompleteWindow(PomodoroService pomodoroService, PomodoroSession session, Window owner)
    {
        _pomodoroService = pomodoroService;
        _session = session;
        Owner = owner;

        Width = 400;
        SizeToContent = SizeToContent.Height;
        MinHeight = 0;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;

        System.Windows.Shell.WindowChrome.SetWindowChrome(this,
            new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(0),
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false
            });

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var titleBar = BuildTitleBar(session);
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        var content = BuildContent(session);
        Grid.SetRow(content, 1);
        root.Children.Add(content);

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
        KeyDown += OnKeyDown;
    }

    private Grid BuildTitleBar(PomodoroSession session)
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

        var icon = new System.Windows.Controls.TextBlock
        {
            Text = "✓",
            Foreground = AccentGreen,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = WpfVA.Center,
            Margin = new Thickness(12, 0, 6, 0)
        };
        Grid.SetColumn(icon, 0);

        var title = new System.Windows.Controls.TextBlock
        {
            Text = $"Session Complete  {session.DurationMinutes} min",
            Foreground = AppText,
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
        closeBtn.Click += (_, _) => OnSkipClick();
        Grid.SetColumn(closeBtn, 2);

        bar.Children.Add(icon);
        bar.Children.Add(title);
        bar.Children.Add(closeBtn);
        bar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };

        return bar;
    }

    private StackPanel BuildContent(PomodoroSession session)
    {
        var panel = new StackPanel { Margin = new Thickness(14, 10, 14, 14), Background = Surface0 };

        // プロジェクト/タスク表示
        var projectLabel = session.TaskTitle != null
            ? $"{session.ProjectName}  /  {session.TaskTitle}"
            : session.ProjectName;
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = projectLabel,
            Foreground = Subtext,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 8)
        });

        // メモ入力
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "What did you do?",
            Foreground = Subtext,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 4)
        });
        _noteBox = new System.Windows.Controls.TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 64,
            MaxHeight = 120,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Surface1,
            Foreground = AppText,
            CaretBrush = AppText,
            BorderBrush = Surface2,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            FontSize = 13,
            VerticalContentAlignment = WpfVA.Top,
            Margin = new Thickness(0, 0, 0, 12)
        };
        panel.Children.Add(_noteBox);

        // ボタン行
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = WpfHA.Right
        };

        var skipBtn = BuildButton("Skip", false);
        skipBtn.Click += (_, _) => OnSkipClick();

        var breakBtn = BuildButton("☕ Take a Break", false);
        breakBtn.Margin = new Thickness(6, 0, 0, 0);
        breakBtn.Click += (_, _) => OnBreakClick();

        var saveBtn = BuildButton("Save ✓", true);
        saveBtn.Margin = new Thickness(6, 0, 0, 0);
        saveBtn.Click += (_, _) => OnSaveClick();

        btnRow.Children.Add(skipBtn);
        btnRow.Children.Add(breakBtn);
        btnRow.Children.Add(saveBtn);
        panel.Children.Add(btnRow);

        return panel;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Background = Surface0;
        PositionBottomRight();
        _noteBox.Focus();
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { OnSkipClick(); e.Handled = true; }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OnSaveClick();
            e.Handled = true;
        }
    }

    private void OnSkipClick()
    {
        _ = _pomodoroService.SaveSessionAsync(_session);
        Close();
    }

    private void OnSaveClick()
    {
        _session.Note = _noteBox.Text.Trim();
        _ = _pomodoroService.SaveSessionAsync(_session);
        Close();
    }

    private void OnBreakClick()
    {
        _session.Note = _noteBox.Text.Trim();
        _ = _pomodoroService.SaveSessionAsync(_session);
        OnBreakRequested?.Invoke();
        Close();
    }

    private void PositionBottomRight()
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen == null) return;

        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var workArea = screen.WorkingArea;

        Left = workArea.Right / dpi.DpiScaleX - Width - 16;
        Top = workArea.Bottom / dpi.DpiScaleY - ActualHeight - 16;
    }

    // ── ファクトリ ──────────────────────────────────────────────────────────

    private System.Windows.Controls.Button BuildButton(string text, bool isPrimary)
    {
        var bg = isPrimary ? Accent : Surface1;
        var fg = isPrimary ? WpfBrushes.Black : AppText;
        return new System.Windows.Controls.Button
        {
            Content = text,
            MinWidth = 60,
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
