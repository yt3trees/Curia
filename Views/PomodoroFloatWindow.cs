using System.Windows;
using System.Windows.Input;
using Curia.Services;

namespace Curia.Views;

/// <summary>ポモドーロ実行中に画面右下へ常時表示するミニフロートウィンドウ。</summary>
public class PomodoroFloatWindow : Window
{
    private System.Windows.Media.Brush Surface1 => (System.Windows.Media.Brush)FindResource("AppSurface1");
    private System.Windows.Media.Brush Surface2 => (System.Windows.Media.Brush)FindResource("AppSurface2");
    private System.Windows.Media.Brush AppText  => (System.Windows.Media.Brush)FindResource("AppText");
    private System.Windows.Media.Brush Subtext  => (System.Windows.Media.Brush)FindResource("AppSubtext0");

    private readonly System.Windows.Controls.TextBlock _iconText;
    private readonly System.Windows.Controls.TextBlock _timerText;
    private readonly System.Windows.Controls.TextBlock _pauseBtn;
    private readonly System.Windows.Controls.TextBlock _stopBtn;
    private readonly System.Windows.Controls.TextBlock _hideBtn;
    private readonly System.Windows.Controls.Border    _border;

    private readonly PomodoroService _service;

    /// <summary>中止ボタン押下時に呼ばれるコールバック (DashboardPage が処理)。</summary>
    public Action? OnStopRequested { get; set; }

    public PomodoroFloatWindow(PomodoroService service)
    {
        _service = service;

        WindowStyle   = WindowStyle.None;
        ResizeMode    = ResizeMode.NoResize;
        Topmost       = true;
        ShowInTaskbar = false;
        Width         = 140;
        Height        = 34;
        Opacity       = 0.92;

        System.Windows.Shell.WindowChrome.SetWindowChrome(this,
            new System.Windows.Shell.WindowChrome
            {
                CaptionHeight         = 0,
                ResizeBorderThickness = new Thickness(0),
                GlassFrameThickness   = new Thickness(0),
                UseAeroCaptionButtons = false
            });

        _iconText = new System.Windows.Controls.TextBlock
        {
            Text              = "🍅",
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(10, 7, 5, 7)
        };

        _timerText = new System.Windows.Controls.TextBlock
        {
            Text              = "25:00",
            FontSize          = 13,
            FontFamily        = new System.Windows.Media.FontFamily("Consolas"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 7, 8, 7)
        };

        // 一時停止 / 再開ボタン
        _pauseBtn = MakeIconBtn("⏸", "Pause");
        _pauseBtn.MouseLeftButtonUp += (_, e) => { OnPauseClick(); e.Handled = true; };

        // 中止ボタン
        _stopBtn = MakeIconBtn("■", "Stop session");
        _stopBtn.MouseLeftButtonUp += (_, e) => { OnStopRequested?.Invoke(); e.Handled = true; };

        // 非表示ボタン
        _hideBtn = MakeIconBtn("−", "Hide — click toolbar timer to restore");
        _hideBtn.MouseLeftButtonUp += (_, e) => { Hide(); e.Handled = true; };

        var row = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal
        };
        row.Children.Add(_iconText);
        row.Children.Add(_timerText);
        row.Children.Add(_pauseBtn);
        row.Children.Add(_stopBtn);
        row.Children.Add(_hideBtn);

        row.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed && !e.Handled
                && e.OriginalSource is System.Windows.Controls.TextBlock tb
                && (tb == _iconText || tb == _timerText))
                DragMove();
        };

        _border = new System.Windows.Controls.Border
        {
            Child           = row,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
        };
        Content = _border;

        Loaded  += OnLoaded;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _border.Background    = Surface1;
        _border.BorderBrush   = Surface2;
        _iconText.Foreground  = AppText;
        _timerText.Foreground = AppText;
        _pauseBtn.Foreground  = Subtext;
        _stopBtn.Foreground   = Subtext;
        _hideBtn.Foreground   = Subtext;
        PositionBottomRight();
    }

    private void OnPauseClick()
    {
        if (_service.State == PomodoroState.Running)
        {
            _service.Pause();
            _pauseBtn.Text   = "▶";
            _pauseBtn.ToolTip = "Resume";
            _iconText.Text   = "⏸";
        }
        else if (_service.State == PomodoroState.Paused)
        {
            _service.Resume();
            _pauseBtn.Text   = "⏸";
            _pauseBtn.ToolTip = "Pause";
            _iconText.Text   = "🍅";
        }
    }

    public void UpdateDisplay(TimeSpan remaining, PomodoroState state)
    {
        _timerText.Text = $"{(int)remaining.TotalMinutes:D2}:{remaining.Seconds:D2}";
        if (state != PomodoroState.Paused)
        {
            _iconText.Text  = state == PomodoroState.Break ? "☕" : "🍅";
            _pauseBtn.Text  = state == PomodoroState.Break ? "" : "⏸";
            _pauseBtn.ToolTip = "Pause";
        }
    }

    private void PositionBottomRight()
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen == null) return;
        var dpi      = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var workArea = screen.WorkingArea;
        Left = workArea.Right  / dpi.DpiScaleX - ActualWidth  - 16;
        Top  = workArea.Bottom / dpi.DpiScaleY - ActualHeight - 16;
    }

    private System.Windows.Controls.TextBlock MakeIconBtn(string text, string tooltip) =>
        new()
        {
            Text              = text,
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 7, 6, 7),
            Cursor            = System.Windows.Input.Cursors.Hand,
            ToolTip           = tooltip,
        };
}
