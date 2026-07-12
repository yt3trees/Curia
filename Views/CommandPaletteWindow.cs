using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Curia.Models;
using Curia.ViewModels;
using Curia.Views.Pages;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfListBoxItem = System.Windows.Controls.ListBoxItem;
using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace Curia.Views;

/// <summary>
/// Command Palette ウィンドウ。Ctrl+K / Ctrl+Shift+K でどこからでも起動する。
/// </summary>
public class CommandPaletteWindow : Window
{
    private readonly CommandPaletteViewModel _viewModel;
    private readonly MainWindow _mainWindow;

    private System.Windows.Controls.TextBox _searchBox = null!;
    private WpfListBox _commandList = null!;
    private TextBlock _askHint = null!;
    private StackPanel _conversationPanel = null!;

    // IME 制御は日本語環境でのみ行う
    private static readonly bool _needsImeControl =
        System.Globalization.CultureInfo.CurrentCulture.TwoLetterISOLanguageName == "ja" ||
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja";
    private bool _canCloseOnDeactivate = false;
    private bool _suppressTextChanged = false;

    // 回答テキストからインライン引用 [C:\path\file.md:L42] "excerpt..." を除去する正規表現
    // [^\]]* で ] 以外すべてにマッチさせ、パス区切り文字の存在で引用と判定する
    private static readonly System.Text.RegularExpressions.Regex StripCitationRegex =
        new(@"\[[^\]]*[/\\][^\]]*\](?:\s*""[^""]{0,600}"")?\s*",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    public CommandPaletteWindow(CommandPaletteViewModel viewModel, MainWindow mainWindow)
    {
        _viewModel = viewModel;
        _mainWindow = mainWindow;

        // ── ウィンドウ基本設定 ─────────────────────────────────────────────
        // NoResize + WindowStyle.None では DWM 白枠が出ないため WindowChrome 不要
        // (AGENTS.md: NoResize ダイアログに WindowChrome を適用しない)
        WindowStyle           = WindowStyle.None;
        ResizeMode            = ResizeMode.NoResize;
        SizeToContent         = SizeToContent.Height;
        MinHeight             = 0;
        Width                 = 640;
        MaxHeight             = 520;
        Topmost               = true;
        ShowInTaskbar         = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        // ── 位置: 検索ボックス（行高さ約40px）の中心がモニター中央に来るよう配置 ───
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top  = workArea.Top  + (workArea.Height / 2) - 20;

        // ── UI 構築 ──────────────────────────────────────────────────────
        Content = BuildContent();
        DataContext = _viewModel;

        // ContentRendered 後（ウィンドウが完全に描画・活性化されてから）のみ
        // Deactivated でクローズする。Show() 中の一時的な非活性化を誤検知しないため。
        // BeginInvoke で次フレームに遅延させることで、非活性化メッセージ処理中の
        // 再入 (reentrancy) によるフリーズを防ぐ。
        ContentRendered += (_, _) => _canCloseOnDeactivate = true;
        Deactivated += (_, _) =>
        {
            if (_canCloseOnDeactivate)
                Dispatcher.BeginInvoke(new Action(Close), System.Windows.Threading.DispatcherPriority.Normal);
        };

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        _viewModel.OnAskAgent = async question =>
        {
            _canCloseOnDeactivate = false;
            _mainWindow.RootNavigation.Navigate(typeof(AgentChatPage));
            var agent = ((App)Application.Current).Services.GetRequiredService<AgentChatViewModel>();
            await agent.InitializeAsync();
            await agent.SubmitAsync(question);
            _mainWindow.BringToFront();
            Close();
        };

        // Window レベルで Escape を捕捉（グローバルホットキー起動時もフォーカスに依存しない）
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                _canCloseOnDeactivate = false;
                Close();
                e.Handled = true;
            }
        };

