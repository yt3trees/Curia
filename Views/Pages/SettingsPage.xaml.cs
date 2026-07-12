using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using Curia.ViewModels;
using WpfUserControl = System.Windows.Controls.UserControl;

namespace Curia.Views.Pages;

public partial class SettingsPage : WpfUserControl, INavigableView<SettingsViewModel>
{
    public SettingsViewModel ViewModel { get; }
    private bool _propertyChangedConnected;

    public SettingsPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = ViewModel;

        // ホットキー表示更新
        ViewModel.OnHotkeyDisplayChanged = display =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (Window.GetWindow(this) is MainWindow mw)
                    mw.UpdateTrayHotkeyDisplay(display);
            });
        };

        InitializeComponent();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.EnsureInitializedAsync();
        InitAutoRefreshCombo();
        // PasswordBox はバインディング非対応のため、ロード後に手動でセット
        LlmApiKeyBox.Password = ViewModel.LlmApiKey;
        AsanaTokenBox.Password = ViewModel.AsanaToken;
        UpdateColorPreviews();
        if (!_propertyChangedConnected)
        {
            _propertyChangedConnected = true;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.EditorTextColor)
                           or nameof(SettingsViewModel.MarkdownRenderTextColor))
            UpdateColorPreviews();
    }

    private void UpdateColorPreviews()
    {
        EditorColorPreview.Background   = MakeBrush(ViewModel.EditorColorR,   ViewModel.EditorColorG,   ViewModel.EditorColorB);
        MarkdownColorPreview.Background = MakeBrush(ViewModel.MarkdownColorR, ViewModel.MarkdownColorG, ViewModel.MarkdownColorB);
    }

    private static System.Windows.Media.SolidColorBrush MakeBrush(int r, int g, int b)
        => new(System.Windows.Media.Color.FromRgb((byte)r, (byte)g, (byte)b));

    private void InitAutoRefreshCombo()
    {
        foreach (ComboBoxItem item in SettingsAutoRefreshComboBox.Items)
        {
            if (item.Tag is string s && s == ViewModel.AutoRefreshMinutes.ToString())
            {
                SettingsAutoRefreshComboBox.SelectedItem = item;
                return;
            }
        }
        SettingsAutoRefreshComboBox.SelectedIndex = 0; // Off
    }

    private void OnAutoRefreshChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingsAutoRefreshComboBox.SelectedItem is ComboBoxItem item &&
            item.Tag is string s && int.TryParse(s, out int val))
        {
            ViewModel.AutoRefreshMinutes = val;
        }
    }

    private void OnApplyHotkey(object sender, RoutedEventArgs e)
        => ViewModel.ApplyHotkeyCommand.Execute(null);

    private void OnApplyCaptureHotkey(object sender, RoutedEventArgs e)
        => ViewModel.ApplyCaptureHotkeyCommand.Execute(null);

    private void OnApplyCommandPaletteHotkey(object sender, RoutedEventArgs e)
        => ViewModel.ApplyCommandPaletteHotkeyCommand.Execute(null);

    private void OnLlmApiKeyChanged(object sender, RoutedEventArgs e)
    {
        // PasswordBox → ViewModel に手動同期
        ViewModel.LlmApiKey = LlmApiKeyBox.Password;
    }

    private void OnAsanaTokenChanged(object sender, RoutedEventArgs e)
    {
        // PasswordBox → ViewModel に手動同期
        ViewModel.AsanaToken = AsanaTokenBox.Password;
    }

}
