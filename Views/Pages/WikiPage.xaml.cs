using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using System.Text.RegularExpressions;
using Wpf.Ui.Controls;
using Curia.Services;
using Curia.ViewModels;
using WpfUserControl = System.Windows.Controls.UserControl;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfKey = System.Windows.Input.Key;
using WpfKeyboard = System.Windows.Input.Keyboard;
using WpfModifierKeys = System.Windows.Input.ModifierKeys;
using WpfDragEventArgs = System.Windows.DragEventArgs;
using WpfDragDropEffects = System.Windows.DragDropEffects;
using WpfDataFormats = System.Windows.DataFormats;
using Win32OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using MediaColor = System.Windows.Media.Color;

namespace Curia.Views.Pages;

public partial class WikiPage : WpfUserControl, INavigableView<WikiViewModel>
{
    public WikiViewModel ViewModel { get; }

    private bool _isInitialized;
    private System.Windows.Media.Brush? _tabActiveBrush;
    private System.Windows.Media.Brush? _tabInactiveBrush;
    private readonly TextEditor _wikiEditor;
    private readonly FlowDocumentScrollViewer _wikiRenderViewer;
    private bool _syncingEditor;
    private IHighlightingDefinition? _markdownHighlighting;
    private string? _lastRenderedPath;
    private readonly FlowDocumentScrollViewer _wikiQueryAnswerViewer;