        // Activated で設定することで、グローバルホットキー経由（他アプリからフォーカス移動）でも
        // 確実にキーボードフォーカスが searchBox に届く
        Activated += (_, _) =>
        {
            _searchBox.Focus();
            Keyboard.Focus(_searchBox);
            if (_needsImeControl)
                System.Windows.Input.InputMethod.SetIsInputMethodEnabled(_searchBox, false);
        };
    }

    private FrameworkElement BuildContent()
    {
        this.SetResourceReference(Window.BackgroundProperty, "AppSurface0");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 検索ボックス
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // セパレータ
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MaxHeight = 400 }); // コンテンツ

        // ── 検索ボックス行 ────────────────────────────────────────────────
        var searchRow = new Border { Padding = new Thickness(8, 8, 8, 8) };
        searchRow.SetResourceReference(Border.BackgroundProperty, "AppBackground");

        _searchBox = new System.Windows.Controls.TextBox
        {
            FontSize        = 15,
            Padding         = new Thickness(4, 4, 4, 4),
            BorderThickness = new Thickness(0),
            AcceptsReturn   = false,
        };
        _searchBox.SetResourceReference(System.Windows.Controls.TextBox.BackgroundProperty, "AppBackground");
        _searchBox.SetResourceReference(System.Windows.Controls.TextBox.ForegroundProperty, "AppText");
        _searchBox.TextChanged += (_, _) =>
        {
            if (!_suppressTextChanged) _viewModel.SearchText = _searchBox.Text;
        };
        _searchBox.PreviewKeyDown += OnSearchBoxKeyDown;
        searchRow.Child = _searchBox;
        Grid.SetRow(searchRow, 0);
        root.Children.Add(searchRow);

        // ── セパレータ ────────────────────────────────────────────────────
        var separator = new Border { Height = 1 };
        separator.SetResourceReference(Border.BackgroundProperty, "AppSurface1");
        Grid.SetRow(separator, 1);
        root.Children.Add(separator);

        // ── コンテンツエリア (通常: コマンドリスト、Ask モード: ヒント/ローディング/回答) ──
        var contentArea = new Grid();
        Grid.SetRow(contentArea, 2);
        root.Children.Add(contentArea);

        // コマンドリスト
        _commandList = new WpfListBox
        {
            BorderThickness = new Thickness(0),
            Padding         = new Thickness(0, 4, 0, 4),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_commandList, ScrollBarVisibility.Disabled);
        _commandList.SetResourceReference(WpfListBox.BackgroundProperty, "AppSurface0");
        _commandList.ItemTemplate = BuildItemTemplate();
        _commandList.ItemContainerStyle = BuildItemContainerStyle();
        _commandList.SetBinding(WpfListBox.ItemsSourceProperty,
            new System.Windows.Data.Binding(nameof(_viewModel.FilteredCommands)));
        _commandList.SetBinding(WpfListBox.SelectedItemProperty,
            new System.Windows.Data.Binding(nameof(_viewModel.SelectedCommand)) { Mode = System.Windows.Data.BindingMode.TwoWay });
        _commandList.MouseDoubleClick += (_, _) => ExecuteAndClose();
        contentArea.Children.Add(_commandList);

        // Ask モードヒント
        _askHint = new TextBlock
        {
            Text         = "Type your question and press Enter",
            FontSize     = 12,
            Margin       = new Thickness(16, 14, 16, 14),
            FontStyle    = FontStyles.Italic,
            Visibility   = Visibility.Collapsed,
        };
        _askHint.SetResourceReference(TextBlock.ForegroundProperty, "AppSubtext1");
        contentArea.Children.Add(_askHint);

        // 会話パネル (ScrollViewer 内にターン一覧)
        var conversationScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Visibility = Visibility.Collapsed,
        };
        // ScrollViewer への参照は _conversationPanel の親として保持
        _conversationPanel = new StackPanel();
        conversationScroll.Content = _conversationPanel;
        // タグで参照できるようにする
        conversationScroll.Tag = "conversationScroll";
        contentArea.Children.Add(conversationScroll);

        // ── 外枠ボーダーオーバーレイ ─────────────────────────────────────
        var borderOverlay = new Border
        {
            BorderThickness  = new Thickness(1),
            IsHitTestVisible = false,
        };
        borderOverlay.SetResourceReference(Border.BorderBrushProperty, "AppSurface2");
        Grid.SetRowSpan(borderOverlay, 3);
        root.Children.Add(borderOverlay);

        return root;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CommandPaletteViewModel.SearchText))
            Dispatcher.InvokeAsync(SyncSearchBoxText);
    }

    private void SyncSearchBoxText()
    {
        if (_searchBox.Text == _viewModel.SearchText) return;
        _suppressTextChanged = true;
        _searchBox.Text = _viewModel.SearchText;
        _searchBox.CaretIndex = _searchBox.Text.Length;
        _suppressTextChanged = false;
    }

    private ScrollViewer? FindConversationScroll()
    {
        var contentArea = (_conversationPanel.Parent as ScrollViewer)?.Parent as Grid
            ?? _conversationPanel.Parent as Grid;
        if (_conversationPanel.Parent is ScrollViewer sv) return sv;
        return null;
    }

    private void UpdateAskModeUI()
    {
        bool askMode = _viewModel.IsAskMode;
        bool loading = _viewModel.IsAskLoading;
        bool hasConversation = _viewModel.ConversationTurns.Count > 0;

        _commandList.Visibility = askMode ? Visibility.Collapsed : Visibility.Visible;
        _askHint.Visibility = askMode && !loading && !hasConversation
            ? Visibility.Visible : Visibility.Collapsed;

        var convScroll = FindConversationScroll();
        if (convScroll != null)
        {
            convScroll.Visibility = askMode && (hasConversation || loading)
                ? Visibility.Visible : Visibility.Collapsed;
        }

        if (askMode && (hasConversation || loading))
            RebuildConversationPanel();
    }

    private void RebuildConversationPanel()
    {
        _conversationPanel.Children.Clear();

        // 会話ヘッダー (New ボタン付き)
        if (_viewModel.ConversationTurns.Count > 0)
        {
            var header = new Border { Padding = new Thickness(14, 6, 14, 6) };
            header.SetResourceReference(Border.BackgroundProperty, "AppBackground");

            var headerRow = new Grid();
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var turnCount = new TextBlock
            {
                Text     = $"Conversation  ({_viewModel.ConversationTurns.Count} turn{(_viewModel.ConversationTurns.Count > 1 ? "s" : "")})",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            turnCount.SetResourceReference(TextBlock.ForegroundProperty, "AppSubtext1");
            Grid.SetColumn(turnCount, 0);

            var newBtn = new TextBlock
            {
                Text            = "New",
                FontSize        = 11,
                Padding         = new Thickness(8, 2, 8, 2),
                Cursor          = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
            };
            newBtn.SetResourceReference(TextBlock.ForegroundProperty, "AppBlue");
            newBtn.MouseLeftButtonUp += (_, _) =>
            {
                _viewModel.ResetConversation();
                _suppressTextChanged = true;
                _searchBox.Text = "?";
                _searchBox.CaretIndex = 1;
                _suppressTextChanged = false;
            };
            Grid.SetColumn(newBtn, 1);

            headerRow.Children.Add(turnCount);
            headerRow.Children.Add(newBtn);
            header.Child = headerRow;
            _conversationPanel.Children.Add(header);

            var headerSep = new Border { Height = 1 };
            headerSep.SetResourceReference(Border.BackgroundProperty, "AppSurface1");
            _conversationPanel.Children.Add(headerSep);
        }

        foreach (var turn in _viewModel.ConversationTurns)
            _conversationPanel.Children.Add(BuildTurnBlock(turn));

        if (_viewModel.IsAskLoading)
        {
            var loadingBlock = new TextBlock
            {
                Text      = "Searching...",
                FontSize  = 12,
                FontStyle = FontStyles.Italic,
                Margin    = new Thickness(16, 10, 16, 10),
            };
            loadingBlock.SetResourceReference(TextBlock.ForegroundProperty, "AppSubtext1");
            _conversationPanel.Children.Add(loadingBlock);
        }

        var sv = FindConversationScroll();
        sv?.ScrollToBottom();
    }

    private FrameworkElement BuildTurnBlock(CuriaConversationTurn turn)
    {
        var turnBorder = new Border
        {
            Margin          = new Thickness(0, 0, 0, 1),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        turnBorder.SetResourceReference(Border.BorderBrushProperty, "AppSurface1");

        var stack = new StackPanel();

        // Question row
        var qBorder = new Border { Padding = new Thickness(14, 8, 14, 8) };
        qBorder.SetResourceReference(Border.BackgroundProperty, "AppSurface1");
        var qBlock = new TextBlock
        {
            Text         = turn.Question,
            FontSize     = 12,
            FontWeight   = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        qBlock.SetResourceReference(TextBlock.ForegroundProperty, "AppSubtext1");
        qBorder.Child = qBlock;
        stack.Children.Add(qBorder);

        // Answer text (paths shortened for readability)
        var answerBlock = new TextBlock
        {
            Text         = StripInlineCitations(turn.AnswerText),
            FontSize     = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(14, 10, 14, 10),
            LineHeight   = 22,
        };
        answerBlock.SetResourceReference(TextBlock.ForegroundProperty, "AppText");
        stack.Children.Add(answerBlock);

        // Citations
        if (turn.Citations.Count > 0)
        {
            var citBorder = new Border { Padding = new Thickness(14, 6, 14, 8) };
            citBorder.SetResourceReference(Border.BackgroundProperty, "AppSurface1");

            var citStack = new StackPanel();
            var sourcesLabel = new TextBlock
            {
                Text     = "Sources",
                FontSize = 11,
                Margin   = new Thickness(0, 0, 0, 4),
            };
            sourcesLabel.SetResourceReference(TextBlock.ForegroundProperty, "AppSubtext1");
            citStack.Children.Add(sourcesLabel);

            foreach (var citation in turn.Citations)
                citStack.Children.Add(BuildCitationRow(citation));

            citBorder.Child = citStack;
            stack.Children.Add(citBorder);
        }

        turnBorder.Child = stack;
        return turnBorder;
    }

    private FrameworkElement BuildCitationRow(CuriaCitation citation)
    {
        var fileName = System.IO.Path.GetFileName(citation.Path.Split('#')[0]);
        var lineInfo = citation.LineHint.HasValue ? $":L{citation.LineHint}" : "";
        var project  = string.IsNullOrEmpty(citation.ProjectId) ? "" : $"[{citation.ProjectId}]  ";
        var display  = $"{project}{fileName}{lineInfo}";

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };

        var bullet = new TextBlock { Text = "• ", FontSize = 11 };
        bullet.SetResourceReference(TextBlock.ForegroundProperty, "AppSubtext1");

        var tb = new TextBlock
        {
            Text            = display,
            FontSize        = 11,
            TextDecorations = TextDecorations.Underline,
            Cursor          = System.Windows.Input.Cursors.Hand,
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "AppBlue");
        tb.MouseLeftButtonUp += (_, _) => _ = _viewModel.OpenCitationAsync(citation);

        panel.Children.Add(bullet);
        panel.Children.Add(tb);
        return panel;
    }

    private static string StripInlineCitations(string text)
    {
        var result = StripCitationRegex.Replace(text, "");
        // 連続スペース・3行以上の空行を整理
        result = System.Text.RegularExpressions.Regex.Replace(result, @"  +", " ");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\n{3,}", "\n\n");
        return result.Trim();
    }

    private DataTemplate BuildItemTemplate()
    {
        var factory = new FrameworkElementFactory(typeof(Border));
        factory.SetValue(Border.PaddingProperty, new Thickness(12, 6, 12, 6));
        factory.SetValue(Border.BackgroundProperty, WpfBrushes.Transparent);

        var textFactory = new FrameworkElementFactory(typeof(TextBlock));
        textFactory.SetValue(TextBlock.FontSizeProperty, 13.0);
        textFactory.SetBinding(TextBlock.TextProperty,
            new System.Windows.Data.Binding(nameof(CommandItem.Display)));
        textFactory.SetResourceReference(TextBlock.ForegroundProperty, "AppText");

        factory.AppendChild(textFactory);

        return new DataTemplate { VisualTree = factory };
    }

    private Style BuildItemContainerStyle()
    {
        var style = new Style(typeof(WpfListBoxItem));

        // ControlTemplate: Border の Background を ListBoxItem.Background に TemplateBinding する
        var template = new ControlTemplate(typeof(WpfListBoxItem));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
        borderFactory.SetValue(Border.SnapsToDevicePixelsProperty, true);
        borderFactory.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        template.VisualTree = borderFactory;
        style.Setters.Add(new Setter(WpfListBoxItem.TemplateProperty, template));

        // デフォルト背景
        style.Setters.Add(new Setter(WpfListBoxItem.BackgroundProperty, WpfBrushes.Transparent));

        // 選択時: Style トリガーで ListBoxItem.Background を変更 (名前付き要素参照不要)
        var selectedTrigger = new Trigger { Property = WpfListBoxItem.IsSelectedProperty, Value = true };
        selectedTrigger.Setters.Add(new Setter(WpfListBoxItem.BackgroundProperty,
            new DynamicResourceExtension("AppSurface1")));
        style.Triggers.Add(selectedTrigger);

        // ホバー時
        var hoverTrigger = new Trigger { Property = WpfListBoxItem.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(WpfListBoxItem.BackgroundProperty,
            new DynamicResourceExtension("AppSurface2")));
        style.Triggers.Add(hoverTrigger);

        return style;
    }

    private void OnSearchBoxKeyDown(object sender, WpfKeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Enter:
                if (_searchBox.Text.StartsWith("?"))
                {
                    _ = _viewModel.AskAsync(_searchBox.Text);
                    e.Handled = true;
                }
                else
                {
                    ExecuteAndClose();
                    e.Handled = true;
                }
                break;
            case Key.Down:
                MoveSelection(1); e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1); e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        int count = _commandList.Items.Count;
        if (count == 0) return;
        int index = Math.Clamp(_commandList.SelectedIndex + delta, 0, count - 1);
        _commandList.SelectedIndex = index;
        _commandList.ScrollIntoView(_commandList.SelectedItem);
    }

    private void ExecuteAndClose()
    {
        _canCloseOnDeactivate = false; // Deactivated による二重クローズを防ぐ
        _viewModel.ExecuteSelected(cmd =>
        {
            Close();
            cmd.Action?.Invoke(_mainWindow);
        });
    }
}
