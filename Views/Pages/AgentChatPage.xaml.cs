using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Input;
using Curia.ViewModels;
using Wpf.Ui.Controls;
using WpfUserControl = System.Windows.Controls.UserControl;

namespace Curia.Views.Pages;

public partial class AgentChatPage : WpfUserControl, INavigableView<AgentChatViewModel>
{
    public AgentChatViewModel ViewModel { get; }

    public AgentChatPage(AgentChatViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = ViewModel;
        InitializeComponent();
        ViewModel.Messages.CollectionChanged += OnMessagesChanged;
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

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e) => await ViewModel.InitializeAsync();
}