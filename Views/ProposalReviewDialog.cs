using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Rendering;
using System.Windows.Media;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Curia.Models;
using MediaColor = System.Windows.Media.Color;

namespace Curia.Views;

/// <summary>
/// LLM 提案の差分確認ダイアログ。EditorPage と CaptureWindow で共用する。
/// </summary>
internal static class ProposalReviewDialog
{
    /// <summary>
    /// 差分確認ダイアログを表示する。
    /// </summary>
    /// <param name="owner">オーナーウィンドウ</param>
    /// <param name="proposal">提案内容</param>
    /// <param name="titleText">タイトルバーに表示するテキスト</param>
    /// <param name="titleIcon">タイトルアイコン文字 (例: "⟳", "⚡")</param>
    /// <param name="extraInfo">サマリ下に表示する追加情報 (null で非表示)</param>
    /// <param name="refineFunc">リファイン関数 (currentProposed, instructions) → refined</param>
    public static Task<(bool apply, string? content)> ShowAsync(
        Window owner,
        FileUpdateProposal proposal,
        string titleText,
        string titleIcon = "⟳",
        string? extraInfo = null,
        Func<string, string, Task<string>>? refineFunc = null)
    {
        var tcs = new TaskCompletionSource<(bool apply, string? content)>();

        var appResources = Application.Current.Resources;
        var surface  = (System.Windows.Media.Brush)appResources["AppSurface0"];
        var surface1 = (System.Windows.Media.Brush)appResources["AppSurface1"];
        var surface2 = (System.Windows.Media.Brush)appResources["AppSurface2"];
        var text     = (System.Windows.Media.Brush)appResources["AppText"];
        var subtext  = (System.Windows.Media.Brush)appResources["AppSubtext0"];
        var accent   = appResources.Contains("AppGreen")
            ? (System.Windows.Media.Brush)appResources["AppGreen"] : text;
        var editorBg = (Application.Current.Resources["EditorBackground"] as System.Windows.Media.SolidColorBrush)
            ?? new System.Windows.Media.SolidColorBrush(
                (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString("#0d1117"));
        var editorFg = (Application.Current.Resources["EditorForeground"] as System.Windows.Media.SolidColorBrush)
            ?? new System.Windows.Media.SolidColorBrush(
                (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString("#c9d1d9"));

        string currentProposed = proposal.ProposedContent;

        // ---- diff viewer ----
        var diffViewer = new TextEditor
        {
            FontFamily = new System.Windows.Media.FontFamily("Consolas, 'Courier New', monospace"),
            FontSize = 12, WordWrap = false, ShowLineNumbers = true, IsReadOnly = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            Background = editorBg, Foreground = editorFg
        };
        AddBottomViewportPadding(diffViewer);
        diffViewer.LineNumbersForeground =
            (Application.Current.Resources["AppSubtext0"] as System.Windows.Media.SolidColorBrush)
            ?? new System.Windows.Media.SolidColorBrush(
                (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString("#8b949e"));
        var diffRenderer = new DiffLineBackgroundRenderer();
        diffViewer.TextArea.TextView.BackgroundRenderers.Add(diffRenderer);
        diffViewer.PreviewMouseWheel += (s, e) =>
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
            var sv = FindVisualChild<ScrollViewer>(diffViewer);
            if (sv == null || sv.ScrollableWidth <= 0) return;
            const double step = 48d;
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset + (e.Delta > 0 ? -step : step));
            e.Handled = true;
        };

        void RefreshDiff(string proposed)
        {
            var builder    = new InlineDiffBuilder(new Differ());
            var diffResult = builder.BuildDiffModel(proposal.CurrentContent, proposed);
            var sb         = new StringBuilder();
            var lineTypes  = new Dictionary<int, ChangeType>();
            int lineNum    = 0;
            foreach (var line in diffResult.Lines)
            {
                lineNum++;
                switch (line.Type)
                {
                    case ChangeType.Inserted:
                        sb.AppendLine("+ " + line.Text);
                        lineTypes[lineNum] = ChangeType.Inserted;
                        break;
                    case ChangeType.Deleted:
                        sb.AppendLine("- " + line.Text);
                        lineTypes[lineNum] = ChangeType.Deleted;
                        break;
                    case ChangeType.Modified:
                        sb.AppendLine("~ " + line.Text);
                        lineTypes[lineNum] = ChangeType.Modified;
                        break;
                    default:
                        sb.AppendLine("  " + line.Text);
                        break;
                }
            }
            diffRenderer.SetLineTypes(lineTypes);
            diffViewer.Text = sb.ToString();
        }
        RefreshDiff(currentProposed);

        // ---- タイトルバー ----
        var titleBar = new Grid { Background = surface1, Height = 38 };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleIconBlock = new System.Windows.Controls.TextBlock
        {
            Text = titleIcon, Foreground = accent, FontSize = 15,
            Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleIconBlock, 0);
        var titleTextBlock = new System.Windows.Controls.TextBlock
        {
            Text = titleText, Foreground = text, FontSize = 14,
            FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleTextBlock, 1);
        var closeBtn = new System.Windows.Controls.Button
        {
            Content = "✕", Width = 34, Height = 26,
            Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0), Foreground = subtext, FontSize = 13
        };
        Grid.SetColumn(closeBtn, 2);
        titleBar.Children.Add(titleIconBlock);
        titleBar.Children.Add(titleTextBlock);
        titleBar.Children.Add(closeBtn);

        // ---- 情報パネル ----
        var infoPanel = new StackPanel { Margin = new Thickness(16, 10, 16, 6) };
        if (!string.IsNullOrWhiteSpace(proposal.Summary))
            infoPanel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = proposal.Summary, Foreground = subtext, FontSize = 11, TextWrapping = TextWrapping.Wrap
            });
        if (extraInfo != null)
            infoPanel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = extraInfo, Foreground = subtext, FontSize = 11, Margin = new Thickness(0, 2, 0, 0)
            });

        // ---- 差分ヘッダー ----
        var diffHeader = new System.Windows.Controls.TextBlock
        {
            Text = "Proposed changes  (+ added  - removed)", Foreground = subtext,
            FontSize = 11, Margin = new Thickness(16, 4, 16, 4)
        };

        // ---- リファインエリア ----
        var instructionsBox = new System.Windows.Controls.TextBox
        {
            Margin = new Thickness(0), Padding = new Thickness(8, 6, 8, 6),
            Background = surface1, Foreground = text, BorderBrush = surface2,
            BorderThickness = new Thickness(1), FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsEnabled = refineFunc != null
        };
        var refineButton = new Wpf.Ui.Controls.Button
        {
            Content = "Refine", Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            MinWidth = 80, Height = 32, Margin = new Thickness(8, 0, 0, 0),
            IsEnabled = refineFunc != null
        };
        var refineStatus = new System.Windows.Controls.TextBlock
        {
            Foreground = subtext, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0), TextWrapping = TextWrapping.Wrap
        };
        var placeholder = new System.Windows.Controls.TextBlock
        {
            Text = "Refinement instructions (e.g. \"Remove duplicate\", \"Move X up\")",
            Foreground = subtext, FontSize = 12, IsHitTestVisible = false,
            Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Visibility = refineFunc != null ? Visibility.Visible : Visibility.Collapsed
        };
        instructionsBox.TextChanged += (s, e) =>
            placeholder.Visibility = string.IsNullOrEmpty(instructionsBox.Text)
                ? (refineFunc != null ? Visibility.Visible : Visibility.Collapsed)
                : Visibility.Collapsed;
        instructionsBox.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                refineButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                e.Handled = true;
            }
        };
        var inputHost = new Grid();
        inputHost.Children.Add(instructionsBox);
        inputHost.Children.Add(placeholder);

        var refineRow = new Grid { Margin = new Thickness(16, 6, 16, 6) };
        refineRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        refineRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        refineRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(inputHost,    0);
        Grid.SetColumn(refineButton, 1);
        Grid.SetColumn(refineStatus, 2);
        refineRow.Children.Add(inputHost);
        refineRow.Children.Add(refineButton);
        refineRow.Children.Add(refineStatus);

        // ---- フッター ----
        var applyButton = new Wpf.Ui.Controls.Button
        {
            Content = "Apply", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            MinWidth = 90, Height = 32, Margin = new Thickness(0, 0, 8, 0)
        };
        var skipButton = new Wpf.Ui.Controls.Button
        {
            Content = "Skip", Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            MinWidth = 80, Height = 32, Margin = new Thickness(0, 0, 8, 0),
            IsCancel = true
        };
        var debugButton = new Wpf.Ui.Controls.Button
        {
            Content = "View Debug", Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            MinWidth = 90, Height = 32
        };
        var footerLeft  = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Left,  Children = { debugButton } };
        var footerRight = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Children = { applyButton, skipButton } };
        var footerGrid  = new Grid { Margin = new Thickness(16, 4, 16, 10) };
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(footerLeft,  0);
        Grid.SetColumn(footerRight, 2);
        footerGrid.Children.Add(footerLeft);
        footerGrid.Children.Add(footerRight);

        // ---- レイアウト ----
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titleBar,   0); Grid.SetRow(infoPanel,  1);
        Grid.SetRow(diffHeader, 2); Grid.SetRow(diffViewer, 3);
        Grid.SetRow(refineRow,  4); Grid.SetRow(footerGrid, 5);
        root.Children.Add(titleBar); root.Children.Add(infoPanel);
        root.Children.Add(diffHeader); root.Children.Add(diffViewer);
        root.Children.Add(refineRow); root.Children.Add(footerGrid);

        // ウィンドウ境界線
        var border = new Border
        {
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false
        };
        var borderStyle = new Style(typeof(Border));
        var borderTrigger = new DataTrigger
        {
            Binding = new System.Windows.Data.Binding("WindowState")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Window), 1)
            },
            Value = WindowState.Maximized
        };
        borderTrigger.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(0)));
        borderStyle.Triggers.Add(borderTrigger);
        border.Style = borderStyle;

        Grid.SetRow(border, 0);
        Grid.SetRowSpan(border, 6);
        root.Children.Add(border);

        var dialog = new Window
        {
            Content = root, Width = 760, Height = 580,
            MinWidth = 500, MinHeight = 380,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.CanResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner, ShowInTaskbar = false,
            Background = surface
        };
        System.Windows.Shell.WindowChrome.SetWindowChrome(dialog,
            new System.Windows.Shell.WindowChrome
            {
                CaptionHeight         = 0,
                ResizeBorderThickness = new Thickness(4),
                GlassFrameThickness   = new Thickness(0),
                UseAeroCaptionButtons = false
            });

        // ---- イベント ----
        void Complete(bool apply, string? content)
        {
            if (!tcs.Task.IsCompleted) tcs.SetResult((apply, content));
            dialog.Close();
            dialog.Owner?.Activate();
        }

        titleBar.MouseLeftButtonDown += (s, e) => dialog.DragMove();
        closeBtn.Click    += (s, e) => Complete(false, null);
        skipButton.Click  += (s, e) => Complete(false, null);
        applyButton.Click += (s, e) => Complete(true, currentProposed);
        dialog.Closed += (s, e) =>
        {
            if (!tcs.Task.IsCompleted) tcs.SetResult((false, null));
            dialog.Owner?.Activate();
        };

        debugButton.Click += (s, e) =>
        {
            var debugText = new StringBuilder();
            debugText.AppendLine("=== SYSTEM PROMPT ===");
            debugText.AppendLine(proposal.DebugSystemPrompt);
            debugText.AppendLine();
            debugText.AppendLine("=== USER PROMPT ===");
            debugText.AppendLine(proposal.DebugUserPrompt);
            debugText.AppendLine();
            debugText.AppendLine("=== RESPONSE ===");
            debugText.AppendLine(proposal.DebugResponse);
            ShowDebugDialog(dialog, "LLM Debug Log", debugText.ToString(), surface, surface1, surface2, text, subtext);
        };

        if (refineFunc != null)
        {
            refineButton.Click += async (s, e) =>
            {
                var instructions = instructionsBox.Text.Trim();
                if (string.IsNullOrEmpty(instructions))
                {
                    refineStatus.Text = "Enter refinement instructions above.";
                    return;
                }
                refineButton.IsEnabled    = false;
                instructionsBox.IsEnabled = false;
                applyButton.IsEnabled     = false;
                refineStatus.Text         = "Refining...";
                try
                {
                    var refined = await refineFunc(currentProposed, instructions);
                    currentProposed = refined;
                    RefreshDiff(currentProposed);
                    instructionsBox.Clear();
                    refineStatus.Text = "Done.";
                }
                catch (Exception ex)
                {
                    refineStatus.Text = $"Error: {ex.Message}";
                }
                finally
                {
                    refineButton.IsEnabled    = true;
                    instructionsBox.IsEnabled = true;
                    applyButton.IsEnabled     = true;
                }
            };
        }

        dialog.Show();
        dialog.Activate();
        return tcs.Task;
    }

    // -------------------------------------------------------------------------

    private static void AddBottomViewportPadding(TextEditor editor)
    {
        editor.Options.AllowScrollBelowDocument = false;
        var margin = editor.TextArea.TextView.Margin;
        editor.TextArea.TextView.Margin = new Thickness(
            margin.Left, margin.Top, margin.Right,
            SystemParameters.HorizontalScrollBarHeight + 2);
    }

    private static TChild? FindVisualChild<TChild>(DependencyObject parent)
        where TChild : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is TChild typed) return typed;
            var nested = FindVisualChild<TChild>(child);
            if (nested != null) return nested;
        }
        return null;
    }

    private static void ShowDebugDialog(
        Window owner,
        string title, string message,
        System.Windows.Media.Brush surface,
        System.Windows.Media.Brush surface1,
        System.Windows.Media.Brush surface2,
        System.Windows.Media.Brush text,
        System.Windows.Media.Brush subtext)
    {
        var titleBar = new Grid { Background = surface1, Height = 38 };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleBlock = new System.Windows.Controls.TextBlock
        {
            Text = title, Foreground = text, FontSize = 14, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0)
        };
        Grid.SetColumn(titleBlock, 0);
        var closeBtn = new System.Windows.Controls.Button
        {
            Content = "✕", Width = 34, Height = 26,
            Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0), Foreground = subtext, FontSize = 13,
            IsCancel = true
        };
        Grid.SetColumn(closeBtn, 1);
        titleBar.Children.Add(titleBlock);
        titleBar.Children.Add(closeBtn);

        var textBox = new System.Windows.Controls.TextBox
        {
            Text = message, IsReadOnly = true, TextWrapping = TextWrapping.NoWrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = surface1, Foreground = text,
            BorderBrush = surface2, BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            FontFamily = new System.Windows.Media.FontFamily("Consolas, 'Courier New', monospace"),
            FontSize = 11
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(titleBar, 0); Grid.SetRow(textBox, 1);
        root.Children.Add(titleBar); root.Children.Add(textBox);

        // ウィンドウ境界線
        var b = new Border
        {
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false
        };
        Grid.SetRow(b, 0);
        Grid.SetRowSpan(b, 2);
        root.Children.Add(b);

        var d = new Window
        {
            Content = root, Width = 640, Height = 500, MinWidth = 400, MinHeight = 300,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.CanResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner, ShowInTaskbar = false, Background = surface
        };
        System.Windows.Shell.WindowChrome.SetWindowChrome(d,
            new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0, ResizeBorderThickness = new Thickness(4),
                GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false
            });
        titleBar.MouseLeftButtonDown += (s, e) => d.DragMove();
        closeBtn.Click += (s, e) => d.Close();
        d.ShowDialog();
    }
}

