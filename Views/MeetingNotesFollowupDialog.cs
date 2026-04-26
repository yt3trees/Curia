using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Curia.Models;
using Curia.Services;
using TextBox             = System.Windows.Controls.TextBox;
using Button              = System.Windows.Controls.Button;
using ScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility;
using Brush               = System.Windows.Media.Brush;
using Brushes             = System.Windows.Media.Brushes;
using SolidColorBrush     = System.Windows.Media.SolidColorBrush;
using FontFamily          = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace Curia.Views;

/// <summary>
/// カレンダーイベントからトリガされる会議メモ反映ダイアログ。
/// Analyze → preview summary → Apply all (+ Asana comment) の 3 ステップ。
/// </summary>
internal class MeetingNotesFollowupDialog : Window
{
    private readonly OutlookEvent _ev;
    private readonly MeetingNotesService _meetingNotesService;
    private readonly CaptureService _captureService;

    private TextBox? _notesBox;
    private StackPanel? _resultPanel;
    private Button? _analyzeBtn;
    private Button? _applyBtn;
    private TextBlock? _statusBlock;

    private MeetingAnalysisResult? _analysisResult;
    private CancellationTokenSource _cts = new();

    public MeetingNotesFollowupDialog(
        OutlookEvent ev,
        MeetingNotesService meetingNotesService,
        CaptureService captureService)
    {
        _ev = ev;
        _meetingNotesService = meetingNotesService;
        _captureService = captureService;

        Title = "Log Meeting Notes";
        Width = 580;
        MinHeight = 0;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var res = Application.Current.Resources;
        Background = res["AppSurface0"] as Brush ?? Brushes.Black;

        Content = BuildContent(res);
    }

    private UIElement BuildContent(ResourceDictionary res)
    {
        var surface1  = res["AppSurface1"] as Brush ?? Brushes.DimGray;
        var surface2  = res["AppSurface2"] as Brush ?? Brushes.Gray;
        var text      = res["AppText"]     as Brush ?? Brushes.White;
        var subtext   = res["AppSubtext0"] as Brush ?? Brushes.LightGray;
        var accent    = res.Contains("AppBlue") ? res["AppBlue"] as Brush ?? text : text;

        var root = new StackPanel { Margin = new Thickness(16) };

        // --- ヘッダー: 会議情報 ---
        var headerPanel = new Border
        {
            Background = surface1,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 12),
        };
        var headerStack = new StackPanel();
        headerStack.Children.Add(new TextBlock
        {
            Text = _ev.Subject,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = text,
        });
        headerStack.Children.Add(new TextBlock
        {
            Text = _ev.IsAllDay
                ? _ev.Start.ToString("M/d")
                : $"{_ev.Start:M/d HH:mm} - {_ev.End:HH:mm}",
            FontSize = 11,
            Foreground = subtext,
            Margin = new Thickness(0, 2, 0, 0),
        });
        if (!string.IsNullOrEmpty(_ev.LinkedTaskTitle))
        {
            headerStack.Children.Add(new TextBlock
            {
                Text = $"⛓ {_ev.LinkedProjectShortName} / {_ev.LinkedTaskTitle}",
                FontSize = 11,
                Foreground = accent,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }
        headerPanel.Child = headerStack;
        root.Children.Add(headerPanel);

        // --- 議事入力エリア ---
        root.Children.Add(new TextBlock
        {
            Text = "Meeting notes (paste your notes here):",
            FontSize = 12,
            Foreground = text,
            Margin = new Thickness(0, 0, 0, 4),
        });

        _notesBox = new TextBox
        {
            Height = 140,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = surface2,
            Foreground = text,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6),
            FontFamily = new FontFamily("Consolas, Segoe UI"),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 10),
        };
        root.Children.Add(_notesBox);

        // --- 分析ボタン ---
        _analyzeBtn = new Button
        {
            Content = "Analyze",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(0, 0, 0, 12),
        };
        _analyzeBtn.Click += OnAnalyzeClick;
        root.Children.Add(_analyzeBtn);

        // --- ステータス ---
        _statusBlock = new TextBlock
        {
            FontSize = 11,
            Foreground = subtext,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        };
        root.Children.Add(_statusBlock);

        // --- 結果サマリ (分析後に表示) ---
        _resultPanel = new StackPanel { Visibility = Visibility.Collapsed };
        root.Children.Add(_resultPanel);

        // --- Apply / Cancel ---
        _applyBtn = new Button
        {
            Content = "Apply All",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(0, 4, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        _applyBtn.Click += OnApplyClick;

        var cancelBtn = new Button
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(0, 4, 0, 0),
        };
        cancelBtn.Click += (_, _) => { _cts.Cancel(); Close(); };

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        btnRow.Children.Add(_applyBtn);
        btnRow.Children.Add(new Border { Width = 8 });
        btnRow.Children.Add(cancelBtn);
        root.Children.Add(btnRow);

        return root;
    }

