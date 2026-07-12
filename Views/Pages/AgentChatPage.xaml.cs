using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Input;
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
        _uiActions.OpenInTimelineAsync = async project => await Dispatcher.InvokeAsync(() =>
        {
            if (System.Windows.Window.GetWindow(this) is MainWindow window) window.NavigateToTimeline(project);
        });
        _uiActions.NavigateAsync = async page => await Dispatcher.InvokeAsync(() =>
        {
            if (System.Windows.Window.GetWindow(this) is not MainWindow window) return;
            window.RootNavigation.Navigate(page switch
            {
                "dashboard" => typeof(DashboardPage), "wiki" => typeof(WikiPage), "schedule" => typeof(WeeklySchedulePage),
                "editor" => typeof(EditorPage), "timeline" => typeof(TimelinePage), _ => typeof(SettingsPage)
            });
        });
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

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e) => await ViewModel.EnsureInitializedAsync();
}