using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;
using ICSharpCode.AvalonEdit.Highlighting;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Wpf.Ui.Controls;
using Curia.ViewModels;
using Curia.Views;
using Curia.Services;
using WpfUserControl = System.Windows.Controls.UserControl;
using WpfKeyEventHandler = System.Windows.Input.KeyEventHandler;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Curia.Views.Pages;

public partial class EditorPage : WpfUserControl, INavigableView<EditorViewModel>
{
    public EditorViewModel ViewModel { get; }

    private readonly TextEditor _editor;
    private TextEditor? _diffViewer;
    private DiffLineBackgroundRenderer? _diffRenderer;
    private IHighlightingDefinition? _markdownDefinition;
    private readonly CaptureService _captureService;

    public EditorPage(EditorViewModel viewModel, CaptureService captureService)
    {
        ViewModel = viewModel;
        _captureService = captureService;
        DataContext = ViewModel;

        // AvalonEdit インスタンスを生成
        _editor = new TextEditor
        {
            FontFamily = new System.Windows.Media.FontFamily("JetBrains Mono, Cascadia Mono, Consolas"),
            FontSize = 14,
            WordWrap = true,
            ShowLineNumbers = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        AddBottomViewportPadding(_editor);

        // テキスト変更イベント
        _editor.TextChanged += (s, e) =>
        {
            ViewModel.NotifyTextChanged(_editor.Text);
        };

        // Ctrl+Click でリンクをブラウザで開く
        _editor.TextArea.PreviewMouseDown += OnEditorMouseDown;
        _editor.PreviewMouseWheel += OnEditorPreviewMouseWheel;

        InitializeComponent();

        // ContentPresenter に AvalonEdit をバインド
        EditorHost.Content = _editor;

        // 右クリックコンテキストメニュー (Work Folder パス挿入)
        var contextMenu = new ContextMenu();
        contextMenu.Opened += OnEditorContextMenuOpened;
        _editor.ContextMenu = contextMenu;

        // decision_log 新規入力ダイアログ
        ViewModel.RequestNewDecisionLogName = ShowNewDecisionLogDialog;

        // Update Focus ダイアログ
        ViewModel.RequestWorkstreamSelection = ShowWorkstreamSelectionDialogAsync;
        ViewModel.RequestFocusUpdateApproval = (proposal, refineFunc) =>
            ShowFocusUpdateProposalDialogAsync(proposal, refineFunc);
        ViewModel.ShowScrollableError = (t, m) => ShowScrollableErrorDialog(t, m);

        // AI Decision Log ダイアログ
        ViewModel.RequestAiDecisionLogInput = (candidates, prefill) => ShowAiDecisionLogInputDialogAsync(candidates, prefill);
        ViewModel.RequestDecisionLogPreview = (draft, refineFunc) =>
            ShowDecisionLogPreviewDialogAsync(draft, refineFunc);

        // Meeting Notes Import ダイアログ
        ViewModel.RequestMeetingNotesInput   = ShowMeetingNotesInputDialogAsync;
        ViewModel.RequestMeetingNotesPreview = (result, project, workstreamId) =>
            ShowMeetingNotesPreviewDialogAsync(result, project, workstreamId);

        // キーバインド:
        // handledEventsToo=true で子コントロールが処理済みのキーも捕捉する。
        AddHandler(Keyboard.PreviewKeyDownEvent, new WpfKeyEventHandler(OnPageKeyDown), handledEventsToo: true);

        // CurrentFile が変わったら必ずエディタ同期 + ハイライト適用
        // (OpenFileAndSelectNodeAsync 経由で SelectedItemChanged が発火しないケースをカバー)
        ViewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(EditorViewModel.CurrentFile)
                && !string.IsNullOrEmpty(ViewModel.CurrentFile))
            {
                _editor.Text = ViewModel.EditorText;
                ApplyHighlighting(ViewModel.CurrentFile);
            }
            else if (e.PropertyName == nameof(EditorViewModel.IsDiffViewActive))
            {
                if (ViewModel.IsDiffViewActive)
                    ShowDiffView();
                else
                    HideDiffView();
            }
            else if (e.PropertyName == nameof(EditorViewModel.EditorFontSize))
            {
                _editor.FontSize = ViewModel.EditorFontSize;
            }
            else if (e.PropertyName == nameof(EditorViewModel.EditorTextColor))
            {
                ApplyEditorTheme();
            }
        };
    }

    // -------------------------------------------------------------------------
    // ページロード
    // -------------------------------------------------------------------------
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // ハイライト定義を一度だけ登録
        RegisterMarkdownHighlighting();

        await ViewModel.EnsureInitializedAsync();
        ApplyEditorTheme();
        _editor.FontSize = ViewModel.EditorFontSize;
    }

    private void ApplyEditorTheme()
    {
        try
        {
            var bg = Application.Current.Resources["EditorBackground"] as System.Windows.Media.SolidColorBrush;
            var fg = ParseHexColor(ViewModel.EditorTextColor)
                  ?? Application.Current.Resources["EditorForeground"] as System.Windows.Media.SolidColorBrush
                  ?? new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#c9d1d9"));

            bg ??= new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0d1117"));

            _editor.Background = bg;
            _editor.Foreground = fg;
            _editor.LineNumbersForeground = (Application.Current.Resources["AppSubtext0"] as System.Windows.Media.SolidColorBrush)
                ?? new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#8b949e"));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EditorPage] テーマ適用エラー: {ex.Message}");
        }
    }

    private static System.Windows.Media.SolidColorBrush? ParseHexColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { return new System.Windows.Media.SolidColorBrush((MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(hex)); }
        catch { return null; }
    }

    // -------------------------------------------------------------------------
    // Markdown ハイライト定義の登録
    // -------------------------------------------------------------------------
    private void RegisterMarkdownHighlighting()
    {
        if (_markdownDefinition != null) return;
        try
        {
            static HighlightingColor Clr(byte r, byte g, byte b) =>
                new() { Foreground = new SimpleHighlightingBrush(MediaColor.FromRgb(r, g, b)) };
            static HighlightingColor ClrBold(byte r, byte g, byte b) =>
                new() { Foreground = new SimpleHighlightingBrush(MediaColor.FromRgb(r, g, b)), FontWeight = FontWeights.Bold };
            static HighlightingColor ClrItalic(byte r, byte g, byte b) =>
                new() { Foreground = new SimpleHighlightingBrush(MediaColor.FromRgb(r, g, b)), FontStyle = FontStyles.Italic };
            static HighlightingColor ClrBoldItalic(byte r, byte g, byte b) =>
                new() { Foreground = new SimpleHighlightingBrush(MediaColor.FromRgb(r, g, b)), FontWeight = FontWeights.Bold, FontStyle = FontStyles.Italic };

            var heading    = ClrBold   (0x58, 0xa6, 0xff);
            var code       = Clr       (0xff, 0xa6, 0x57);
            var emphasis   = ClrItalic (0xff, 0xa8, 0xcc);
            var strong     = ClrBold   (0xff, 0xa8, 0xcc);
            var boldItalic = ClrBoldItalic(0xff, 0xa8, 0xcc);
            var blockquote = Clr       (0x7e, 0xe7, 0x87);
            var link       = Clr       (0x79, 0xc0, 0xff);
            var list       = Clr       (0xf0, 0x88, 0x3e);
            var comment    = Clr       (0x8b, 0x94, 0x9e);

            var rs = new HighlightingRuleSet();

            // Headings
            rs.Rules.Add(new HighlightingRule { Color = heading,    Regex = new Regex(@"^\#{1,6}[^\n]*",   RegexOptions.Multiline) });
            // Block quote
            rs.Rules.Add(new HighlightingRule { Color = blockquote, Regex = new Regex(@"^\s*>.*",          RegexOptions.Multiline) });
            // Horizontal rule
            rs.Rules.Add(new HighlightingRule { Color = new HighlightingColor { Foreground = new SimpleHighlightingBrush(MediaColor.FromRgb(0x48, 0x4f, 0x58)) },
                                                Regex = new Regex(@"^(\-{3,}|\*{3,}|_{3,})\s*$", RegexOptions.Multiline) });
            // Image (before link)
            rs.Rules.Add(new HighlightingRule { Color = link, Regex = new Regex(@"!\[.*?\]\([^\)]*\)") });
            // Link
            rs.Rules.Add(new HighlightingRule { Color = link, Regex = new Regex(@"\[.*?\]\([^\)]*\)") });
            // Unordered list
            rs.Rules.Add(new HighlightingRule { Color = list, Regex = new Regex(@"^\s*[\*\+\-]\s", RegexOptions.Multiline) });
            // Ordered list
            rs.Rules.Add(new HighlightingRule { Color = list, Regex = new Regex(@"^\s*\d+\.\s",   RegexOptions.Multiline) });

            // HTML comment: <!-- ... -->
            rs.Spans.Add(new HighlightingSpan { StartColor = comment, SpanColor = comment, EndColor = comment,
                StartExpression = new Regex(@"<!--"), EndExpression = new Regex(@"-->") });

            // Fenced code block (before inline code)
            rs.Spans.Add(new HighlightingSpan { StartColor = code, SpanColor = code, EndColor = code,
                StartExpression = new Regex("```"), EndExpression = new Regex("```") });
            // Inline code
            rs.Spans.Add(new HighlightingSpan { StartColor = code, SpanColor = code, EndColor = code,
                StartExpression = new Regex("`"), EndExpression = new Regex("`") });
            // Bold italic: ***
            rs.Spans.Add(new HighlightingSpan { StartColor = boldItalic, SpanColor = boldItalic, EndColor = boldItalic,
                StartExpression = new Regex(@"\*\*\*"), EndExpression = new Regex(@"\*\*\*") });
            // Bold: **
            rs.Spans.Add(new HighlightingSpan { StartColor = strong, SpanColor = strong, EndColor = strong,
                StartExpression = new Regex(@"\*\*"), EndExpression = new Regex(@"\*\*") });
            // Italic: *
            rs.Spans.Add(new HighlightingSpan { StartColor = emphasis, SpanColor = emphasis, EndColor = emphasis,
                StartExpression = new Regex(@"\*"), EndExpression = new Regex(@"\*") });

            _markdownDefinition = new MarkdownHighlightingDefinition(rs);
            System.Diagnostics.Debug.WriteLine("[EditorPage] Markdown ハイライト定義作成成功");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EditorPage] Markdown ハイライト定義エラー: {ex.Message}");
        }
    }

    private void ApplyHighlighting(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLower();
        if (ext is ".md" or ".markdown")
        {
            if (_markdownDefinition == null)
                RegisterMarkdownHighlighting();
            _editor.SyntaxHighlighting = _markdownDefinition;
        }
        else
        {
            _editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinitionByExtension(ext);
        }
    }

    private sealed class MarkdownHighlightingDefinition : IHighlightingDefinition
    {
        private readonly HighlightingRuleSet _mainRuleSet;
        public MarkdownHighlightingDefinition(HighlightingRuleSet mainRuleSet) => _mainRuleSet = mainRuleSet;
        public string Name => "Markdown";
        public HighlightingRuleSet MainRuleSet => _mainRuleSet;
        public IEnumerable<HighlightingColor> NamedHighlightingColors => [];
        public IDictionary<string, string> Properties { get; } = new Dictionary<string, string>();
        public HighlightingColor GetNamedColor(string name) => null!;
        public HighlightingRuleSet GetNamedRuleSet(string name) => null!;
    }

    // -------------------------------------------------------------------------
    // イベントハンドラ
    // -------------------------------------------------------------------------
    private async void OnProjectSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await Task.CompletedTask;
    }

    private async void OnTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FileTreeNode node && !node.IsDirectory)
        {
            ViewModel.SelectedNode = node;
            await ViewModel.OpenFileAsync(node.FullPath);
            // CurrentFile PropertyChanged がエディタ同期とハイライト適用を担う
        }
    }

    private void OnTreeItemPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.TreeViewItem treeViewItem)
            treeViewItem.IsSelected = true;
    }

    private void OnFileTreeContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;

        var selectedNode = FileTreeView.SelectedItem as FileTreeNode;
        var addItem = menu.Items.OfType<System.Windows.Controls.MenuItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, "AddObsidianNote", StringComparison.Ordinal));
        var deleteItem = menu.Items.OfType<System.Windows.Controls.MenuItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, "DeleteNode", StringComparison.Ordinal));

        if (addItem != null)
            addItem.IsEnabled = ViewModel.CanAddObsidianNote(selectedNode);
        if (deleteItem != null)
            deleteItem.IsEnabled = ViewModel.CanDeleteObsidianNote(selectedNode) || CanDeleteDecisionLog(selectedNode);
    }

    private async void OnAddObsidianNote(object sender, RoutedEventArgs e)
    {
        var selectedNode = FileTreeView.SelectedItem as FileTreeNode;
        if (!ViewModel.CanAddObsidianNote(selectedNode))
        {
            MessageBox.Show(
                "Select a folder (or file) under obsidian_notes first.",
                "Add Note",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var fileName = await ShowNewObsidianNoteDialog();
        if (string.IsNullOrWhiteSpace(fileName)) return;

        var result = await ViewModel.CreateObsidianNoteAsync(selectedNode, fileName);
        if (!result.Ok)
        {
            MessageBox.Show(
                $"Failed to create note:\n{result.Error}",
                "Add Note",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnDeleteNode(object sender, RoutedEventArgs e)
    {
        var selectedNode = FileTreeView.SelectedItem as FileTreeNode;
        if (ViewModel.CanDeleteObsidianNote(selectedNode))
        {
            var confirm = MessageBox.Show(
                $"Delete '{selectedNode!.Name}'?",
                "Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            var result = ViewModel.DeleteObsidianNote(selectedNode);
            if (!result.Ok)
            {
                MessageBox.Show(
                    $"Failed to delete:\n{result.Error}",
                    "Delete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            return;
        }

        if (CanDeleteDecisionLog(selectedNode))
        {
            ViewModel.DeleteDecisionLogCommand.Execute(selectedNode);
            return;
        }

        MessageBox.Show(
            "This item cannot be deleted from this menu.",
            "Delete",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static bool CanDeleteDecisionLog(FileTreeNode? node)
    {
        if (node == null || node.IsDirectory) return false;
        return node.FullPath.Contains("decision_log", StringComparison.OrdinalIgnoreCase);
    }

    private async void OnPageKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.S:
                    e.Handled = true;
                    ViewModel.NotifyTextChanged(_editor.Text);
                    await ViewModel.SaveAsync();
                    break;
                case Key.D:
                    e.Handled = true;
                    ViewModel.IsDiffViewActive = !ViewModel.IsDiffViewActive;
                    break;
                case Key.F:
                    e.Handled = true;
                    if (ViewModel.IsSearchBarVisible)
                    {
                        ViewModel.IsSearchBarVisible = false;
                        _editor.Focus();
                    }
                    else
                    {
                        ViewModel.IsSearchBarVisible = true;
                        SearchTextBox.Focus();
                    }
                    break;
            }
        }
        else if (e.Key == Key.Escape && ViewModel.IsSearchBarVisible)
        {
            ViewModel.IsSearchBarVisible = false;
            _editor.Focus();
            e.Handled = true;
        }
    }

    // -------------------------------------------------------------------------
    // 検索・リンク・ダイアログ
    // -------------------------------------------------------------------------
    private void OnSearchKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Enter) { FindText(forward: Keyboard.Modifiers != ModifierKeys.Shift); e.Handled = true; }
        else if (e.Key == Key.Escape) { ViewModel.IsSearchBarVisible = false; _editor.Focus(); e.Handled = true; }
    }

    private void OnSearchNext(object sender, RoutedEventArgs e)
    {
        FindText(true);
    }

    private void OnSearchPrev(object sender, RoutedEventArgs e)
    {
        FindText(false);
    }
    private void OnSearchClose(object sender, RoutedEventArgs e) { ViewModel.IsSearchBarVisible = false; _editor.Focus(); }

    private void FindText(bool forward)
    {
        var query = ViewModel.SearchText;
        if (string.IsNullOrEmpty(query)) return;
        var text = _editor.Text;
        var start = _editor.CaretOffset;
        int idx = forward 
            ? text.IndexOf(query, Math.Min(text.Length, start + 1), StringComparison.OrdinalIgnoreCase) 
            : text.LastIndexOf(query, Math.Max(0, start - 1), StringComparison.OrdinalIgnoreCase);
        
        if (idx < 0) idx = forward ? text.IndexOf(query, 0, StringComparison.OrdinalIgnoreCase) : text.LastIndexOf(query, text.Length, StringComparison.OrdinalIgnoreCase);
        
        if (idx >= 0)
        {
            _editor.Select(idx, query.Length);
            _editor.CaretOffset = idx;
            _editor.ScrollToLine(_editor.Document.GetLineByOffset(idx).LineNumber);
        }
    }

    // -------------------------------------------------------------------------
    // 右クリックコンテキストメニュー (Work Folder パス挿入)
    // -------------------------------------------------------------------------
    private async void OnEditorContextMenuOpened(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        menu.Items.Clear();

        var folders = await Task.Run(() => FindWorkFolders());
        if (folders.Count == 0)
        {
            menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "No work folders found", IsEnabled = false });
            return;
        }

        var header = new System.Windows.Controls.MenuItem { Header = "Insert Work Folder Path", IsEnabled = false };
        menu.Items.Add(header);
        menu.Items.Add(new Separator());

        foreach (var folder in folders)
        {
            var item = new System.Windows.Controls.MenuItem { Header = Path.GetFileName(folder) };
            var captured = folder;
            item.Click += (s, args) => InsertWorkFolderLink(captured);
            menu.Items.Add(item);
        }
    }

    private List<string> FindWorkFolders()
    {
        var startDir = string.IsNullOrEmpty(ViewModel.CurrentFile)
            ? null
            : Path.GetDirectoryName(ViewModel.CurrentFile);

        if (startDir == null) return [];

        string? projectRoot = null;
        var current = new DirectoryInfo(startDir);
        while (current != null)
        {
            var sharedWork = Path.Combine(current.FullName, "shared", "_work");
            if (Directory.Exists(sharedWork)) { projectRoot = current.FullName; break; }
            current = current.Parent;
        }

        if (projectRoot == null) return [];

        var workRoot = Path.Combine(projectRoot, "shared", "_work");
        var pattern = new Regex(@"^\d{8}_");

        var result = Directory
            .EnumerateDirectories(workRoot, "*", SearchOption.AllDirectories)
            .Where(d => pattern.IsMatch(Path.GetFileName(d)))
            .OrderByDescending(d => Path.GetFileName(d))
            .Take(30)
            .ToList();

        return result;
    }

    private void InsertWorkFolderLink(string folderPath)
    {
        var name = Path.GetFileName(folderPath);
        var link = $"[{name}]({folderPath})";
        _editor.Document.Insert(_editor.CaretOffset, link);
        _editor.Focus();
    }

    private void OnEditorMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || e.ChangedButton != MouseButton.Left) return;
        var pos = _editor.GetPositionFromPoint(e.GetPosition(_editor));
        if (pos == null) return;
        var offset = _editor.Document.GetOffset(pos.Value.Line, pos.Value.Column);
        var line = _editor.Document.GetLineByOffset(offset);
        var lineText = _editor.Document.GetText(line.Offset, line.Length);

        var urlMatch = System.Text.RegularExpressions.Regex.Match(lineText, @"\[.*?\]\((https?://[^\)]+)\)");
        if (urlMatch.Success) { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(urlMatch.Groups[1].Value) { UseShellExecute = true }); e.Handled = true; return; }

        var pathMatch = System.Text.RegularExpressions.Regex.Match(lineText, @"\[.*?\]\((?!https?://)([^\)]+)\)");
        if (pathMatch.Success)
        {
            var rel = pathMatch.Groups[1].Value;
            string absPath = Path.IsPathRooted(rel) ? rel : (!string.IsNullOrEmpty(ViewModel.CurrentFile) ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ViewModel.CurrentFile)!, rel)) : "");
            if (string.IsNullOrEmpty(absPath)) return;
            if (Directory.Exists(absPath)) { System.Diagnostics.Process.Start("explorer.exe", absPath); e.Handled = true; }
            else if (File.Exists(absPath)) { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(absPath) { UseShellExecute = true }); e.Handled = true; }
        }
    }

    private void OnEditorPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;

        var scrollViewer = FindVisualChild<ScrollViewer>(_editor);
        if (scrollViewer == null || scrollViewer.ScrollableWidth <= 0) return;

        const double horizontalStep = 48d;
        var direction = e.Delta > 0 ? -1d : 1d;
        scrollViewer.ScrollToHorizontalOffset(scrollViewer.HorizontalOffset + (direction * horizontalStep));
        e.Handled = true;
    }

    private static TChild? FindVisualChild<TChild>(DependencyObject parent)
        where TChild : DependencyObject
    {
        var childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < childCount; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is TChild typedChild)
            {
                return typedChild;
            }

            var nestedChild = FindVisualChild<TChild>(child);
            if (nestedChild != null)
            {
                return nestedChild;
            }
        }

        return null;
    }

    // -------------------------------------------------------------------------
    // Diff ビュー
    // -------------------------------------------------------------------------
    private void EnsureDiffViewer()
    {
        if (_diffViewer != null) return;

        _diffViewer = new TextEditor
        {
            FontFamily = new System.Windows.Media.FontFamily("JetBrains Mono, Cascadia Mono, Consolas"),
            FontSize = 14,
            WordWrap = false,
            ShowLineNumbers = true,
            IsReadOnly = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        AddBottomViewportPadding(_diffViewer);

        // テーマ適用
        var bg = Application.Current.Resources["EditorBackground"] as System.Windows.Media.SolidColorBrush;
        var fg = Application.Current.Resources["EditorForeground"] as System.Windows.Media.SolidColorBrush;
        _diffViewer.Background = bg ?? new System.Windows.Media.SolidColorBrush((MediaColor)System.Windows.Media.ColorConverter.ConvertFromString("#0d1117"));
        _diffViewer.Foreground = fg ?? new System.Windows.Media.SolidColorBrush((MediaColor)System.Windows.Media.ColorConverter.ConvertFromString("#c9d1d9"));
        _diffViewer.LineNumbersForeground = (Application.Current.Resources["AppSubtext0"] as System.Windows.Media.SolidColorBrush)
            ?? new System.Windows.Media.SolidColorBrush((MediaColor)System.Windows.Media.ColorConverter.ConvertFromString("#8b949e"));

        _diffRenderer = new DiffLineBackgroundRenderer();
        _diffViewer.TextArea.TextView.BackgroundRenderers.Add(_diffRenderer);
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

    private void ShowDiffView()
    {
        if (string.IsNullOrEmpty(ViewModel.CurrentFile)) return;

        var fileName = Path.GetFileName(ViewModel.CurrentFile);
        if (fileName.Equals("current_focus.md", StringComparison.OrdinalIgnoreCase))
        {
            var dir = Path.GetDirectoryName(ViewModel.CurrentFile)!;
            var histDir = Path.Combine(dir, "focus_history");
            var snapshots = GetFocusSnapshots(histDir);

            if (snapshots.Count == 0)
            {
                ViewModel.IsDiffViewActive = false;
                System.Windows.MessageBox.Show(
                    "No snapshots found in focus_history/.",
                    "Diff", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var selected = ShowSnapshotPickerDialog(snapshots);
            if (selected == null)
            {
                ViewModel.IsDiffViewActive = false;
                return;
            }

            ViewModel.NotifyTextChanged(_editor.Text);
            var previous = File.ReadAllText(selected);
            var current = ViewModel.EditorText ?? "";
            var headerText = $"Diff: current_focus.md  vs  {Path.GetFileName(selected)}";
            RenderDiff(previous, current, headerText);
        }
        else
        {
            ViewModel.NotifyTextChanged(_editor.Text);
            var previous = ViewModel.OriginalContent ?? "";
            var current = ViewModel.EditorText ?? "";

            if (current == previous)
            {
                ViewModel.IsDiffViewActive = false;
                System.Windows.MessageBox.Show(
                    "No unsaved changes.",
                    "Diff", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var headerText = $"Diff: unsaved changes  vs  saved ({fileName})";
            RenderDiff(previous, current, headerText);
        }
    }

    private void RenderDiff(string previous, string current, string headerText)
    {
        EnsureDiffViewer();

        var diffBuilder = new InlineDiffBuilder(new Differ());
        var diffResult = diffBuilder.BuildDiffModel(previous, current);

        var sb = new StringBuilder();
        var lineTypes = new Dictionary<int, ChangeType>();
        sb.AppendLine(headerText);
        sb.AppendLine(new string('─', Math.Min(headerText.Length + 20, 120)));
        int lineNum = 2;

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
                case ChangeType.Imaginary:
                    sb.AppendLine();
                    lineTypes[lineNum] = ChangeType.Imaginary;
                    break;
                default:
                    sb.AppendLine("  " + line.Text);
                    break;
            }
        }

        _diffRenderer!.SetLineTypes(lineTypes);
        _diffViewer!.Text = sb.ToString();

        if (!string.IsNullOrEmpty(ViewModel.CurrentFile))
        {
            var ext = Path.GetExtension(ViewModel.CurrentFile).ToLower();
            if (ext is ".md" or ".markdown")
            {
                if (_markdownDefinition == null) RegisterMarkdownHighlighting();
                _diffViewer.SyntaxHighlighting = _markdownDefinition;
            }
        }

        EditorHost.Content = _diffViewer;
        DiffToggleButton.Appearance = Wpf.Ui.Controls.ControlAppearance.Primary;
    }

    private static List<string> GetFocusSnapshots(string histDir)
    {
        if (!Directory.Exists(histDir)) return [];
        return Directory.GetFiles(histDir, "*.md")
            .OrderByDescending(f => Path.GetFileNameWithoutExtension(f))
            .ToList();
    }

    private string? ShowSnapshotPickerDialog(List<string> snapshots)
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

        string? result = null;

        var listBox = new System.Windows.Controls.ListBox
        {
            MinHeight = 80,
            MaxHeight = 300,
            Background = surface1,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            FontSize = 13,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var todayPrefix = DateTime.Now.ToString("yyyy-MM-dd");
        int defaultIndex = -1;
        for (int i = 0; i < snapshots.Count; i++)
        {
            var name = Path.GetFileNameWithoutExtension(snapshots[i]);
            listBox.Items.Add(name);
            if (defaultIndex < 0 && !name.Equals(todayPrefix, StringComparison.OrdinalIgnoreCase))
                defaultIndex = i;
        }
        listBox.SelectedIndex = defaultIndex >= 0 ? defaultIndex : 0;

        // タイトルバー
        var titleBar = new Grid { Background = surface1, Height = 38 };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleIcon = new System.Windows.Controls.TextBlock
        {
            Text = "⇄", Foreground = accent, FontSize = 15,
            Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleIcon, 0);

        var titleText = new System.Windows.Controls.TextBlock
        {
            Text = "Compare with snapshot", Foreground = text, FontSize = 14,
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
            Text = "Select a focus_history snapshot to compare:",
            Foreground = subtext, FontSize = 12,
        };

        var contentPanel = new StackPanel
        {
            Margin = new Thickness(16, 12, 16, 8),
            Children = { helper, listBox }
        };

        var compareButton = new Wpf.Ui.Controls.Button
        {
            Content = "Compare",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            MinWidth = 100, Height = 32,
            Margin = new Thickness(0, 0, 8, 0), IsDefault = true
        };
        var cancelButton = new Wpf.Ui.Controls.Button
        {
            Content = "Cancel",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            MinWidth = 80, Height = 32, IsCancel = true
        };
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(16, 4, 16, 12),
            Children = { compareButton, cancelButton }
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(contentPanel, 1);
        Grid.SetRow(footer, 2);
        root.Children.Add(titleBar);
        root.Children.Add(contentPanel);
        root.Children.Add(footer);

        var dialog = new Window
        {
            Content = root,
            Width = 360,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            ShowInTaskbar = false,
            Background = surface,
        };

        titleBar.MouseLeftButtonDown += (s, e) => dialog.DragMove();
        closeButton.Click += (s, e) => dialog.DialogResult = false;
        cancelButton.Click += (s, e) => dialog.DialogResult = false;
        compareButton.Click += (s, e) =>
        {
            if (listBox.SelectedIndex >= 0)
            {
                result = snapshots[listBox.SelectedIndex];
                dialog.DialogResult = true;
            }
        };
        listBox.MouseDoubleClick += (s, e) =>
        {
            if (listBox.SelectedIndex >= 0)
            {
                result = snapshots[listBox.SelectedIndex];
                dialog.DialogResult = true;
            }
        };

        dialog.ShowDialog();
        return result;
    }

    private void HideDiffView()
    {
        EditorHost.Content = _editor;
        DiffToggleButton.Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
    }

    // DiffLineBackgroundRenderer は Views/ProposalReviewDialog.cs に移動済み

    private void OnDeleteDecisionLog(object sender, RoutedEventArgs e)
    {
        if (FileTreeView.SelectedItem is FileTreeNode node && CanDeleteDecisionLog(node))
            ViewModel.DeleteDecisionLogCommand.Execute(node);
    }

    // -------------------------------------------------------------------------
    // スクロール可能なエラーダイアログ
    // -------------------------------------------------------------------------
    private void ShowScrollableErrorDialog(string title, string message,
        Wpf.Ui.Controls.SymbolRegular iconSymbol = Wpf.Ui.Controls.SymbolRegular.ErrorCircle24)
    {
        var appResources = Application.Current.Resources;
        var surface  = (System.Windows.Media.Brush)appResources["AppSurface0"];
        var surface1 = (System.Windows.Media.Brush)appResources["AppSurface1"];
        var surface2 = (System.Windows.Media.Brush)appResources["AppSurface2"];
        var text     = (System.Windows.Media.Brush)appResources["AppText"];
        var subtext  = (System.Windows.Media.Brush)appResources["AppSubtext0"];

        // タイトルバー
        var titleBar = new Grid { Background = surface1, Height = 38 };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleIcon = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = iconSymbol, FontSize = 16,
            Foreground = text,
            Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleIcon, 0);
        var titleText = new System.Windows.Controls.TextBlock
        {
            Text = title, Foreground = text, FontSize = 14,
            FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center
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

        // スクロール可能なメッセージエリア
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
            FontFamily = new System.Windows.Media.FontFamily("JetBrains Mono, Cascadia Mono, Consolas"),
            FontSize = 12,
            MinHeight = 80,
            MaxHeight = 400,
        };
        var contentPanel = new StackPanel { Margin = new Thickness(16, 12, 16, 8) };
        contentPanel.Children.Add(textBox);

        var okButton = new Wpf.Ui.Controls.Button
        {
            Content = "OK", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            MinWidth = 80, Height = 32, IsDefault = true, IsCancel = true
        };
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(16, 4, 16, 12),
            Children = { okButton }
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titleBar, 0); Grid.SetRow(contentPanel, 1); Grid.SetRow(footer, 2);
        root.Children.Add(titleBar); root.Children.Add(contentPanel); root.Children.Add(footer);

        var dialog = new Window
        {
            Content = root, Width = 560,
            SizeToContent = SizeToContent.Height,
            MinWidth = 400, MaxHeight = 600,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this), ShowInTaskbar = false,
            Background = surface
        };
        titleBar.MouseLeftButtonDown += (s, e) => dialog.DragMove();
        closeBtn.Click += (s, e) => dialog.Close();
        okButton.Click += (s, e) => dialog.Close();
        dialog.ShowDialog();
    }

    // -------------------------------------------------------------------------
    // Update Focus ダイアログ
    // -------------------------------------------------------------------------
    private Task<(bool ok, string? wsId)> ShowWorkstreamSelectionDialogAsync(List<Curia.Models.WorkstreamInfo> workstreams)
    {
        var appResources = Application.Current.Resources;
        var surface  = (System.Windows.Media.Brush)appResources["AppSurface0"];
        var surface1 = (System.Windows.Media.Brush)appResources["AppSurface1"];
        var surface2 = (System.Windows.Media.Brush)appResources["AppSurface2"];
        var text     = (System.Windows.Media.Brush)appResources["AppText"];
        var subtext  = (System.Windows.Media.Brush)appResources["AppSubtext0"];
        var accent   = appResources.Contains("AppBlue")
            ? (System.Windows.Media.Brush)appResources["AppBlue"] : text;

        string? result = null;

        // タイトルバー
        var titleBar = new Grid { Background = surface1, Height = 38 };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleIcon = new System.Windows.Controls.TextBlock
        {
            Text = "⇄", Foreground = accent, FontSize = 15,
            Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleIcon, 0);
        var titleText = new System.Windows.Controls.TextBlock
        {
            Text = "Select Workstream", Foreground = text, FontSize = 14,
            FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center
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

        var listBox = new System.Windows.Controls.ListBox
        {
            MinHeight = 60, MaxHeight = 250,
            Background = surface1, Foreground = text,
            BorderBrush = surface2, BorderThickness = new Thickness(1),
            FontSize = 13, Margin = new Thickness(0, 8, 0, 0),
        };
        // SystemColors を上書きして選択色をテーマに合わせる
        listBox.Resources[System.Windows.SystemColors.HighlightBrushKey]                    = accent;
        listBox.Resources[System.Windows.SystemColors.HighlightTextBrushKey]                = surface1;
        listBox.Resources[System.Windows.SystemColors.InactiveSelectionHighlightBrushKey]   = surface2;
        listBox.Resources[System.Windows.SystemColors.InactiveSelectionHighlightTextBrushKey] = text;
        // "Whole project" を先頭に追加
        listBox.Items.Add(new System.Windows.Controls.ListBoxItem
        {
            Content = "Whole project", Tag = (string?)null,
        });
        foreach (var ws in workstreams)
        {
            listBox.Items.Add(new System.Windows.Controls.ListBoxItem
            {
                Content = ws.Label, Tag = ws.Id,
            });
        }
        listBox.SelectedIndex = 0;

        var helper = new System.Windows.Controls.TextBlock
        {
            Text = "Select workstream to update current_focus.md:",
            Foreground = subtext, FontSize = 12
        };
        var contentPanel = new StackPanel
        {
            Margin = new Thickness(16, 12, 16, 8),
            Children = { helper, listBox }
        };

        var okButton = new Wpf.Ui.Controls.Button
        {
            Content = "Select", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            MinWidth = 100, Height = 32, Margin = new Thickness(0, 0, 8, 0), IsDefault = true
        };
        var cancelButton = new Wpf.Ui.Controls.Button
        {
            Content = "Cancel", Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            MinWidth = 80, Height = 32, IsCancel = true
        };
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(16, 4, 16, 12),
            Children = { okButton, cancelButton }
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titleBar, 0); Grid.SetRow(contentPanel, 1); Grid.SetRow(footer, 2);
        root.Children.Add(titleBar); root.Children.Add(contentPanel); root.Children.Add(footer);

        var dialog = new Window
        {
            Content = root, Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this), ShowInTaskbar = false,
            Background = surface
        };
        titleBar.MouseLeftButtonDown += (s, e) => dialog.DragMove();
        closeBtn.Click  += (s, e) => dialog.DialogResult = false;
        cancelButton.Click += (s, e) => dialog.DialogResult = false;
        okButton.Click  += (s, e) =>
        {
            if (listBox.SelectedItem is System.Windows.Controls.ListBoxItem item)
            {
                result = item.Tag as string; // null = general
                dialog.DialogResult = true;
            }
        };
        listBox.MouseDoubleClick += (s, e) =>
        {
            if (listBox.SelectedItem is System.Windows.Controls.ListBoxItem item)
            {
                result = item.Tag as string;
                dialog.DialogResult = true;
            }
        };

        dialog.ShowDialog();
        var ok = dialog.DialogResult == true;
        return Task.FromResult((ok, ok ? result : (string?)null));
    }

    private Task<(bool apply, string? content)> ShowFocusUpdateProposalDialogAsync(
        Curia.Models.FocusUpdateResult proposal,
        Func<string, string, Task<string>> refineFunc)
    {
        var backupInfo = proposal.BackupStatus == Curia.Models.BackupStatus.Created
            ? $"Backup created: {System.IO.Path.GetFileName(proposal.BackupPath)}"
            : "Backup already exists (skipped)";

        return ProposalReviewDialog.ShowAsync(
            Window.GetWindow(this),
            proposal,
            titleText:  "Update Focus from Asana",
            titleIcon:  "⟳",
            extraInfo:  backupInfo,
            refineFunc: refineFunc);
    }


    private Task<string?> ShowNewDecisionLogDialog()
    {
        var dialog = new InputDialog("New Decision Log", "File name (date is added automatically):") { Owner = Window.GetWindow(this) };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.InputText : null);
    }

    private Task<string?> ShowNewObsidianNoteDialog()
    {
        var dialog = new InputDialog("Add Obsidian Note", "File name (.md is optional):")
        {
            Owner = Window.GetWindow(this)
        };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.InputText : null);
    }

    // -------------------------------------------------------------------------
    // AI Decision Log - 入力ダイアログ
    // -------------------------------------------------------------------------
    private Task<Curia.Models.AiDecisionLogInputResult?> ShowAiDecisionLogInputDialogAsync(
        List<Curia.Models.DetectedDecision> candidates, string? prefillText = null)
    {
        var appResources = Application.Current.Resources;
        var surface  = (System.Windows.Media.Brush)appResources["AppSurface0"];
        var surface1 = (System.Windows.Media.Brush)appResources["AppSurface1"];
        var surface2 = (System.Windows.Media.Brush)appResources["AppSurface2"];
        var text     = (System.Windows.Media.Brush)appResources["AppText"];
        var subtext  = (System.Windows.Media.Brush)appResources["AppSubtext0"];
        var accent   = appResources.Contains("AppBlue")
            ? (System.Windows.Media.Brush)appResources["AppBlue"] : text;

        Curia.Models.AiDecisionLogInputResult? result = null;

        // ---- タイトルバー ----
        var titleBar = new Grid { Background = surface1, Height = 38 };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleIcon = new System.Windows.Controls.TextBlock
        {
            Text = "✦", Foreground = accent, FontSize = 13,
            Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleIcon, 0);
        var titleText = new System.Windows.Controls.TextBlock
        {
            Text = "AI Decision Log", Foreground = text, FontSize = 14,
            FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center
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

        // ---- Input セクション ----
        var inputLabel = new System.Windows.Controls.TextBlock
        {
            Text = "What was decided?",
            Foreground = subtext, FontSize = 11, Margin = new Thickness(0, 0, 0, 4)
        };
        var inputBox = new System.Windows.Controls.TextBox
        {
            Padding = new Thickness(8, 6, 8, 6),
            Background = surface1, Foreground = text, BorderBrush = surface2,
            BorderThickness = new Thickness(1), FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            Height = 90,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Text = prefillText ?? "",
        };

        // ---- Status ----
        var confirmedRadio = new System.Windows.Controls.RadioButton
        {
            Content = "Confirmed", IsChecked = true,
            Foreground = text, FontSize = 12, Margin = new Thickness(0, 0, 16, 0)
        };
        var tentativeRadio = new System.Windows.Controls.RadioButton
        {
            Content = "Tentative",
            Foreground = text, FontSize = 12
        };
        var statusPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var statusLabel = new System.Windows.Controls.TextBlock
        {
            Text = "Status: ", Foreground = subtext, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
        };
        statusPanel.Children.Add(statusLabel);
        statusPanel.Children.Add(confirmedRadio);
        statusPanel.Children.Add(tentativeRadio);

        // ---- Trigger ----
        var triggerLabel = new System.Windows.Controls.TextBlock
        {
            Text = "Trigger: ", Foreground = subtext, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
        };
        var triggerCombo = new System.Windows.Controls.ComboBox
        {
            Background = surface1, Foreground = text, BorderBrush = surface2,
            FontSize = 12, MinWidth = 160, Padding = new Thickness(6, 4, 4, 4)
        };
        triggerCombo.Items.Add("Solo decision");
        triggerCombo.Items.Add("AI session");
        triggerCombo.Items.Add("Meeting");
        triggerCombo.SelectedIndex = 0;
        var triggerPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 6, 0, 0)
        };
        triggerPanel.Children.Add(triggerLabel);
        triggerPanel.Children.Add(triggerCombo);

        var inputSection = new StackPanel { Margin = new Thickness(16, 12, 16, 8) };
        inputSection.Children.Add(inputLabel);
        inputSection.Children.Add(inputBox);
        inputSection.Children.Add(statusPanel);
        inputSection.Children.Add(triggerPanel);

        // ---- 添付ファイル ----
        var attachedFiles     = new List<string>();
        var attachedFilesList = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        var attachButton = new Wpf.Ui.Controls.Button
        {
            Content = "Attach file...",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            Height = 26, FontSize = 11,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 0),
            Padding = new Thickness(8, 2, 8, 2)
        };
        attachButton.Click += (s, e) =>
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Text/Markdown (*.txt;*.md)|*.txt;*.md",
                Multiselect = true
            };
            if (ofd.ShowDialog() != true) return;

            foreach (var f in ofd.FileNames)
            {
                if (attachedFiles.Contains(f)) continue;
                attachedFiles.Add(f);

                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 2, 0, 0)
                };
                var fileLabel = new System.Windows.Controls.TextBlock
                {
                    Text = Path.GetFileName(f),
                    Foreground = text, FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                };
                var removeBtn = new System.Windows.Controls.Button
                {
                    Content = "✕", FontSize = 10, Width = 18, Height = 18,
                    Background = System.Windows.Media.Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = subtext, Padding = new Thickness(0)
                };
                var capturedFile = f;
                var capturedRow  = row;
                removeBtn.Click += (s2, e2) =>
                {
                    attachedFiles.Remove(capturedFile);
                    attachedFilesList.Children.Remove(capturedRow);
                };
                row.Children.Add(fileLabel);
                row.Children.Add(removeBtn);
                attachedFilesList.Children.Add(row);
            }
        };

        inputSection.Children.Add(attachButton);
        inputSection.Children.Add(attachedFilesList);

        // ---- Detected candidates セクション (候補がある場合のみ表示) ----
        var checkBoxItems = new List<(System.Windows.Controls.CheckBox chk,
            Curia.Models.DetectedDecision candidate)>();

        var candidatesSection = new StackPanel
        {
            Margin = new Thickness(16, 0, 16, 8),
            Visibility = candidates.Count > 0 ? Visibility.Visible : Visibility.Collapsed
        };
        if (candidates.Count > 0)
        {
            candidatesSection.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Detected from recent changes",
                Foreground = subtext, FontSize = 11, Margin = new Thickness(0, 0, 0, 6)
            });

            foreach (var candidate in candidates)
            {
                var statusTag = candidate.Status == "tentative" ? " (tentative)" : "";
                var chk = new System.Windows.Controls.CheckBox
                {
                    Content = candidate.Summary + statusTag,
                    IsChecked = candidate.IsSelected,
                    Foreground = text, FontSize = 12,
                    Margin = new Thickness(0, 2, 0, 2)
                };
                checkBoxItems.Add((chk, candidate));
                candidatesSection.Children.Add(chk);
            }
        }

        // ---- フッター ----
        var generateButton = new Wpf.Ui.Controls.Button
        {
            Content = "Generate Draft",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            MinWidth = 120, Height = 32, Margin = new Thickness(0, 0, 8, 0)
        };
        var blankButton = new Wpf.Ui.Controls.Button
        {
            Content = "Blank Template",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            MinWidth = 110, Height = 32, Margin = new Thickness(0, 0, 8, 0)
        };
        var cancelButton = new Wpf.Ui.Controls.Button
        {
            Content = "Cancel",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            MinWidth = 80, Height = 32, IsCancel = true
        };
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(16, 4, 16, 12),
            Children = { generateButton, blankButton, cancelButton }
        };

        // Generate Draft のボタン状態更新
        void UpdateGenerateButtonState()
        {
            var hasInput    = !string.IsNullOrWhiteSpace(inputBox.Text);
            var hasSelected = checkBoxItems.Any(pair => pair.chk.IsChecked == true);
            generateButton.IsEnabled = hasInput || hasSelected;
        }
        inputBox.TextChanged += (s, e) => UpdateGenerateButtonState();
        foreach (var (chk, _) in checkBoxItems)
        {
            chk.Checked   += (s, e) => UpdateGenerateButtonState();
            chk.Unchecked += (s, e) => UpdateGenerateButtonState();
        }
        // 初期状態
        UpdateGenerateButtonState();

        // Ctrl+Enter で Generate
        inputBox.KeyDown += (s, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter
                && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0
                && generateButton.IsEnabled)
            {
                generateButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                e.Handled = true;
            }
        };

        // ---- レイアウト ----
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titleBar,          0);
        Grid.SetRow(inputSection,      1);
        Grid.SetRow(candidatesSection, 2);
        Grid.SetRow(footer,            3);
        root.Children.Add(titleBar);
        root.Children.Add(inputSection);
        root.Children.Add(candidatesSection);
        root.Children.Add(footer);

        var dialog = new Window
        {
            Content = root, Width = 480,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this), ShowInTaskbar = false,
            Background = surface
        };

        titleBar.MouseLeftButtonDown += (s, e) => dialog.DragMove();
        closeBtn.Click    += (s, e) => dialog.DialogResult = false;
        cancelButton.Click += (s, e) => dialog.DialogResult = false;

        generateButton.Click += (s, e) =>
        {
            var selectedCandidates = checkBoxItems
                .Where(pair => pair.chk.IsChecked == true)
                .Select(pair =>
                {
                    pair.candidate.IsSelected = true;
                    return pair.candidate;
                })
                .ToList();

            result = new Curia.Models.AiDecisionLogInputResult
            {
                UseBlankTemplate   = false,
                UserInput          = inputBox.Text.Trim(),
                Status             = confirmedRadio.IsChecked == true ? "Confirmed" : "Tentative",
                Trigger            = triggerCombo.SelectedItem as string ?? "Solo decision",
                SelectedCandidates = selectedCandidates,
                AttachedFilePaths  = [..attachedFiles],
            };
            dialog.DialogResult = true;
        };

        blankButton.Click += (s, e) =>
        {
            result = new Curia.Models.AiDecisionLogInputResult { UseBlankTemplate = true };
            dialog.DialogResult = true;
        };

        dialog.ShowDialog();
        return Task.FromResult(result);
    }

    // -------------------------------------------------------------------------
    // AI Decision Log - プレビュー/承認ダイアログ
    // -------------------------------------------------------------------------
    private async Task<(bool save, string? content, string? fileName, bool removeTension)>
        ShowDecisionLogPreviewDialogAsync(
            Curia.Models.DecisionLogDraftResult draft,
            Func<string, string, Task<string>> refineFunc)
    {
        var tcs = new TaskCompletionSource<(bool save, string? content, string? fileName, bool removeTension)>();

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

        string currentContent = draft.DraftContent;
        bool removeTension    = false;

        // ---- タイトルバー (ファイル名編集可能) ----
        var titleBar = new Grid { Background = surface1, Height = 38 };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleIcon = new System.Windows.Controls.TextBlock
        {
            Text = "✦", Foreground = accent, FontSize = 13,
            Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleIcon, 0);

        // ファイル名: 編集可能 TextBox
        var fileNameBox = new System.Windows.Controls.TextBox
        {
            Text = draft.SuggestedFileName,
            Background = System.Windows.Media.Brushes.Transparent,
            Foreground = text, BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = surface2, FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 8, 4), Padding = new Thickness(2, 0, 2, 0)
        };
        Grid.SetColumn(fileNameBox, 1);

        var fileNameHint = new System.Windows.Controls.TextBlock
        {
            Text = ".md", Foreground = subtext, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(fileNameHint, 2);

        var closeBtn = new System.Windows.Controls.Button
        {
            Content = "✕", Width = 34, Height = 26,
            Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0), Foreground = subtext, FontSize = 13
        };
        Grid.SetColumn(closeBtn, 3);

        titleBar.Children.Add(titleIcon);
        titleBar.Children.Add(fileNameBox);
        titleBar.Children.Add(fileNameHint);
        titleBar.Children.Add(closeBtn);

        // ---- AvalonEdit プレビュー (読み取り専用) ----
        var previewViewer = new ICSharpCode.AvalonEdit.TextEditor
        {
            FontFamily = new System.Windows.Media.FontFamily("JetBrains Mono, Cascadia Mono, Consolas"),
            FontSize = 12, WordWrap = false, ShowLineNumbers = true, IsReadOnly = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            Background = editorBg, Foreground = editorFg
        };
        AddBottomViewportPadding(previewViewer);
        previewViewer.LineNumbersForeground =
            (Application.Current.Resources["AppSubtext0"] as System.Windows.Media.SolidColorBrush)
            ?? new System.Windows.Media.SolidColorBrush(
                (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString("#8b949e"));
        // TextView の下余白をスクロールバー高さ分確保し、最終行がバーに隠れないようにする
        previewViewer.TextArea.TextView.Margin = new Thickness(
            previewViewer.TextArea.TextView.Margin.Left,
            previewViewer.TextArea.TextView.Margin.Top,
            previewViewer.TextArea.TextView.Margin.Right,
            SystemParameters.HorizontalScrollBarHeight + 2);
        if (_markdownDefinition != null)
            previewViewer.SyntaxHighlighting = _markdownDefinition;
        previewViewer.Text = currentContent;
        previewViewer.PreviewMouseWheel += (s, e) =>
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
            var sv = FindVisualChild<ScrollViewer>(previewViewer);
            if (sv == null || sv.ScrollableWidth <= 0) return;
            const double step = 48d;
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset + (e.Delta > 0 ? -step : step));
            e.Handled = true;
        };

        // ---- Refine 行 ----
        var refineBox = new System.Windows.Controls.TextBox
        {
            Padding = new Thickness(8, 6, 8, 6),
            Background = surface1, Foreground = text, BorderBrush = surface2,
            BorderThickness = new Thickness(1), FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        var refineButton = new Wpf.Ui.Controls.Button
        {
            Content = "Refine",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            MinWidth = 80, Height = 32, Margin = new Thickness(8, 0, 0, 0)
        };
        var refineStatus = new System.Windows.Controls.TextBlock
        {
            Foreground = subtext, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0), TextWrapping = TextWrapping.Wrap
        };
        var refinePlaceholder = new System.Windows.Controls.TextBlock
        {
            Text = "Refinement instructions (e.g. \"Add more detail to Why section\")",
            Foreground = subtext, FontSize = 12, IsHitTestVisible = false,
            Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
        };
        refineBox.TextChanged += (s, e) =>
            refinePlaceholder.Visibility = string.IsNullOrEmpty(refineBox.Text)
                ? Visibility.Visible : Visibility.Collapsed;
        refineBox.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                refineButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                e.Handled = true;
            }
        };
        var refineInputHost = new Grid();
        refineInputHost.Children.Add(refineBox);
        refineInputHost.Children.Add(refinePlaceholder);

        var refineRow = new Grid { Margin = new Thickness(16, 6, 16, 4) };
        refineRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        refineRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        refineRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(refineInputHost, 0);
        Grid.SetColumn(refineButton,    1);
        Grid.SetColumn(refineStatus,    2);
        refineRow.Children.Add(refineInputHost);
        refineRow.Children.Add(refineButton);
        refineRow.Children.Add(refineStatus);

        // ---- Tension 解決パネル (ResolvedTension がある場合のみ表示) ----
        var tensionPanel = new StackPanel
        {
            Margin = new Thickness(16, 2, 16, 4),
            Visibility = string.IsNullOrWhiteSpace(draft.ResolvedTension)
                ? Visibility.Collapsed : Visibility.Visible
        };
        if (!string.IsNullOrWhiteSpace(draft.ResolvedTension))
        {
            var tensionCheckBox = new System.Windows.Controls.CheckBox
            {
                Content = $"Remove resolved item from open_issues.md: \"{draft.ResolvedTension}\"",
                IsChecked = false,
                Foreground = subtext, FontSize = 11
            };
            tensionCheckBox.Checked   += (s, e) => removeTension = true;
            tensionCheckBox.Unchecked += (s, e) => removeTension = false;
            tensionPanel.Children.Add(tensionCheckBox);
        }

        // ---- フッター ----
        var saveButton = new Wpf.Ui.Controls.Button
        {
            Content = "Save",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            MinWidth = 90, Height = 32, Margin = new Thickness(0, 0, 8, 0)
        };
        var cancelFooterButton = new Wpf.Ui.Controls.Button
        {
            Content = "Cancel",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            MinWidth = 80, Height = 32, Margin = new Thickness(0, 0, 8, 0)
        };
        var debugButton = new Wpf.Ui.Controls.Button
        {
            Content = "View Debug",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            MinWidth = 90, Height = 32
        };
        var footerLeft = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Children = { debugButton }
        };
        var footerRight = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Children = { saveButton, cancelFooterButton }
        };
        var footerGrid = new Grid { Margin = new Thickness(16, 4, 16, 10) };
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
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titleBar,    0);
        Grid.SetRow(previewViewer, 1);
        Grid.SetRow(refineRow,   2);
        Grid.SetRow(tensionPanel, 3);
        Grid.SetRow(footerGrid,  4);
        root.Children.Add(titleBar);
        root.Children.Add(previewViewer);
        root.Children.Add(refineRow);
        root.Children.Add(tensionPanel);
        root.Children.Add(footerGrid);

        var dialog = new Window
        {
            Content = root, Width = 760, Height = 580,
            MinWidth = 500, MinHeight = 400,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.CanResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this), ShowInTaskbar = false,
            Background = surface
        };
        // WindowChrome で OS アクセントカラーのボーダーを除去しつつリサイズを維持
        System.Windows.Shell.WindowChrome.SetWindowChrome(dialog,
            new System.Windows.Shell.WindowChrome
            {
                CaptionHeight         = 0,
                ResizeBorderThickness = new Thickness(4),
                GlassFrameThickness   = new Thickness(0),
                UseAeroCaptionButtons = false
            });

        // ---- イベント ----
        void Complete(bool save)
        {
            var fn = fileNameBox.Text.Trim();
            if (!tcs.Task.IsCompleted)
                tcs.SetResult((save, save ? currentContent : null,
                    save ? (string.IsNullOrWhiteSpace(fn) ? null : fn) : null,
                    save && removeTension));
            dialog.Close();
            dialog.Owner?.Activate();
        }

        titleBar.MouseLeftButtonDown    += (s, e) => dialog.DragMove();
        closeBtn.Click                  += (s, e) => Complete(false);
        cancelFooterButton.Click        += (s, e) => Complete(false);
        saveButton.Click                += (s, e) => Complete(true);
        dialog.Closed += (s, e) =>
        {
            if (!tcs.Task.IsCompleted) tcs.SetResult((false, null, null, false));
            dialog.Owner?.Activate();
        };

        debugButton.Click += (s, e) =>
        {
            var debugText = new StringBuilder();
            debugText.AppendLine("=== SYSTEM PROMPT ===");
            debugText.AppendLine(draft.DebugSystemPrompt);
            debugText.AppendLine();
            debugText.AppendLine("=== USER PROMPT ===");
            debugText.AppendLine(draft.DebugUserPrompt);
            debugText.AppendLine();
            debugText.AppendLine("=== RESPONSE ===");
            debugText.AppendLine(draft.DebugResponse);
            ShowScrollableErrorDialog("LLM Debug Log", debugText.ToString(),
                Wpf.Ui.Controls.SymbolRegular.Bug24);
        };

        refineButton.Click += async (s, e) =>
        {
            var instructions = refineBox.Text.Trim();
            if (string.IsNullOrEmpty(instructions))
            {
                refineStatus.Text = "Enter refinement instructions above.";
                return;
            }

            refineButton.IsEnabled   = false;
            refineBox.IsEnabled      = false;
            saveButton.IsEnabled     = false;
            refineStatus.Text        = "Refining...";
            try
            {
                var refined      = await refineFunc(currentContent, instructions);
                currentContent   = refined;
                previewViewer.Text = refined;
                refineBox.Clear();
                refineStatus.Text = "Done.";
            }
            catch (Exception ex)
            {
                refineStatus.Text = $"Error: {ex.Message}";
            }
            finally
            {
                refineButton.IsEnabled = true;
                refineBox.IsEnabled    = true;
                saveButton.IsEnabled   = true;
            }
        };

        dialog.Show();
        return await tcs.Task;
    }

    // -------------------------------------------------------------------------
    // Meeting Notes Import - 入力ダイアログ
    // -------------------------------------------------------------------------
    private Task<Curia.Models.MeetingNotesInputResult?> ShowMeetingNotesInputDialogAsync(
        Curia.Models.ProjectInfo project,
        List<Curia.Models.WorkstreamInfo> workstreams)
    {
        var appResources = Application.Current.Resources;
        var surface  = (System.Windows.Media.Brush)appResources["AppSurface0"];
        var surface1 = (System.Windows.Media.Brush)appResources["AppSurface1"];
        var surface2 = (System.Windows.Media.Brush)appResources["AppSurface2"];
        var text     = (System.Windows.Media.Brush)appResources["AppText"];
        var subtext  = (System.Windows.Media.Brush)appResources["AppSubtext0"];
        var accent   = appResources.Contains("AppBlue")
            ? (System.Windows.Media.Brush)appResources["AppBlue"] : text;

        Curia.Models.MeetingNotesInputResult? result = null;

        // ---- タイトルバー ----
        var titleBar = new Grid { Background = surface1, Height = 38 };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleIcon = new System.Windows.Controls.TextBlock
        {
            Text = "📋", FontSize = 14,
            Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleIcon, 0);
        var titleText = new System.Windows.Controls.TextBlock
        {
            Text = "Import Meeting Notes", Foreground = text, FontSize = 14,
            FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center
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

        // ---- プロジェクト表示 ----
        var projectLabel = new System.Windows.Controls.TextBlock
        {
            Text = $"Project: {project.Name}",
            Foreground = subtext, FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8)
        };

        // ---- Workstream 選択 ----
        System.Windows.Controls.ComboBox? wsCombo = null;
        FrameworkElement wsControl;
        if (workstreams.Count > 0)
        {
            wsCombo = new System.Windows.Controls.ComboBox
            {
                Background = surface1, Foreground = text, BorderBrush = surface2,
                FontSize = 12, Padding = new Thickness(6, 4, 4, 4),
                Margin = new Thickness(0, 0, 0, 8)
            };
            wsCombo.Items.Add(new System.Windows.Controls.ComboBoxItem
                { Content = "General", Tag = (string?)null });
            foreach (var ws in workstreams)
                wsCombo.Items.Add(new System.Windows.Controls.ComboBoxItem
                    { Content = ws.Label, Tag = ws.Id });
            wsCombo.SelectedIndex = 0;
            wsControl = wsCombo;
        }
        else
        {
            wsControl = new System.Windows.Controls.TextBlock
            {
                Text = "Workstream: General",
                Foreground = subtext, FontSize = 12,
                Margin = new Thickness(0, 0, 0, 8)
            };
        }

        // ---- メモ入力 ----
        var notesLabel = new System.Windows.Controls.TextBlock
        {
            Text = "Paste meeting notes here...",
            Foreground = subtext, FontSize = 11,
            Margin = new Thickness(0, 0, 0, 4)
        };
        var notesBox = new System.Windows.Controls.TextBox
        {
            Padding = new Thickness(8, 6, 8, 6),
            Background = surface1, Foreground = text, BorderBrush = surface2,
            BorderThickness = new Thickness(1), FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true, Height = 180,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var hintText = new System.Windows.Controls.TextBlock
        {
            Text = "Decisions, next actions, and open questions will be detected.",
            Foreground = subtext, FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap
        };

        var contentPanel = new StackPanel { Margin = new Thickness(16, 12, 16, 8) };
        contentPanel.Children.Add(projectLabel);
        contentPanel.Children.Add(wsControl);
        contentPanel.Children.Add(notesLabel);
        contentPanel.Children.Add(notesBox);
        contentPanel.Children.Add(hintText);

        // ---- フッター ----
        var analyzeButton = new Wpf.Ui.Controls.Button
        {
            Content = "Analyze",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            MinWidth = 100, Height = 32, Margin = new Thickness(0, 0, 8, 0)
        };
        analyzeButton.IsEnabled = false;
        var cancelButton = new Wpf.Ui.Controls.Button
        {
            Content = "Cancel",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            MinWidth = 80, Height = 32, IsCancel = true
        };
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(16, 4, 16, 12),
            Children = { analyzeButton, cancelButton }
        };

        notesBox.TextChanged += (s, e) =>
            analyzeButton.IsEnabled = !string.IsNullOrWhiteSpace(notesBox.Text);

        notesBox.PreviewKeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter
                && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
                && analyzeButton.IsEnabled)
            {
                analyzeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                e.Handled = true;
            }
        };

        // ---- レイアウト ----
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titleBar,      0);
        Grid.SetRow(contentPanel,  1);
        Grid.SetRow(footer,        2);
        root.Children.Add(titleBar);
        root.Children.Add(contentPanel);
        root.Children.Add(footer);

        var dialog = new Window
        {
            Content = root, Width = 600,
            SizeToContent = SizeToContent.Height,
            MinHeight = 300,
            MaxHeight = SystemParameters.WorkArea.Height - 40,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this), ShowInTaskbar = false,
            Background = surface
        };

        titleBar.MouseLeftButtonDown += (s, e) => dialog.DragMove();
        closeBtn.Click    += (s, e) => dialog.DialogResult = false;
        cancelButton.Click += (s, e) => dialog.DialogResult = false;

        analyzeButton.Click += (s, e) =>
        {
            string? wsId = null;
            if (wsCombo != null && wsCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item)
                wsId = item.Tag as string;

            result = new Curia.Models.MeetingNotesInputResult
            {
                MeetingNotes = notesBox.Text.Trim(),
                WorkstreamId = wsId,
            };
            dialog.DialogResult = true;
        };

        dialog.ShowDialog();
        return Task.FromResult(result);
    }

    // -------------------------------------------------------------------------
    // Meeting Notes Import - プレビューダイアログ
    // -------------------------------------------------------------------------
    private async Task<bool> ShowMeetingNotesPreviewDialogAsync(
        Curia.Models.MeetingAnalysisResult analysisResult,
        Curia.Models.ProjectInfo project,
        string? workstreamId)
    {
        // Asana プロジェクト一覧を事前ロード
        var (gids, wsMap) = _captureService.LoadAsanaProjectGids(project);
        var asanaProjectMetas = new List<Curia.Models.AsanaProjectMeta>();
        if (gids.Count > 0)
        {
            using var metaCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            foreach (var gid in gids)
            {
                var m = await _captureService.FetchProjectMetaAsync(gid, metaCts.Token)
                        ?? new Curia.Models.AsanaProjectMeta { Gid = gid, Name = "" };
                asanaProjectMetas.Add(m);
            }
        }
        // デフォルト選択 GID: Personal Project の先頭を優先
        var personalGids = _captureService.GetPersonalProjectGids();
        string? defaultProjectGid = personalGids.Count > 0
            ? personalGids[0]
            : null;
        // workstream_project_map に一致があればそちらを優先
        if (!string.IsNullOrWhiteSpace(workstreamId) && wsMap.TryGetValue(workstreamId, out var wgid))
            defaultProjectGid = wgid;

        // デフォルトプロジェクトのセクションを事前ロード
        var sectionCache = new Dictionary<string, List<Curia.Models.AsanaSectionMeta>>();
        if (defaultProjectGid != null)
        {
            using var secCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            var defaultSections = await _captureService.FetchSectionsAsync(defaultProjectGid, secCts.Token);
            sectionCache[defaultProjectGid] = defaultSections;
        }
        var defaultProjectMeta = defaultProjectGid != null
            ? asanaProjectMetas.FirstOrDefault(m => m.Gid == defaultProjectGid)
            : null;

        // プロジェクトの AnkenAliases (+ プロジェクト名) をセクション自動選択の候補に使う
        var ankenAliases = _captureService.LoadAnkenAliases(project);

        var tcs = new TaskCompletionSource<bool>();

        var appResources = Application.Current.Resources;
        var surface  = (System.Windows.Media.Brush)appResources["AppSurface0"];
        var surface1 = (System.Windows.Media.Brush)appResources["AppSurface1"];
        var surface2 = (System.Windows.Media.Brush)appResources["AppSurface2"];
        var text     = (System.Windows.Media.Brush)appResources["AppText"];
        var subtext  = (System.Windows.Media.Brush)appResources["AppSubtext0"];
        var accent   = appResources.Contains("AppBlue")
            ? (System.Windows.Media.Brush)appResources["AppBlue"] : text;
        var accentGreen = appResources.Contains("AppGreen")
            ? (System.Windows.Media.Brush)appResources["AppGreen"] : text;
        var editorBg = (Application.Current.Resources["EditorBackground"] as System.Windows.Media.SolidColorBrush)
            ?? new System.Windows.Media.SolidColorBrush(
                (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString("#0d1117"));
        var editorFg = (Application.Current.Resources["EditorForeground"] as System.Windows.Media.SolidColorBrush)
            ?? new System.Windows.Media.SolidColorBrush(
                (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString("#c9d1d9"));

        // ---- タイトルバー ----
        var titleBar = new Grid { Background = surface1, Height = 38 };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleIcon = new System.Windows.Controls.TextBlock
        {
            Text = "📋", FontSize = 14,
            Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleIcon, 0);
        var decisionCount   = analysisResult.Decisions.Count;
        var tensionCount    = analysisResult.Tensions.OpenQuestions.Count
                            + analysisResult.Tensions.Concerns.Count;
        var asanaTaskCount  = analysisResult.AsanaTasks.Tasks.Count;
        var titleTextBlock = new System.Windows.Controls.TextBlock
        {
            Text = $"Meeting Analysis — {DateTime.Now:yyyy-MM-dd}",
            Foreground = text, FontSize = 14,
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
        titleBar.Children.Add(titleIcon);
        titleBar.Children.Add(titleTextBlock);
        titleBar.Children.Add(closeBtn);

        // ---- サマリーバー ----
        var summaryText = new StringBuilder();
        summaryText.Append($"{decisionCount} decision(s)");
        var focusCount = analysisResult.FocusUpdate.RecentContext.Count + analysisResult.FocusUpdate.NextActions.Count;
        if (focusCount > 0) summaryText.Append($"  ·  {focusCount} focus item(s)");
        if (tensionCount > 0) summaryText.Append($"  ·  {tensionCount} tension(s)");
        if (asanaTaskCount > 0) summaryText.Append($"  ·  {asanaTaskCount} task(s)");
        var summaryBar = new System.Windows.Controls.TextBlock
        {
            Text = summaryText.ToString(),
            Foreground = subtext, FontSize = 12,
            Margin = new Thickness(16, 6, 16, 4)
        };

        // ====================================================================
        // TabControl
        // ====================================================================
        var tabControl = new System.Windows.Controls.TabControl
        {
            Background = surface, BorderBrush = surface2, BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0)
        };

        // ---- Tab 1: Decisions ----
        var decisionsTab = new System.Windows.Controls.TabItem
        {
            Header = $"Decisions ({decisionCount})",
            Foreground = text, Background = surface1
        };
        var decisionsPanel = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
        var decisionCheckboxes = new List<(System.Windows.Controls.CheckBox chk, Curia.Models.MeetingDecision decision)>();
        var decisionExpanders  = new Dictionary<Curia.Models.MeetingDecision, ICSharpCode.AvalonEdit.TextEditor>();

        if (decisionCount == 0)
        {
            decisionsPanel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "No decisions detected.",
                Foreground = subtext, FontSize = 12, Margin = new Thickness(0, 8, 0, 0)
            });
        }
        else
        {
            foreach (var dec in analysisResult.Decisions)
            {
                var chk = new System.Windows.Controls.CheckBox
                {
                    Content = $"{dec.Title}  [{dec.Status}]",
                    IsChecked = dec.IsSelected,
                    Foreground = text, FontSize = 13,
                    Margin = new Thickness(0, 6, 0, 0)
                };
                chk.Checked   += (s, e) => dec.IsSelected = true;
                chk.Unchecked += (s, e) => dec.IsSelected = false;
                decisionCheckboxes.Add((chk, dec));

                var expandBtn = new System.Windows.Controls.Button
                {
                    Content = "Show draft ▼",
                    Background = System.Windows.Media.Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = accent, FontSize = 11,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                    Margin = new Thickness(20, 2, 0, 0), Padding = new Thickness(0)
                };

                var draftViewer = new ICSharpCode.AvalonEdit.TextEditor
                {
                    FontFamily = new System.Windows.Media.FontFamily("JetBrains Mono, Cascadia Mono, Consolas"),
                    FontSize = 12, IsReadOnly = true,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                    Background = editorBg, Foreground = editorFg,
                    MaxHeight = 200, MinHeight = 80,
                    Visibility = Visibility.Collapsed,
                    Margin = new Thickness(20, 4, 0, 4)
                };
                AddBottomViewportPadding(draftViewer);
                if (_markdownDefinition != null)
                    draftViewer.SyntaxHighlighting = _markdownDefinition;
                draftViewer.Text = Curia.Services.MeetingNotesService.BuildDecisionLogContent(dec);

                var capturedViewer = draftViewer;
                var capturedBtn    = expandBtn;
                bool isExpanded    = false;
                expandBtn.Click += (s, e) =>
                {
                    isExpanded = !isExpanded;
                    capturedViewer.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;
                    capturedBtn.Content       = isExpanded ? "Hide draft ▲" : "Show draft ▼";
                };

                decisionExpanders[dec] = draftViewer;
                decisionsPanel.Children.Add(chk);
                decisionsPanel.Children.Add(expandBtn);
                decisionsPanel.Children.Add(draftViewer);
            }
        }

        var decisionsScroll = new ScrollViewer
        {
            Content = decisionsPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        decisionsTab.Content = decisionsScroll;

        // ---- Tab 2: Focus ----
        var focusTab = new System.Windows.Controls.TabItem
        {
            Header = $"Focus ({focusCount})",
            Foreground = text, Background = surface1
        };

        var focusChk = new System.Windows.Controls.CheckBox
        {
            Content = "Apply focus update to current_focus.md",
            IsChecked = analysisResult.FocusUpdate.IsSelected && focusCount > 0,
            IsEnabled = focusCount > 0,
            Foreground = text, FontSize = 13,
            Margin = new Thickness(12, 8, 12, 4)
        };
        focusChk.Checked   += (s, e) => analysisResult.FocusUpdate.IsSelected = true;
        focusChk.Unchecked += (s, e) => analysisResult.FocusUpdate.IsSelected = false;

        var focusDiffHeader = new System.Windows.Controls.TextBlock
        {
            Text = "Proposed changes  (+ added  - removed)",
            Foreground = subtext, FontSize = 11,
            Margin = new Thickness(12, 2, 12, 4)
        };

        // diff ビュー
        var focusDiffViewer = new ICSharpCode.AvalonEdit.TextEditor
        {
            FontFamily = new System.Windows.Media.FontFamily("JetBrains Mono, Cascadia Mono, Consolas"),
            FontSize = 12, IsReadOnly = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            Background = editorBg, Foreground = editorFg,
        };
        focusDiffViewer.LineNumbersForeground =
            (Application.Current.Resources["AppSubtext0"] as System.Windows.Media.SolidColorBrush)
            ?? new System.Windows.Media.SolidColorBrush(
                (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString("#8b949e"));
        AddBottomViewportPadding(focusDiffViewer);
        var focusDiffRenderer = new DiffLineBackgroundRenderer();
        focusDiffViewer.TextArea.TextView.BackgroundRenderers.Add(focusDiffRenderer);

        void RefreshFocusDiff()
        {
            if (string.IsNullOrWhiteSpace(analysisResult.FocusUpdate.ProposedContent))
            {
                focusDiffViewer.Text = focusCount == 0 ? "No focus updates detected." : "";
                return;
            }
            var builder    = new DiffPlex.DiffBuilder.InlineDiffBuilder(new DiffPlex.Differ());
            var diffResult = builder.BuildDiffModel(
                analysisResult.FocusUpdate.CurrentContent,
                analysisResult.FocusUpdate.ProposedContent);
            var sb        = new StringBuilder();
            var lineTypes = new Dictionary<int, DiffPlex.DiffBuilder.Model.ChangeType>();
            int lineNum   = 0;
            foreach (var line in diffResult.Lines)
            {
                lineNum++;
                switch (line.Type)
                {
                    case DiffPlex.DiffBuilder.Model.ChangeType.Inserted:
                        sb.AppendLine("+ " + line.Text);
                        lineTypes[lineNum] = DiffPlex.DiffBuilder.Model.ChangeType.Inserted;
                        break;
                    case DiffPlex.DiffBuilder.Model.ChangeType.Deleted:
                        sb.AppendLine("- " + line.Text);
                        lineTypes[lineNum] = DiffPlex.DiffBuilder.Model.ChangeType.Deleted;
                        break;
                    case DiffPlex.DiffBuilder.Model.ChangeType.Modified:
                        sb.AppendLine("~ " + line.Text);
                        lineTypes[lineNum] = DiffPlex.DiffBuilder.Model.ChangeType.Modified;
                        break;
                    default:
                        sb.AppendLine("  " + line.Text);
                        break;
                }
            }
            focusDiffRenderer.SetLineTypes(lineTypes);
            focusDiffViewer.Text = sb.ToString();
        }
        RefreshFocusDiff();

        var focusRoot = new Grid();
        focusRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        focusRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        focusRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(focusChk,        0);
        Grid.SetRow(focusDiffHeader, 1);
        Grid.SetRow(focusDiffViewer, 2);
        focusRoot.Children.Add(focusChk);
        focusRoot.Children.Add(focusDiffHeader);
        focusRoot.Children.Add(focusDiffViewer);

        focusTab.Content = focusRoot;

        // ---- Tab 3: Open Issues ----
        var tensionsTab = new System.Windows.Controls.TabItem
        {
            Header = $"Open Issues ({tensionCount})",
            Foreground = text, Background = surface1
        };
        var tensionsPanel = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };

        var tensionsChk = new System.Windows.Controls.CheckBox
        {
            Content = "Add to open_issues.md",
            IsChecked = analysisResult.Tensions.IsSelected && analysisResult.Tensions.HasItems,
            IsEnabled = analysisResult.Tensions.HasItems,
            Foreground = text, FontSize = 13,
            Margin = new Thickness(0, 0, 0, 8)
        };
        tensionsChk.Checked   += (s, e) => analysisResult.Tensions.IsSelected = true;
        tensionsChk.Unchecked += (s, e) => analysisResult.Tensions.IsSelected = false;
        tensionsPanel.Children.Add(tensionsChk);

        if (!analysisResult.Tensions.HasItems)
        {
            tensionsPanel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "No open issues detected.",
                Foreground = subtext, FontSize = 12
            });
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(analysisResult.Tensions.AppendContent))
            {
                var tensionsViewer = new ICSharpCode.AvalonEdit.TextEditor
                {
                    FontFamily = new System.Windows.Media.FontFamily("JetBrains Mono, Cascadia Mono, Consolas"),
                    FontSize = 12, IsReadOnly = true,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                    Background = editorBg, Foreground = editorFg,
                    MaxHeight = 300
                };
                AddBottomViewportPadding(tensionsViewer);
                if (_markdownDefinition != null)
                    tensionsViewer.SyntaxHighlighting = _markdownDefinition;
                tensionsViewer.Text = analysisResult.Tensions.AppendContent;
                tensionsPanel.Children.Add(tensionsViewer);
            }

            if (!File.Exists(System.IO.Path.Combine(
                string.IsNullOrWhiteSpace(analysisResult.Tensions.CurrentContent)
                    ? "" : "")))
            {
                // open_issues.md が存在しない場合のヒント
            }

            if (string.IsNullOrWhiteSpace(analysisResult.Tensions.CurrentContent))
            {
                tensionsPanel.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = "open_issues.md not found — will be created",
                    Foreground = accent, FontSize = 11, Margin = new Thickness(0, 4, 0, 0)
                });
            }
        }

        var tensionsScroll = new ScrollViewer
        {
            Content = tensionsPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        tensionsTab.Content = tensionsScroll;

        // ---- Tab 4: Asana Tasks ----
        var asanaTab = new System.Windows.Controls.TabItem
        {
            Header = $"Asana Tasks ({asanaTaskCount})",
            Foreground = text, Background = surface1
        };
        var asanaPanel = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };

        var asanaChk = new System.Windows.Controls.CheckBox
        {
            Content = "Add tasks to tasks.md",
            IsChecked = analysisResult.AsanaTasks.IsSelected && analysisResult.AsanaTasks.HasItems,
            IsEnabled = analysisResult.AsanaTasks.HasItems,
            Foreground = text, FontSize = 13,
            Margin = new Thickness(0, 0, 0, 8)
        };
        asanaChk.Checked   += (s, e) => analysisResult.AsanaTasks.IsSelected = true;
        asanaChk.Unchecked += (s, e) => analysisResult.AsanaTasks.IsSelected = false;
        asanaPanel.Children.Add(asanaChk);

        // ---- タスクリスト (タスクごとのプロジェクト/セクション/期限設定) ----

        // セクションを async でロード・キャッシュするローカル関数
        async Task<List<Curia.Models.AsanaSectionMeta>> LoadSectionsAsync(string projectGid)
        {
            if (sectionCache.TryGetValue(projectGid, out var cached)) return cached;
            using var cts2 = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            var sections = await _captureService.FetchSectionsAsync(projectGid, cts2.Token);
            sectionCache[projectGid] = sections;
            return sections;
        }

        void PopulateTaskSectionCombo(System.Windows.Controls.ComboBox secCb,
                                      List<Curia.Models.AsanaSectionMeta> sections,
                                      Curia.Models.MeetingAsanaTask t)
        {
            secCb.Items.Clear();
            secCb.Items.Add(new Curia.Models.AsanaSectionMeta { Gid = "", Name = "(none)" });
            foreach (var sec in sections) secCb.Items.Add(sec);
            secCb.IsEnabled = true;

            // AnkenAliases でセクション名マッチング → 自動選択
            var match = sections.FirstOrDefault(s =>
                ankenAliases.Any(a => string.Equals(s.Name, a, StringComparison.OrdinalIgnoreCase)));
            if (match != null)
            {
                secCb.SelectedItem = match;
                t.SectionGid  = match.Gid;
                t.SectionName = match.Name;
            }
            else
            {
                secCb.SelectedIndex = 0;
            }
        }

        if (!analysisResult.AsanaTasks.HasItems)
        {
            asanaPanel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "No tasks detected.",
                Foreground = subtext, FontSize = 12
            });
        }
        else
        {
            foreach (var task in analysisResult.AsanaTasks.Tasks)
            {
                var capturedTask = task;

                // タスク行 (チェックボックス + ノート + 設定行)
                var priorityTag = task.Priority is "High" or "Medium" or "Low"
                    ? $"  [{task.Priority}]" : "";
                var taskChk = new System.Windows.Controls.CheckBox
                {
                    Content = $"{task.Title}{priorityTag}",
                    IsChecked = task.IsSelected,
                    Foreground = text, FontSize = 13,
                    Margin = new Thickness(0, 6, 0, 0)
                };
                taskChk.Checked   += (s, e) => capturedTask.IsSelected = true;
                taskChk.Unchecked += (s, e) => capturedTask.IsSelected = false;
                asanaPanel.Children.Add(taskChk);

                if (!string.IsNullOrWhiteSpace(task.Notes))
                {
                    asanaPanel.Children.Add(new System.Windows.Controls.TextBlock
                    {
                        Text = task.Notes,
                        Foreground = subtext, FontSize = 11,
                        Margin = new Thickness(20, 1, 0, 0),
                        TextWrapping = TextWrapping.Wrap
                    });
                }

                // ---- タスクごとの設定行 ----
                var settingsGrid = new Grid { Margin = new Thickness(20, 4, 0, 6) };
                settingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
                settingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 100 });
                settingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                settingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                settingsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                settingsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                settingsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                // Project label + ComboBox
                var projLabel = new System.Windows.Controls.TextBlock
                {
                    Text = "Project", Foreground = subtext, FontSize = 10,
                    Margin = new Thickness(0, 0, 8, 1)
                };
                Grid.SetColumn(projLabel, 0); Grid.SetRow(projLabel, 0);

                var taskProjectCombo = new System.Windows.Controls.ComboBox
                {
                    Foreground = text, Background = surface1,
                    Padding = new Thickness(6, 3, 4, 3),
                    Margin = new Thickness(0, 0, 8, 0)
                };
                taskProjectCombo.DisplayMemberPath = nameof(Curia.Models.AsanaProjectMeta.DisplayLabel);
                Grid.SetColumn(taskProjectCombo, 0); Grid.SetRow(taskProjectCombo, 1);

                // Section label + ComboBox
                var secLabel = new System.Windows.Controls.TextBlock
                {
                    Text = "Section", Foreground = subtext, FontSize = 10,
                    Margin = new Thickness(0, 0, 8, 1)
                };
                Grid.SetColumn(secLabel, 1); Grid.SetRow(secLabel, 0);

                var taskSectionCombo = new System.Windows.Controls.ComboBox
                {
                    Foreground = text, Background = surface1,
                    Padding = new Thickness(6, 3, 4, 3),
                    IsEnabled = false,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                taskSectionCombo.DisplayMemberPath = nameof(Curia.Models.AsanaSectionMeta.DisplayLabel);
                Grid.SetColumn(taskSectionCombo, 1); Grid.SetRow(taskSectionCombo, 1);

                // Due Date label + DatePicker (Row 0-1, Col 2)
                var dateLabel = new System.Windows.Controls.TextBlock
                {
                    Text = "Due Date", Foreground = subtext, FontSize = 10,
                    Margin = new Thickness(0, 0, 8, 1)
                };
                Grid.SetColumn(dateLabel, 2); Grid.SetRow(dateLabel, 0);

                var taskDatePicker = new System.Windows.Controls.DatePicker
                {
                    Width = 120, Foreground = text,
                    Background = surface1,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                Grid.SetColumn(taskDatePicker, 2); Grid.SetRow(taskDatePicker, 1);

                // "Set time" CheckBox (Row 1, Col 3) — チェックで時刻行を表示
                var setTimeChk = new System.Windows.Controls.CheckBox
                {
                    Content = "Set time", Foreground = subtext, FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 0)
                };
                Grid.SetColumn(setTimeChk, 3); Grid.SetRow(setTimeChk, 1);

                // Time picker Row (Row 2, Col 2-3) — 初期非表示
                var timePickerRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Visibility = Visibility.Collapsed,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                var taskHourCombo = new System.Windows.Controls.ComboBox
                {
                    Width = 60, Foreground = text, Background = surface1,
                    Padding = new Thickness(6, 3, 4, 3)
                };
                foreach (var h in Enumerable.Range(0, 24)) taskHourCombo.Items.Add(h.ToString("00"));
                taskHourCombo.SelectedIndex = 9;  // default 09:00
                var colonLbl = new System.Windows.Controls.TextBlock
                {
                    Text = ":", Foreground = text, FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 4, 0)
                };
                var taskMinCombo = new System.Windows.Controls.ComboBox
                {
                    Width = 60, Foreground = text, Background = surface1,
                    Padding = new Thickness(6, 3, 4, 3)
                };
                foreach (var m in new[] { "00", "15", "30", "45" }) taskMinCombo.Items.Add(m);
                taskMinCombo.SelectedIndex = 0;
                timePickerRow.Children.Add(taskHourCombo);
                timePickerRow.Children.Add(colonLbl);
                timePickerRow.Children.Add(taskMinCombo);
                Grid.SetColumn(timePickerRow, 2); Grid.SetRow(timePickerRow, 2);
                Grid.SetColumnSpan(timePickerRow, 2);

                setTimeChk.Checked   += (s, e) => timePickerRow.Visibility = Visibility.Visible;
                setTimeChk.Unchecked += (s, e) =>
                {
                    timePickerRow.Visibility = Visibility.Collapsed;
                    capturedTask.DueTime = "";
                };

                settingsGrid.Children.Add(projLabel);
                settingsGrid.Children.Add(taskProjectCombo);
                settingsGrid.Children.Add(secLabel);
                settingsGrid.Children.Add(taskSectionCombo);
                settingsGrid.Children.Add(dateLabel);
                settingsGrid.Children.Add(taskDatePicker);
                settingsGrid.Children.Add(setTimeChk);
                settingsGrid.Children.Add(timePickerRow);
                asanaPanel.Children.Add(settingsGrid);

                // Project ComboBox をプロジェクト一覧で初期化
                if (asanaProjectMetas.Count == 0)
                {
                    taskProjectCombo.Items.Add(new Curia.Models.AsanaProjectMeta { Gid = "", Name = "No Asana config" });
                    taskProjectCombo.SelectedIndex = 0;
                    taskProjectCombo.IsEnabled = false;
                }
                else
                {
                    foreach (var pm in asanaProjectMetas) taskProjectCombo.Items.Add(pm);

                    // プロジェクト選択変更 → セクションをロード
                    taskProjectCombo.SelectionChanged += async (s, e) =>
                    {
                        var selProj = taskProjectCombo.SelectedItem as Curia.Models.AsanaProjectMeta;
                        if (selProj == null || string.IsNullOrEmpty(selProj.Gid))
                        {
                            taskSectionCombo.Items.Clear();
                            taskSectionCombo.Items.Add(new Curia.Models.AsanaSectionMeta { Gid = "", Name = "(none)" });
                            taskSectionCombo.SelectedIndex = 0;
                            taskSectionCombo.IsEnabled = false;
                            capturedTask.ProjectGid  = "";
                            capturedTask.ProjectName = "";
                            return;
                        }
                        capturedTask.ProjectGid  = selProj.Gid;
                        capturedTask.ProjectName = selProj.Name;
                        capturedTask.SectionGid  = "";
                        capturedTask.SectionName = "";

                        taskSectionCombo.IsEnabled = false;
                        taskSectionCombo.Items.Clear();
                        taskSectionCombo.Items.Add(new Curia.Models.AsanaSectionMeta { Gid = "", Name = "Loading..." });
                        taskSectionCombo.SelectedIndex = 0;

                        var sections = await LoadSectionsAsync(selProj.Gid);
                        PopulateTaskSectionCombo(taskSectionCombo, sections, capturedTask);
                    };

                    taskSectionCombo.SelectionChanged += (s, e) =>
                    {
                        var sel = taskSectionCombo.SelectedItem as Curia.Models.AsanaSectionMeta;
                        capturedTask.SectionGid  = sel?.Gid ?? "";
                        capturedTask.SectionName = sel?.Name ?? "";
                    };

                    taskDatePicker.SelectedDateChanged += (s, e) =>
                    {
                        capturedTask.DueOn = taskDatePicker.SelectedDate.HasValue
                            ? taskDatePicker.SelectedDate.Value.ToString("yyyy-MM-dd") : "";
                    };

                    void UpdateDueTime()
                    {
                        if (setTimeChk.IsChecked == true)
                            capturedTask.DueTime = $"{taskHourCombo.SelectedItem}:{taskMinCombo.SelectedItem}";
                    }
                    taskHourCombo.SelectionChanged += (s, e) => UpdateDueTime();
                    taskMinCombo.SelectionChanged  += (s, e) => UpdateDueTime();

                    // デフォルトプロジェクトを事前選択
                    if (defaultProjectMeta != null)
                    {
                        taskProjectCombo.SelectedItem = defaultProjectMeta;
                        capturedTask.ProjectGid  = defaultProjectMeta.Gid;
                        capturedTask.ProjectName = defaultProjectMeta.Name;
                        // キャッシュ済みセクションで即時初期化
                        if (sectionCache.TryGetValue(defaultProjectMeta.Gid, out var cachedSections))
                            PopulateTaskSectionCombo(taskSectionCombo, cachedSections, capturedTask);
                    }
                }
            }
        }

        var asanaScroll = new ScrollViewer
        {
            Content = asanaPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        asanaTab.Content = asanaScroll;

        tabControl.Items.Add(decisionsTab);
        tabControl.Items.Add(focusTab);
        tabControl.Items.Add(tensionsTab);
        tabControl.Items.Add(asanaTab);

        // ---- フッター ----
        var applyButton = new Wpf.Ui.Controls.Button
        {
            Content = "Apply Selected",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            MinWidth = 120, Height = 32, Margin = new Thickness(0, 0, 8, 0)
        };
        var cancelFooterBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Cancel",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            MinWidth = 80, Height = 32, IsCancel = true
        };
        var debugFooterBtn = new Wpf.Ui.Controls.Button
        {
            Content = "View Debug",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            MinWidth = 90, Height = 32
        };
        var footerLeft  = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Left,  Children = { debugFooterBtn } };
        var footerRight = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Children = { applyButton, cancelFooterBtn } };
        var footer = new Grid { Margin = new Thickness(16, 6, 16, 12) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(footerLeft,  0);
        Grid.SetColumn(footerRight, 2);
        footer.Children.Add(footerLeft);
        footer.Children.Add(footerRight);

        // ---- レイアウト ----
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titleBar,   0);
        Grid.SetRow(summaryBar, 1);
        Grid.SetRow(tabControl, 2);
        Grid.SetRow(footer,     3);
        root.Children.Add(titleBar);
        root.Children.Add(summaryBar);
        root.Children.Add(tabControl);
        root.Children.Add(footer);

        var dialog = new Window
        {
            Content = root, Width = 780, Height = 580,
            MinWidth = 500, MinHeight = 400,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.CanResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this), ShowInTaskbar = false,
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

        void Complete(bool apply)
        {
            if (!tcs.Task.IsCompleted)
                tcs.SetResult(apply);
            dialog.Close();
            dialog.Owner?.Activate();
        }

        titleBar.MouseLeftButtonDown += (s, e) => dialog.DragMove();
        closeBtn.Click              += (s, e) => Complete(false);
        cancelFooterBtn.Click       += (s, e) => Complete(false);
        applyButton.Click += (s, e) => Complete(true);
        dialog.Closed += (s, e) =>
        {
            if (!tcs.Task.IsCompleted) tcs.SetResult(false);
            dialog.Owner?.Activate();
        };

        debugFooterBtn.Click += (s, e) =>
        {
            var debugText = new StringBuilder();
            debugText.AppendLine("=== SYSTEM PROMPT ===");
            debugText.AppendLine(analysisResult.DebugSystemPrompt);
            debugText.AppendLine();
            debugText.AppendLine("=== USER PROMPT ===");
            debugText.AppendLine(analysisResult.DebugUserPrompt);
            debugText.AppendLine();
            debugText.AppendLine("=== RESPONSE ===");
            debugText.AppendLine(analysisResult.DebugResponse);

            var dbTitleBar = new Grid { Background = surface1, Height = 38 };
            dbTitleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            dbTitleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var dbTitleBlock = new System.Windows.Controls.TextBlock
            {
                Text = "LLM Debug Log", Foreground = text, FontSize = 14, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0)
            };
            Grid.SetColumn(dbTitleBlock, 0);
            var dbCloseBtn = new System.Windows.Controls.Button
            {
                Content = "✕", Width = 34, Height = 26,
                Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0), Foreground = subtext, FontSize = 13
            };
            Grid.SetColumn(dbCloseBtn, 1);
            dbTitleBar.Children.Add(dbTitleBlock);
            dbTitleBar.Children.Add(dbCloseBtn);

            var dbTextBox = new System.Windows.Controls.TextBox
            {
                Text = debugText.ToString(), IsReadOnly = true, TextWrapping = TextWrapping.NoWrap,
                AcceptsReturn = true,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = surface1, Foreground = text,
                BorderBrush = surface2, BorderThickness = new Thickness(1),
                Padding = new Thickness(8),
                FontFamily = new System.Windows.Media.FontFamily("JetBrains Mono, Cascadia Mono, Consolas"),
                FontSize = 11
            };

            var dbRoot = new Grid();
            dbRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            dbRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(dbTitleBar, 0); Grid.SetRow(dbTextBox, 1);
            dbRoot.Children.Add(dbTitleBar); dbRoot.Children.Add(dbTextBox);

            var dbDialog = new Window
            {
                Content = dbRoot, Width = 640, Height = 500, MinWidth = 400, MinHeight = 300,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.CanResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = dialog, ShowInTaskbar = false, Background = surface
            };
            System.Windows.Shell.WindowChrome.SetWindowChrome(dbDialog,
                new System.Windows.Shell.WindowChrome
                {
                    CaptionHeight = 0, ResizeBorderThickness = new Thickness(4),
                    GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false
                });
            dbTitleBar.MouseLeftButtonDown += (_, _) => dbDialog.DragMove();
            dbCloseBtn.Click += (_, _) => dbDialog.Close();
            dbDialog.Show();
        };

        dialog.Show();
        return await tcs.Task;
    }
}

