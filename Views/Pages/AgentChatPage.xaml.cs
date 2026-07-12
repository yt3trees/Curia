using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Curia.ViewModels;
using Curia.Services.Agent;
using Curia.Models;
using Curia.Views;
using Wpf.Ui.Controls;
using WpfUserControl = System.Windows.Controls.UserControl;

namespace Curia.Views.Pages;

public partial class AgentChatPage : WpfUserControl, INavigableView<AgentChatViewModel>
{
    public AgentChatViewModel ViewModel { get; }

    private readonly AgentUiActions _uiActions;

    public AgentChatPage(AgentChatViewModel viewModel, AgentUiActions uiActions)
    {
        ViewModel = viewModel;
        _uiActions = uiActions;
        DataContext = ViewModel;
        InitializeComponent();
        ViewModel.Messages.CollectionChanged += OnMessagesChanged;
        _uiActions.OpenInEditorAsync = async (project, path) => await Dispatcher.InvokeAsync(() =>
        {
            if (System.Windows.Window.GetWindow(this) is MainWindow window) window.NavigateToEditorAndOpenFile(project, path);
        });
        _uiActions.NavigateAsync = async (page, project) => await Dispatcher.InvokeAsync(() =>
        {
            if (System.Windows.Window.GetWindow(this) is not MainWindow window) return;
            if (page == "timeline" && project != null)
            {
                window.NavigateToTimeline(project);
                return;
            }
            window.RootNavigation.Navigate(page switch
            {
                "dashboard" => typeof(DashboardPage), "wiki" => typeof(WikiPage), "schedule" => typeof(WeeklySchedulePage),
                "editor" => typeof(EditorPage), "timeline" => typeof(TimelinePage), _ => typeof(SettingsPage)
            });
        });
        ViewModel.RefreshTools();
        _uiActions.ReviewFocusUpdateAsync = async (result, refine) => await Dispatcher.InvokeAsync(async () =>
        {
            var owner = System.Windows.Window.GetWindow(this);
            if (owner == null) return (false, (string?)null);
            return await ProposalReviewDialog.ShowAsync(
                owner,
                result,
                "Review Focus Update",
                extraInfo: result.Summary,
                refineFunc: refine);
        }).Task.Unwrap();
        _uiActions.ReviewDecisionLogAsync = async (proposal, refine) => await Dispatcher.InvokeAsync(async () =>
        {
            var owner = System.Windows.Window.GetWindow(this);
            if (owner == null) return (false, (string?)null);
            return await ProposalReviewDialog.ShowAsync(
                owner,
                proposal,
                "Review Decision Log",
                titleIcon: "D",
                extraInfo: proposal.Summary,
                refineFunc: refine);
        }).Task.Unwrap();
    }

    private void OnInputKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && ViewModel.SendCommand.CanExecute(null))
        {
            ViewModel.SendCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ViewModel.Messages.Count > 0)
            Dispatcher.BeginInvoke(MessageScrollViewer.ScrollToEnd);
    }

    private void OnMessageScrollPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        MessageScrollViewer.ScrollToVerticalOffset(
            MessageScrollViewer.VerticalOffset - e.Delta / 3d);
        e.Handled = true;
    }

    private void OnMarkdownViewerLoaded(object sender, System.Windows.RoutedEventArgs e) =>
        QueueMarkdownTheme(sender);

    private void OnMarkdownViewerDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e) =>
        QueueMarkdownTheme(sender);

    private void OnMarkdownViewerTargetUpdated(object sender, System.Windows.Data.DataTransferEventArgs e) =>
        QueueMarkdownTheme(sender);

    private void QueueMarkdownTheme(object sender)
    {
        if (sender is not Markdig.Wpf.MarkdownViewer viewer) return;

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () =>
        {
            if (viewer.Document != null) ApplyMarkdownTheme(viewer.Document);
        });
    }

    private static void ApplyMarkdownTheme(FlowDocument document)
    {
        var resources = System.Windows.Application.Current.Resources;
        var text = resources["AppText"] as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White;
        var codeBackground = resources["AppSurface2"] as System.Windows.Media.Brush
            ?? System.Windows.Media.Brushes.DimGray;
        var tableBorder = resources["AppSurface2"] as System.Windows.Media.Brush
            ?? System.Windows.Media.Brushes.DimGray;

        document.PagePadding = new System.Windows.Thickness(0);
        document.FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Yu Gothic UI, Meiryo UI, Arial");
        document.FontSize = 14;
        document.LineHeight = 21;
        document.Foreground = text;

        foreach (Block block in document.Blocks)
            ApplyMarkdownTheme(block, text, codeBackground, tableBorder);
    }

    private static void ApplyMarkdownTheme(
        Block block,
        System.Windows.Media.Brush text,
        System.Windows.Media.Brush codeBackground,
        System.Windows.Media.Brush tableBorder,
        bool isInList = false)
    {
        block.Foreground = text;
        if (HasLocalBackground(block))
        {
            block.Background = codeBackground;
            block.Foreground = text;
            block.FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas, Courier New");
            block.FontSize = 13;
        }

        switch (block)
        {
            case Paragraph paragraph:
                if (isInList)
                    paragraph.Margin = new System.Windows.Thickness(0, 0, 0, 3);
                foreach (Inline inline in paragraph.Inlines)
                    ApplyMarkdownTheme(inline, text, codeBackground);
                break;
            case Section section:
                foreach (Block child in section.Blocks)
                    ApplyMarkdownTheme(child, text, codeBackground, tableBorder, isInList);
                break;
            case List list:
                list.Margin = new System.Windows.Thickness(0, 4, 0, 6);
                foreach (ListItem item in list.ListItems)
                    foreach (Block child in item.Blocks)
                        ApplyMarkdownTheme(child, text, codeBackground, tableBorder, true);
                break;
            case Table table:
                table.BorderBrush = tableBorder;
                table.BorderThickness = new System.Windows.Thickness(1);
                table.CellSpacing = 0;
                foreach (TableRowGroup group in table.RowGroups)
                    foreach (TableRow row in group.Rows)
                        foreach (TableCell cell in row.Cells)
                        {
                            cell.BorderBrush = tableBorder;
                            cell.BorderThickness = new System.Windows.Thickness(0, 0, 1, 1);
                            foreach (Block child in cell.Blocks)
                                ApplyMarkdownTheme(child, text, codeBackground, tableBorder, isInList);
                        }
                break;
        }
    }

    private static void ApplyMarkdownTheme(
        Inline inline,
        System.Windows.Media.Brush text,
        System.Windows.Media.Brush codeBackground)
    {
        inline.Foreground = text;
        if (HasLocalBackground(inline))
        {
            inline.Background = codeBackground;
            inline.Foreground = text;
            inline.FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas, Courier New");
            inline.FontSize = 13;
        }

        if (inline is Span span)
            foreach (Inline child in span.Inlines)
                ApplyMarkdownTheme(child, text, codeBackground);
    }

    private static bool HasLocalBackground(System.Windows.DependencyObject element) =>
        element.ReadLocalValue(TextElement.BackgroundProperty) != System.Windows.DependencyProperty.UnsetValue;

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e) => await ViewModel.EnsureInitializedAsync();
}