    public WikiPage(WikiViewModel viewModel)
    {
        ViewModel = viewModel;
        ViewModel.OnOpenInEditor = (project, filePath) =>
        {
            if (Window.GetWindow(this) is MainWindow mw)
                mw.NavigateToEditorAndOpenFile(project, filePath);
        };
        DataContext = ViewModel;
        InitializeComponent();

        _wikiEditor = new TextEditor
        {
            FontFamily = new System.Windows.Media.FontFamily("Consolas, 'Courier New', monospace"),
            FontSize = 13,
            WordWrap = true,
            ShowLineNumbers = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        AddBottomViewportPadding(_wikiEditor);
        _wikiEditor.TextChanged += (_, _) =>
        {
            if (_syncingEditor) return;
            ViewModel.PreviewContent = _wikiEditor.Text;
        };
        _wikiEditor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == WpfKey.S && WpfKeyboard.Modifiers.HasFlag(WpfModifierKeys.Control))
            {
                ViewModel.SaveCurrentPageCommand.Execute(null);
                e.Handled = true;
            }
        };
        _wikiRenderViewer = new FlowDocumentScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsToolBarVisible = false,
            Document = new FlowDocument()
        };
        // スクロール速度の改善 (0.5倍に微調整：ちょうど1行ずつ追える心地よい速度)
        _wikiRenderViewer.PreviewMouseWheel += (s, e) =>
        {
            var sv = FindVisualChild<ScrollViewer>(_wikiRenderViewer);
            if (sv != null)
            {
                sv.ScrollToVerticalOffset(sv.VerticalOffset - (e.Delta * 0.5));
                e.Handled = true;
            }
        };
        WikiEditorHost.Content = _wikiEditor;
        WikiRenderHost.Content = _wikiRenderViewer;

        _wikiQueryAnswerViewer = new FlowDocumentScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsToolBarVisible = false,
            Document = new FlowDocument()
        };
        _wikiQueryAnswerViewer.PreviewMouseWheel += (s, e) =>
        {
            var sv = FindVisualChild<ScrollViewer>(_wikiQueryAnswerViewer);
            if (sv != null)
            {
                sv.ScrollToVerticalOffset(sv.VerticalOffset - (e.Delta * 0.5));
                e.Handled = true;
            }
        };
        WikiQueryAnswerHost.Content = _wikiQueryAnswerViewer;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized)
        {
            _isInitialized = true;

            var appRes = Application.Current.Resources;
            _tabActiveBrush   = appRes.Contains("AppSurface2")
                ? (System.Windows.Media.Brush)appRes["AppSurface2"]
                : System.Windows.Media.Brushes.Gray;
            _tabInactiveBrush = System.Windows.Media.Brushes.Transparent;
            UpdateTabStyles();
            ApplyEditorTheme();
            RegisterMarkdownHighlighting();
            _wikiEditor.SyntaxHighlighting = _markdownHighlighting;

            ViewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(WikiViewModel.ActiveTab))
                    UpdateTabStyles();
                else if (args.PropertyName == nameof(WikiViewModel.PreviewContent))
                {
                    SyncEditorFromViewModel();
                    SyncRenderFromViewModel();
                }
                else if (args.PropertyName == nameof(WikiViewModel.SelectedQueryRecord))
                    UpdateQueryAnswerView();
                else if (args.PropertyName == nameof(WikiViewModel.EditorFontSize))
                    _wikiEditor.FontSize = ViewModel.EditorFontSize;
                else if (args.PropertyName == nameof(WikiViewModel.MarkdownRenderFontSize))
                {
                    SyncRenderFromViewModel();
                    UpdateQueryAnswerView();
                }
                else if (args.PropertyName == nameof(WikiViewModel.EditorTextColor))
                    ApplyEditorTheme();
                else if (args.PropertyName == nameof(WikiViewModel.MarkdownRenderTextColor))
                {
                    SyncRenderFromViewModel();
                    UpdateQueryAnswerView();
                }
            };

            ViewModel.ConversationLog.CollectionChanged += (_, _) => UpdateQueryAnswerView();
            ViewModel.SessionPreviewRecords.CollectionChanged += (_, _) => UpdateQueryAnswerView();
            _wikiEditor.FontSize = ViewModel.EditorFontSize;
        }

        await ViewModel.EnsureInitializedAsync();
    }

    private void ResetRenderScroll()
    {
        // UIスレッドのレイアウト更新が完全に終わるのを待ってからリセット
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            var sv = FindVisualChild<ScrollViewer>(_wikiRenderViewer);
            sv?.ScrollToHome();
        });
    }

    private static T? FindVisualChild<T>(DependencyObject obj) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
        {
            var child = VisualTreeHelper.GetChild(obj, i);
            if (child is T t) return t;
            var childOfChild = FindVisualChild<T>(child);
            if (childOfChild != null) return childOfChild;
        }
        return null;
    }

    private void SyncEditorFromViewModel()
    {
        var text = ViewModel.PreviewContent ?? "";
        if (string.Equals(_wikiEditor.Text, text, StringComparison.Ordinal))
            return;

        _syncingEditor = true;
        _wikiEditor.Text = text;
        _syncingEditor = false;
    }

    private void UpdateQueryAnswerView()
    {
        if (ViewModel.SessionPreviewRecords.Count > 0)
        {
            // 過去セッションの全会話を表示
            _wikiQueryAnswerViewer.Document = BuildConversationDocument(ViewModel.SessionPreviewRecords, ViewModel.MarkdownRenderFontSize, ViewModel.MarkdownRenderTextColor);
        }
        else
        {
            // 現セッションの会話ログを表示
            _wikiQueryAnswerViewer.Document = BuildConversationDocument(ViewModel.ConversationLog, ViewModel.MarkdownRenderFontSize, ViewModel.MarkdownRenderTextColor);
            var sv = FindVisualChild<ScrollViewer>(_wikiQueryAnswerViewer);
            sv?.ScrollToEnd();
        }
    }

    private static FlowDocument BuildConversationDocument(IEnumerable<Curia.Models.WikiQueryRecord> records, double fontSize = 13, string? textColor = null)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(14, 10, 14, SystemParameters.HorizontalScrollBarHeight + 6),
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Lucida Sans Unicode, Arial"),
            FontSize = fontSize,
            LineHeight = Math.Max(fontSize + 6, 20)
        };

        doc.Foreground = ParseHexColor(textColor)
            ?? Application.Current?.Resources["AppText"] as System.Windows.Media.Brush
            ?? System.Windows.Media.Brushes.White;

        var subtext = Application.Current?.Resources["AppSubtext0"] as System.Windows.Media.Brush
                      ?? System.Windows.Media.Brushes.Gray;
        var blue    = Application.Current?.Resources["AppBlue"] as System.Windows.Media.Brush
                      ?? System.Windows.Media.Brushes.SteelBlue;
        var surface = Application.Current?.Resources["AppSurface1"] as System.Windows.Media.Brush
                      ?? System.Windows.Media.Brushes.Transparent;
        var divider = Application.Current?.Resources["AppSurface2"] as System.Windows.Media.Brush
                      ?? System.Windows.Media.Brushes.DimGray;

        bool first = true;
        foreach (var record in records)
        {
            if (!first)
            {
                doc.Blocks.Add(new Paragraph(new Run("─────────────────────────────────────"))
                {
                    Foreground = divider,
                    Margin = new Thickness(0, 6, 0, 6),
                    FontSize = 10
                });
            }
            first = false;

            // 質問ヘッダー
            doc.Blocks.Add(new Paragraph(new Run(record.Question))
            {
                FontWeight = FontWeights.SemiBold,
                FontSize = fontSize,
                Foreground = blue,
                Margin = new Thickness(0, 0, 0, 4)
            });

            // 回答 (Markdown パース)
            var answerDoc = BuildMarkdownDocument(record.Answer ?? "", fontSize, textColor);
            foreach (var block in answerDoc.Blocks.ToList())
            {
                answerDoc.Blocks.Remove(block);
                doc.Blocks.Add(block);
            }

            // 参照ページ (あれば)
            if (record.ReferencedPages.Count > 0)
            {
                doc.Blocks.Add(new Paragraph(
                    new Run("Refs: " + string.Join(", ", record.ReferencedPages)))
                {
                    FontSize = 10,
                    Foreground = subtext,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }
        }

        return doc;
    }

    private void SyncRenderFromViewModel()
    {
        _wikiRenderViewer.Document = BuildMarkdownDocument(ViewModel.PreviewContent ?? "", ViewModel.MarkdownRenderFontSize, ViewModel.MarkdownRenderTextColor);

        // ファイルが切り替わった時のみスクロールをリセット（編集中に上に戻るのを防ぐ）
        if (_lastRenderedPath != ViewModel.SelectedPagePath)
        {
            _lastRenderedPath = ViewModel.SelectedPagePath;
            ResetRenderScroll();
        }
    }

    private void ApplyEditorTheme()
    {
        var resources = Application.Current.Resources;
        _wikiEditor.Background = resources["EditorBackground"] as System.Windows.Media.Brush
            ?? resources["AppSurface0"] as System.Windows.Media.Brush
            ?? System.Windows.Media.Brushes.Black;
        _wikiEditor.Foreground = ParseHexColor(ViewModel.EditorTextColor)
            ?? resources["EditorForeground"] as System.Windows.Media.Brush
            ?? resources["AppText"] as System.Windows.Media.Brush
            ?? System.Windows.Media.Brushes.White;
        _wikiEditor.LineNumbersForeground = resources["AppSubtext0"] as System.Windows.Media.Brush
            ?? System.Windows.Media.Brushes.Gray;
    }

    private static System.Windows.Media.SolidColorBrush? ParseHexColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { return new System.Windows.Media.SolidColorBrush((MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(hex)); }
        catch { return null; }
    }

    private static void AddBottomViewportPadding(TextEditor editor)
    {
        editor.Options.AllowScrollBelowDocument = false;
        var margin = editor.TextArea.TextView.Margin;
        editor.TextArea.TextView.Margin = new Thickness(
            margin.Left,
            margin.Top,
            margin.Right,
            SystemParameters.HorizontalScrollBarHeight + 2);
    }

    private static FlowDocument BuildMarkdownDocument(string markdown, double fontSize = 13, string? textColor = null)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(14, 10, 14, SystemParameters.HorizontalScrollBarHeight + 6),
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Lucida Sans Unicode, Arial"),
            FontSize = fontSize,
            LineHeight = Math.Max(fontSize + 6, 20)
        };

        doc.Foreground = ParseHexColor(textColor)
            ?? Application.Current?.Resources["AppText"] as System.Windows.Media.Brush
            ?? System.Windows.Media.Brushes.White;

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var inCode = false;
        var startIdx = 0;

        // --- Front Matter Parsing ---
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            var endIdx = -1;
            for (int j = 1; j < lines.Length; j++)
            {
                if (lines[j].Trim() == "---") { endIdx = j; break; }
            }

            if (endIdx != -1)
            {
                var frontMatterLines = lines.Skip(1).Take(endIdx - 1).ToList();
                var metadata = ParseFrontMatter(frontMatterLines);
                if (metadata.Count > 0)
                {
                    doc.Blocks.Add(BuildFrontMatterCard(metadata, fontSize));
                }
                startIdx = endIdx + 1;
            }
        }

        for (int i = startIdx; i < lines.Length; i++)
        {
            var line = lines[i] ?? string.Empty;
            var trimmed = line.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
                continue;
            }

            if (inCode)
            {
                var p = new Paragraph(new Run(line))
                {
                    FontFamily = new System.Windows.Media.FontFamily("Consolas, 'Courier New', monospace"),
                    FontSize = 12, // コードブロックは少し小さめが綺麗
                    Background = new SolidColorBrush(MediaColor.FromRgb(0x1f, 0x24, 0x2d)),
                    Foreground = System.Windows.Media.Brushes.Gainsboro,
                    Margin = new Thickness(0, 0, 0, 2),
                    Padding = new Thickness(6, 2, 6, 2)
                };
                doc.Blocks.Add(p);
                continue;
            }

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                // Skip blank lines; paragraph margins provide spacing
                continue;
            }

            // Table support
            if (trimmed.StartsWith("|", StringComparison.Ordinal) && i + 1 < lines.Length)
            {
                var nextLine = lines[i + 1]?.Trim() ?? "";
                if (nextLine.StartsWith("|", StringComparison.Ordinal) && nextLine.Contains("---"))
                {
                    var table = ParseTable(lines, ref i);
                    if (table != null)
                    {
                        doc.Blocks.Add(table);
                        continue;
                    }
                }
            }

            if (trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                var level = Math.Min(6, trimmed.TakeWhile(c => c == '#').Count());
                var text = trimmed[level..].Trim();
                // 見出しサイズはベースフォントサイズに対して相対的に決定
                var scale = level switch { 1 => 1.54d, 2 => 1.38d, 3 => 1.23d, 4 => 1.08d, _ => 1d };
                var size = Math.Round(fontSize * scale);
                var headingTopMargin = level <= 2 ? 14d : 10d;
                doc.Blocks.Add(new Paragraph(new Run(text))
                {
                    FontWeight = FontWeights.SemiBold,
                    FontSize = size,
                    Foreground = new SolidColorBrush(MediaColor.FromRgb(0x79, 0xc0, 0xff)),
                    Margin = new Thickness(0, headingTopMargin, 0, 4)
                });
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                var indentLen = line.Length - line.TrimStart().Length;
                var depth = indentLen / 2;
                var bullet = depth == 0 ? "•" : depth == 1 ? "◦" : "▪";
                var leftMargin = 10 + depth * 16;
                doc.Blocks.Add(new Paragraph(new Run(bullet + " " + trimmed[2..].Trim()))
                {
                    Margin = new Thickness(leftMargin, 0, 0, 3)
                });
                continue;
            }

            var ordered = ParseOrderedListLine(trimmed);
            if (ordered != null)
            {
                var indentLen = line.Length - line.TrimStart().Length;
                var depth = indentLen / 2;
                var leftMargin = 10 + depth * 16;
                doc.Blocks.Add(new Paragraph(new Run(ordered))
                {
                    Margin = new Thickness(leftMargin, 0, 0, 3)
                });
                continue;
            }

            if (trimmed.StartsWith("> ", StringComparison.Ordinal))
            {
                doc.Blocks.Add(new Paragraph(new Run(trimmed[2..]))
                {
                    Margin = new Thickness(12, 0, 0, 5),
                    Foreground = new SolidColorBrush(MediaColor.FromRgb(0x9b, 0xd0, 0x9b))
                });
                continue;
            }

            doc.Blocks.Add(new Paragraph(new Run(line))
            {
                Margin = new Thickness(0, 0, 0, 6)
            });
        }

        return doc;
    }

    private static Table? ParseTable(string[] lines, ref int i)
    {
        var headerLine = lines[i];
        var headerCells = ExtractCells(headerLine);
        if (headerCells.Length == 0) return null;

        var table = new Table
        {
            CellSpacing = 0,
            BorderBrush = Application.Current?.Resources["AppSurface2"] as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray,
            BorderThickness = new Thickness(0, 1, 0, 1),
            Margin = new Thickness(0, 8, 0, 12)
        };

        for (int c = 0; c < headerCells.Length; c++)
        {
            table.Columns.Add(new TableColumn());
        }

        var headerRowGroup = new TableRowGroup();
        var headerRow = new TableRow();
        foreach (var cell in headerCells)
        {
            var p = new Paragraph(new Run(cell))
            {
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0)
            };
            var tableCell = new TableCell(p)
            {
                Background = new SolidColorBrush(MediaColor.FromRgb(0x1f, 0x24, 0x2d)),
                Foreground = System.Windows.Media.Brushes.Gainsboro,
                BorderBrush = table.BorderBrush,
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            headerRow.Cells.Add(tableCell);
        }
        headerRowGroup.Rows.Add(headerRow);
        table.RowGroups.Add(headerRowGroup);

        var bodyRowGroup = new TableRowGroup();
        i += 2; // skip header and separator

        while (i < lines.Length)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("|", StringComparison.Ordinal)) break;

            var cells = ExtractCells(line);
            var row = new TableRow();
            for (int c = 0; c < headerCells.Length; c++)
            {
                var cellText = c < cells.Length ? cells[c] : "";
                var p = new Paragraph(new Run(cellText))
                {
                    Padding = new Thickness(8, 4, 8, 4),
                    Margin = new Thickness(0)
                };
                row.Cells.Add(new TableCell(p)
                {
                    BorderBrush = table.BorderBrush,
                    BorderThickness = new Thickness(0, 0, 0, 1)
                });
            }
            bodyRowGroup.Rows.Add(row);
            i++;
        }
        i--; // back up one so the outer loop points to the last table line, then increments

        table.RowGroups.Add(bodyRowGroup);
        return table;
    }

    private static string[] ExtractCells(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith("|", StringComparison.Ordinal)) trimmed = trimmed[1..];
        if (trimmed.EndsWith("|", StringComparison.Ordinal)) trimmed = trimmed[..^1];
        return trimmed.Split('|').Select(c => c.Trim()).ToArray();
    }

    private static string? ParseOrderedListLine(string trimmed)
    {
        if (trimmed.Length < 3 || !char.IsDigit(trimmed[0])) return null;
        var idx = 0;
        while (idx < trimmed.Length && char.IsDigit(trimmed[idx])) idx++;
        if (idx + 1 >= trimmed.Length || trimmed[idx] != '.' || trimmed[idx + 1] != ' ') return null;
        return trimmed;
    }

    private void RegisterMarkdownHighlighting()
    {
        if (_markdownHighlighting != null) return;

        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("Markdown.xshd", StringComparison.OrdinalIgnoreCase));
            if (resourceName == null) return;

            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null) return;
            using var reader = XmlReader.Create(stream);
            _markdownHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            HighlightingManager.Instance.RegisterHighlighting("Markdown", new[] { ".md", ".markdown" }, _markdownHighlighting);
        }
        catch
        {
            _markdownHighlighting = BuildFallbackMarkdownHighlighting();
        }

        _markdownHighlighting ??= BuildFallbackMarkdownHighlighting();
    }

    private static IHighlightingDefinition BuildFallbackMarkdownHighlighting()
    {
        static HighlightingColor Clr(byte r, byte g, byte b) =>
            new() { Foreground = new SimpleHighlightingBrush(MediaColor.FromRgb(r, g, b)) };
        static HighlightingColor ClrBold(byte r, byte g, byte b) =>
            new() { Foreground = new SimpleHighlightingBrush(MediaColor.FromRgb(r, g, b)), FontWeight = FontWeights.Bold };
        static HighlightingColor ClrItalic(byte r, byte g, byte b) =>
            new() { Foreground = new SimpleHighlightingBrush(MediaColor.FromRgb(r, g, b)), FontStyle = FontStyles.Italic };
        static HighlightingColor ClrBoldItalic(byte r, byte g, byte b) =>
            new() { Foreground = new SimpleHighlightingBrush(MediaColor.FromRgb(r, g, b)), FontWeight = FontWeights.Bold, FontStyle = FontStyles.Italic };

        var heading    = ClrBold(0x58, 0xa6, 0xff);
        var code       = Clr(0xff, 0xa6, 0x57);
        var emphasis   = ClrItalic(0xff, 0xa8, 0xcc);
        var strong     = ClrBold(0xff, 0xa8, 0xcc);
        var boldItalic = ClrBoldItalic(0xff, 0xa8, 0xcc);
        var blockquote = Clr(0x7e, 0xe7, 0x87);
        var link       = Clr(0x79, 0xc0, 0xff);
        var list       = Clr(0xf0, 0x88, 0x3e);
        var comment    = Clr(0x8b, 0x94, 0x9e);

        var rs = new HighlightingRuleSet();
        rs.Rules.Add(new HighlightingRule { Color = heading, Regex = new Regex(@"^\#{1,6}[^\n]*", RegexOptions.Multiline) });
        rs.Rules.Add(new HighlightingRule { Color = blockquote, Regex = new Regex(@"^\s*>.*", RegexOptions.Multiline) });
        rs.Rules.Add(new HighlightingRule
        {
            Color = new HighlightingColor { Foreground = new SimpleHighlightingBrush(MediaColor.FromRgb(0x48, 0x4f, 0x58)) },
            Regex = new Regex(@"^(\-{3,}|\*{3,}|_{3,})\s*$", RegexOptions.Multiline)
        });
        rs.Rules.Add(new HighlightingRule { Color = link, Regex = new Regex(@"!\[.*?\]\([^\)]*\)") });
        rs.Rules.Add(new HighlightingRule { Color = link, Regex = new Regex(@"\[.*?\]\([^\)]*\)") });
        rs.Rules.Add(new HighlightingRule { Color = list, Regex = new Regex(@"^\s*[\*\+\-]\s", RegexOptions.Multiline) });
        rs.Rules.Add(new HighlightingRule { Color = list, Regex = new Regex(@"^\s*\d+\.\s", RegexOptions.Multiline) });

        rs.Spans.Add(new HighlightingSpan { StartColor = comment, SpanColor = comment, EndColor = comment, StartExpression = new Regex(@"<!--"), EndExpression = new Regex(@"-->") });
        rs.Spans.Add(new HighlightingSpan { StartColor = code, SpanColor = code, EndColor = code, StartExpression = new Regex("```"), EndExpression = new Regex("```") });
        rs.Spans.Add(new HighlightingSpan { StartColor = code, SpanColor = code, EndColor = code, StartExpression = new Regex("`"), EndExpression = new Regex("`") });
        rs.Spans.Add(new HighlightingSpan { StartColor = boldItalic, SpanColor = boldItalic, EndColor = boldItalic, StartExpression = new Regex(@"\*\*\*"), EndExpression = new Regex(@"\*\*\*") });
        rs.Spans.Add(new HighlightingSpan { StartColor = strong, SpanColor = strong, EndColor = strong, StartExpression = new Regex(@"\*\*"), EndExpression = new Regex(@"\*\*") });
        rs.Spans.Add(new HighlightingSpan { StartColor = emphasis, SpanColor = emphasis, EndColor = emphasis, StartExpression = new Regex(@"\*"), EndExpression = new Regex(@"\*") });

        return new WikiMarkdownHighlightingDefinition(rs);
    }

    private sealed class WikiMarkdownHighlightingDefinition : IHighlightingDefinition
    {
        private readonly HighlightingRuleSet _mainRuleSet;
        public WikiMarkdownHighlightingDefinition(HighlightingRuleSet mainRuleSet) => _mainRuleSet = mainRuleSet;
        public string Name => "Markdown";
        public HighlightingRuleSet MainRuleSet => _mainRuleSet;
        public IEnumerable<HighlightingColor> NamedHighlightingColors => [];
        public IDictionary<string, string> Properties { get; } = new Dictionary<string, string>();
        public HighlightingColor GetNamedColor(string name) => null!;
        public HighlightingRuleSet GetNamedRuleSet(string name) => null!;
    }

    private void UpdateTabStyles()
    {
        var active   = _tabActiveBrush   ?? System.Windows.Media.Brushes.Gray;
        var inactive = _tabInactiveBrush ?? System.Windows.Media.Brushes.Transparent;
        var tab      = ViewModel.ActiveTab;

        PagesTabBtn.Background   = tab == WikiTab.Pages   ? active : inactive;
        QueryTabBtn.Background   = tab == WikiTab.Query   ? active : inactive;
        LintTabBtn.Background    = tab == WikiTab.Lint    ? active : inactive;
        PromptsTabBtn.Background = tab == WikiTab.Prompts ? active : inactive;
    }

    // ── Page tree selection ───────────────────────────────────────────────────

    private void OnTreeViewSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is WikiTreeItem item)
        {
            ViewModel.SelectedTreeItem = item;
        }
    }

    private void OnPageItemSelected(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is WikiTreeItem item)
        {
            ViewModel.SelectedTreeItem = item;
            if (sender is System.Windows.Controls.ListBox lb)
                lb.SelectedItem = null;
        }
    }

    // ── Terminal ─────────────────────────────────────────────────────────────

    private void OnWikiTermClick(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenTerminalCommand.Execute(null);
    }

    private void OnWikiTermMenuAgent(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem mi)
            ViewModel.OpenAgentTerminal(mi.Tag as string ?? "");
    }

    // ── Query Enter key ──────────────────────────────────────────────────────

    private void OnQueryKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == WpfKey.Enter && !WpfKeyboard.Modifiers.HasFlag(WpfModifierKeys.Shift))
        {
            ViewModel.RunQueryCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ── Wiki Schema Ctrl+S ───────────────────────────────────────────────────

    private void OnWikiSchemaKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == WpfKey.S && WpfKeyboard.Modifiers.HasFlag(WpfModifierKeys.Control))
        {
            ViewModel.SaveWikiSchemaCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ── Import button ─────────────────────────────────────────────────────────

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Win32OpenFileDialog
        {
            Title = "Select source files to import into Wiki",
            Filter = "Supported files|*.md;*.txt;*.pdf;*.docx|Markdown|*.md|Text|*.txt|PDF|*.pdf|Word|*.docx|All files|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog() != true) return;

        var owner = Window.GetWindow(this);
        var (ok, prompt) = ImportPromptDialog.Show(owner, dlg.FileNames);
        if (!ok) return;

        ViewModel.IngestSourceCommand.Execute((dlg.FileNames, prompt));
    }

    // ── Drag & drop ──────────────────────────────────────────────────────────

    private void OnDragEnter(object sender, WpfDragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(WpfDataFormats.FileDrop)
            ? WpfDragDropEffects.Copy
            : WpfDragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, WpfDragEventArgs e)
    {
        if (!e.Data.GetDataPresent(WpfDataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(WpfDataFormats.FileDrop);
        if (files == null || files.Length == 0) return;

        var supportedFiles = files.Where(f =>
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            return ext is ".md" or ".txt" or ".pdf" or ".docx";
        }).ToArray();

        if (supportedFiles.Length > 0)
        {
            var owner = Window.GetWindow(this);
            var (ok, prompt) = ImportPromptDialog.Show(owner, supportedFiles);
            if (ok)
                ViewModel.IngestSourceCommand.Execute((supportedFiles, prompt));
        }
        else
        {
            ViewModel.StatusText = "No supported files found in drop.";
        }
        e.Handled = true;
    }

    // ── Front Matter Helpers ──────────────────────────────────────────────────

    private static Dictionary<string, string> ParseFrontMatter(List<string> lines)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var parts = line.Split(':', 2);
            if (parts.Length != 2) continue;

            var key = parts[0].Trim();
            var val = parts[1].Trim();

            // シンプルなクォート除去
            if (val.StartsWith("\"") && val.EndsWith("\"")) val = val[1..^1];
            if (val.StartsWith("'") && val.EndsWith("'")) val = val[1..^1];

            result[key] = val;
        }
        return result;
    }

    private static BlockUIContainer BuildFrontMatterCard(Dictionary<string, string> metadata, double fontSize)
    {
        var border = new Border
        {
            Background = Application.Current?.Resources["AppSurface1"] as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Transparent,
            BorderBrush = Application.Current?.Resources["AppSurface2"] as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 4, 0, 12)
        };

        var grid = new Grid();
        // 1列目はAutoに、2列目との間に固定のスペース(40px)を設ける
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) }); 
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var leftStack = new StackPanel { Orientation = Orientation.Vertical };
        var rightStack = new StackPanel { Orientation = Orientation.Vertical };
        Grid.SetColumn(leftStack, 0);
        Grid.SetColumn(rightStack, 2);
        grid.Children.Add(leftStack);
        grid.Children.Add(rightStack);

        var subtextBrush = Application.Current?.Resources["AppSubtext0"] as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;
        var textBrush = Application.Current?.Resources["AppText"] as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White;

        void AddInfo(StackPanel panel, string icon, string label, string key)
        {
            if (!metadata.TryGetValue(key, out var val)) return;

            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            
            // アイコン
            sp.Children.Add(new System.Windows.Controls.TextBlock { 
                Text = icon, 
                FontSize = fontSize, 
                Width = 22, 
                Foreground = subtextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            });

            // ラベル (上部に少しマージンを入れて視覚的な高さを調整)
            sp.Children.Add(new System.Windows.Controls.TextBlock { 
                Text = label + ":  ", 
                FontSize = fontSize - 2, 
                Foreground = subtextBrush, 
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 1.5, 0, 0) 
            });
            
            // 値の整形
            var displayVal = val;
            if (displayVal.StartsWith("[") && displayVal.EndsWith("]"))
                displayVal = displayVal[1..^1].Replace("\"", "").Replace("'", "");

            // 値 (ラベルと同様に高さを調整)
            sp.Children.Add(new System.Windows.Controls.TextBlock { 
                Text = displayVal, 
                FontSize = fontSize - 1, 
                Foreground = textBrush, 
                FontWeight = FontWeights.Medium,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 400,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 1.5, 0, 0),
                ToolTip = new System.Windows.Controls.ToolTip
                {
                    Content = new System.Windows.Controls.TextBlock
                    {
                        Text = displayVal,
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 600
                    }
                }
            });
            panel.Children.Add(sp);
        }

        AddInfo(leftStack, "📅", "Created", "created");
        AddInfo(leftStack, "📝", "Updated", "updated");
        AddInfo(rightStack, "🔗", "Sources", "sources");
        AddInfo(rightStack, "🏷️", "Tags", "tags");

        border.Child = grid;
        return new BlockUIContainer(border);
    }
}