/// <summary>
/// diff ビュー用の行背景レンダラー。ProposalReviewDialog と EditorPage で共用する。
/// </summary>
internal sealed class DiffLineBackgroundRenderer : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Background;
    private Dictionary<int, ChangeType> _lineTypes = new();

    private static readonly System.Windows.Media.SolidColorBrush InsertedBrush =
        new(MediaColor.FromRgb(0x1a, 0x2f, 0x1e)) { Opacity = 1.0 };
    private static readonly System.Windows.Media.SolidColorBrush DeletedBrush =
        new(MediaColor.FromRgb(0x3b, 0x1a, 0x1a)) { Opacity = 1.0 };
    private static readonly System.Windows.Media.SolidColorBrush ModifiedBrush =
        new(MediaColor.FromRgb(0x2a, 0x24, 0x12)) { Opacity = 1.0 };

    static DiffLineBackgroundRenderer()
    {
        InsertedBrush.Freeze();
        DeletedBrush.Freeze();
        ModifiedBrush.Freeze();
    }

    public void SetLineTypes(Dictionary<int, ChangeType> lineTypes)
    {
        _lineTypes = new Dictionary<int, ChangeType>(lineTypes);
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_lineTypes.Count == 0) return;
        foreach (var visualLine in textView.VisualLines)
        {
            var lineNumber = visualLine.FirstDocumentLine.LineNumber;
            if (!_lineTypes.TryGetValue(lineNumber, out var changeType)) continue;
            System.Windows.Media.Brush? brush = changeType switch
            {
                ChangeType.Inserted => InsertedBrush,
                ChangeType.Deleted  => DeletedBrush,
                ChangeType.Modified => ModifiedBrush,
                _ => null
            };
            if (brush == null) continue;
            var y      = visualLine.VisualTop - textView.ScrollOffset.Y;
            var height = visualLine.Height;
            drawingContext.DrawRectangle(brush, null, new System.Windows.Rect(0, y, textView.ActualWidth, height));
        }
    }
}