internal class InputDialog : Window
{
    private readonly System.Windows.Controls.TextBox _textBox;
    public string InputText => _textBox.Text;
    public InputDialog(string title, string prompt)
    {
        Title = title;
        Width = 440;
        Height = 190;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var appResources = Application.Current.Resources;
        var appSurface0 = appResources["AppSurface0"] as System.Windows.Media.Brush;
        var appSurface1 = appResources["AppSurface1"] as System.Windows.Media.Brush;
        var appSurface2 = appResources["AppSurface2"] as System.Windows.Media.Brush;
        var appText = appResources["AppText"] as System.Windows.Media.Brush;
        var appSubtext0 = appResources["AppSubtext0"] as System.Windows.Media.Brush;

        var fallbackSurface0 = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#111827"));
        var fallbackSurface1 = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1f2937"));
        var fallbackSurface2 = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#374151"));
        var fallbackText = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#e5e7eb"));
        var fallbackSubtext = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#cbd5e1"));

        Background = appSurface0 ?? fallbackSurface0;
        Foreground = appText ?? fallbackText;

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = prompt,
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = appText ?? fallbackText
        });

        _textBox = new System.Windows.Controls.TextBox
        {
            Margin = new Thickness(0, 0, 0, 12),
            Background = appSurface1 ?? fallbackSurface1,
            Foreground = appText ?? fallbackText,
            BorderBrush = appSurface2 ?? fallbackSurface2,
            CaretBrush = appText ?? fallbackText,
            Padding = new Thickness(8, 6, 8, 6)
        };
        _textBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) DialogResult = true; else if (e.Key == Key.Escape) DialogResult = false; };
        panel.Children.Add(_textBox);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var ok = new System.Windows.Controls.Button
        {
            Content = "Create",
            Width = 88,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true,
            Background = appSurface2 ?? fallbackSurface2,
            Foreground = appText ?? fallbackText,
            BorderBrush = appSurface2 ?? fallbackSurface2
        };
        ok.Click += (s, e) => DialogResult = true;

        var cancel = new System.Windows.Controls.Button
        {
            Content = "Cancel",
            Width = 88,
            IsCancel = true,
            Background = appSurface1 ?? fallbackSurface1,
            Foreground = appSubtext0 ?? fallbackSubtext,
            BorderBrush = appSurface2 ?? fallbackSurface2
        };
        cancel.Click += (s, e) => DialogResult = false;

        btnPanel.Children.Add(ok);
        btnPanel.Children.Add(cancel);
        panel.Children.Add(btnPanel);

        var border = new Border
        {
            BorderBrush = appSurface2 ?? fallbackSurface2,
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false
        };
        var root = new Grid();
        root.Children.Add(panel);
        root.Children.Add(border);

        Content = root;
        Loaded += (s, e) => _textBox.Focus();
    }
}