    private async void OnAnalyzeClick(object sender, RoutedEventArgs e)
    {
        if (_notesBox == null || string.IsNullOrWhiteSpace(_notesBox.Text))
        {
            MessageBox.Show("Please enter meeting notes.", "Curia",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_ev.LinkedProject == null)
        {
            MessageBox.Show("Linked project not found.", "Curia",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetStatus("Analyzing with AI...");
        _analyzeBtn!.IsEnabled = false;
        _cts = new CancellationTokenSource();

        try
        {
            _analysisResult = await _meetingNotesService.AnalyzeAsync(
                _notesBox.Text.Trim(),
                _ev.LinkedProject,
                null,
                _cts.Token);

            BuildResultSummary(_analysisResult);
            _resultPanel!.Visibility = Visibility.Visible;
            _applyBtn!.Visibility = Visibility.Visible;
            SetStatus("Analysis complete. Review above and click Apply All.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled.");
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
        }
        finally
        {
            _analyzeBtn!.IsEnabled = true;
        }
    }

    private void BuildResultSummary(MeetingAnalysisResult result)
    {
        if (_resultPanel == null) return;
        _resultPanel.Children.Clear();

        var res     = Application.Current.Resources;
        var surface = res["AppSurface1"] as Brush ?? Brushes.DimGray;
        var text    = res["AppText"]     as Brush ?? Brushes.White;
        var subtext = res["AppSubtext0"] as Brush ?? Brushes.LightGray;
        var accent  = res.Contains("AppBlue") ? res["AppBlue"] as Brush ?? text : text;

        void AddSection(string header, IEnumerable<string> items)
        {
            if (!items.Any()) return;
            var section = new Border
            {
                Background = surface,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 6),
            };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = header,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = accent,
                Margin = new Thickness(0, 0, 0, 4),
            });
            foreach (var item in items)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = $"- {item}",
                    FontSize = 11,
                    Foreground = text,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 1, 0, 0),
                });
            }
            section.Child = panel;
            _resultPanel.Children.Add(section);
        }

        AddSection("Decisions",
            result.Decisions.Select(d => d.Title));

        if (result.FocusUpdate.RecentContext.Count > 0 || result.FocusUpdate.NextActions.Count > 0)
        {
            var items = result.FocusUpdate.RecentContext.Concat(result.FocusUpdate.NextActions);
            AddSection("Focus updates", items);
        }

        AddSection("Open questions / Concerns",
            result.Tensions.OpenQuestions.Concat(result.Tensions.Concerns));

        AddSection("Asana tasks",
            result.AsanaTasks.Tasks.Select(t => t.Title));

        if (!string.IsNullOrEmpty(_ev.LinkedAsanaGid))
        {
            _resultPanel.Children.Add(new TextBlock
            {
                Text = "⛓ Meeting summary will also be added as a comment to the linked Asana task.",
                FontSize = 11,
                FontStyle = FontStyles.Italic,
                Foreground = subtext,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }
    }

    private async void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (_analysisResult == null || _ev.LinkedProject == null) return;

        _applyBtn!.IsEnabled = false;
        _analyzeBtn!.IsEnabled = false;
        SetStatus("Applying...");
        _cts = new CancellationTokenSource();

        try
        {
            var project     = _ev.LinkedProject;
            var workstreamId = (string?)null;
            var ct          = _cts.Token;

            // Apply all sections (decisions/focus/openIssues は ct なし、asanaTasks は ct あり)
            await Task.WhenAll(
                _meetingNotesService.ApplyDecisionsAsync(_analysisResult, project, workstreamId),
                _meetingNotesService.ApplyFocusAsync(_analysisResult, project, workstreamId),
                _meetingNotesService.ApplyOpenIssuesAsync(_analysisResult, project, workstreamId),
                _meetingNotesService.ApplyAsanaTasksAsync(_analysisResult, project, workstreamId, ct)
            );

            // Asana タスクへのコメント追記
            if (!string.IsNullOrEmpty(_ev.LinkedAsanaGid))
            {
                var summary = BuildCommentText(_analysisResult);
                var (ok, msg) = await _captureService.AddTaskCommentAsync(
                    _ev.LinkedAsanaGid, summary, ct);
                if (!ok)
                    SetStatus($"Applied. (Asana comment failed: {msg})");
                else
                    SetStatus("Applied. Asana comment added.");
            }
            else
            {
                SetStatus("Applied successfully.");
            }

            _applyBtn.Content = "Applied";
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled.");
            _applyBtn!.IsEnabled = true;
        }
        catch (Exception ex)
        {
            SetStatus($"Error during apply: {ex.Message}");
            _applyBtn!.IsEnabled = true;
        }
        finally
        {
            _analyzeBtn!.IsEnabled = true;
        }
    }

    private static string BuildCommentText(MeetingAnalysisResult result)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Meeting notes summary (via Curia)");
        sb.AppendLine();

        if (result.Decisions.Count > 0)
        {
            sb.AppendLine("Decisions:");
            foreach (var d in result.Decisions)
                sb.AppendLine($"- {d.Title}");
            sb.AppendLine();
        }

        if (result.FocusUpdate.NextActions.Count > 0)
        {
            sb.AppendLine("Next actions:");
            foreach (var a in result.FocusUpdate.NextActions)
                sb.AppendLine($"- {a}");
            sb.AppendLine();
        }

        if (result.Tensions.OpenQuestions.Count > 0)
        {
            sb.AppendLine("Open questions:");
            foreach (var q in result.Tensions.OpenQuestions)
                sb.AppendLine($"- {q}");
        }

        return sb.ToString().TrimEnd();
    }

    private void SetStatus(string message)
    {
        if (_statusBlock == null) return;
        _statusBlock.Text = message;
        _statusBlock.Visibility = string.IsNullOrEmpty(message)
            ? Visibility.Collapsed : Visibility.Visible;
    }
}
