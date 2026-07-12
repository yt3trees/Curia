using System.ComponentModel;
using System.Windows;
using Wpf.Ui.Controls;
using Curia.ViewModels;
using WpfUserControl = System.Windows.Controls.UserControl;

namespace Curia.Views.Pages;

public partial class SetupPage : WpfUserControl, INavigableView<SetupViewModel>
{
    public SetupViewModel ViewModel { get; }
    private bool _propertyChangedConnected;

    public SetupPage(SetupViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = ViewModel;

        ViewModel.OnOpenWorkstreamFocusInEditor = (project, filePath) =>
        {
            if (Window.GetWindow(this) is MainWindow mw)
                mw.NavigateToEditorAndOpenFile(project, filePath);
        };

        InitializeComponent();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.EnsureInitializedAsync();

        // OutputText 変化時に自動スクロール
        if (!_propertyChangedConnected)
        {
            _propertyChangedConnected = true;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SetupViewModel.OutputText))
        {
            OutputScrollViewer.ScrollToEnd();
        }
    }
}
