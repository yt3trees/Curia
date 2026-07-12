using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using Curia.ViewModels;
using WpfUserControl = System.Windows.Controls.UserControl;

namespace Curia.Views.Pages;

public partial class AsanaSyncPage : WpfUserControl, INavigableView<AsanaSyncViewModel>
{
    public AsanaSyncViewModel ViewModel { get; }
    private bool _logEntriesConnected;

    public AsanaSyncPage(AsanaSyncViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = ViewModel;
        InitializeComponent();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_logEntriesConnected)
        {
            _logEntriesConnected = true;
            ViewModel.LogEntries.CollectionChanged += OnLogEntriesChanged;
        }
        await ViewModel.EnsureInitializedAsync();
    }

    private void OnLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            Dispatcher.InvokeAsync(() => LogScrollViewer.ScrollToBottom(), DispatcherPriority.Background);
        else if (e.Action == NotifyCollectionChangedAction.Reset)
            Dispatcher.InvokeAsync(() => LogScrollViewer.ScrollToTop(), DispatcherPriority.Background);
    }
}
