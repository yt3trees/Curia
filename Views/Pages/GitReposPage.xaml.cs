using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Controls;
using Curia.ViewModels;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfCursors = System.Windows.Input.Cursors;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using WpfUserControl = System.Windows.Controls.UserControl;

namespace Curia.Views.Pages;

public partial class GitReposPage : WpfUserControl, INavigableView<GitReposViewModel>
{
    public GitReposViewModel ViewModel { get; }

    public GitReposPage(GitReposViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = ViewModel;
        InitializeComponent();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.EnsureInitializedAsync();
    }

    private void OnShowGitLog(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement btn || btn.Tag is not GitRepoItem repo) return;
        ShowGitLogDialog(repo);
    }

    private void ShowGitLogDialog(GitRepoItem repo)
    {
        var res = Application.Current.Resources;
        var surface0 = (WpfBrush)res["AppSurface0"];
        var surface1 = (WpfBrush)res["AppSurface1"];
        var surface2 = (WpfBrush)res["AppSurface2"];
        var text     = (WpfBrush)res["AppText"];
        var subtext  = (WpfBrush)res["AppSubtext0"];
        var accent   = (WpfBrush)res["AppAccentColor"];

        // --- title bar ---
        var titleBar = new Grid { Background = surface1, Height = 38 };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleIcon = new WpfTextBlock
        {
            Text = "◎",
            Foreground = accent,
            FontSize = 11,
            Margin = new Thickness(12, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleIcon, 0);

        var titleText = new WpfTextBlock
        {
            Text = $"Git Log — {repo.RepoName} [{repo.Branch}]",
            Foreground = text,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleText, 1);

        Window? dialog = null;
        var closeButton = new System.Windows.Controls.Button
        {
            Content = "✕",
            Width = 34,
            Height = 26,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = WpfBrushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = subtext,
            Cursor = WpfCursors.Hand
        };
        closeButton.Click += (_, _) => dialog?.Close();
        Grid.SetColumn(closeButton, 2);

        titleBar.Children.Add(titleIcon);
        titleBar.Children.Add(titleText);
        titleBar.Children.Add(closeButton);

        // --- content ---
        var logBox = new System.Windows.Controls.TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = surface0,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(0),
            FontFamily = new WpfFontFamily("Consolas"),
            FontSize = 12,
            Padding = new Thickness(8),
            MinHeight = 300,
            MaxHeight = 500,
            Text = "Loading..."
        };

        var content = new Grid { Margin = new Thickness(12) };
        content.Children.Add(logBox);

        // --- footer ---
        var footer = new Grid
        {
            Background = surface1,
            Height = 44
        };
        var closeBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Close",
            Margin = new Thickness(0, 0, 12, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        closeBtn.Click += (_, _) => dialog?.Close();
        footer.Children.Add(closeBtn);

        // --- root ---
        var root = new Grid { Background = surface0 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(content, 1);
        Grid.SetRow(footer, 2);
        root.Children.Add(titleBar);
        root.Children.Add(content);
        root.Children.Add(footer);

        // ウィンドウ境界線
        var border = new Border
        {
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false
        };
        var borderStyle = new Style(typeof(Border));
        var borderTrigger = new DataTrigger
        {
            Binding = new System.Windows.Data.Binding("WindowState")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Window), 1)
            },
            Value = WindowState.Maximized
        };
        borderTrigger.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(0)));
        borderStyle.Triggers.Add(borderTrigger);
        border.Style = borderStyle;

        Grid.SetRow(border, 0);
        Grid.SetRowSpan(border, 3);
        root.Children.Add(border);

        dialog = new Window
        {
            Owner = Window.GetWindow(this),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.CanResize,
            Width = 740,
            Height = 440,
            MinWidth = 500,
            MinHeight = 300,
            Content = root,
            Background = surface0,
            Foreground = text,
            ShowInTaskbar = false
        };
        System.Windows.Shell.WindowChrome.SetWindowChrome(dialog,
            new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(4),
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false
            });
        titleBar.MouseLeftButtonDown += (_, _) => dialog.DragMove();

        dialog.Loaded += (_, _) =>
        {
            var log = ViewModel.GetGitLog(repo);
            logBox.Text = log;
        };

        dialog.ShowDialog();
    }
}
