using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.IO;
using System.Text.RegularExpressions;
using Wpf.Ui.Controls;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;
using Win32OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Win32SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using Curia.Models;
using Curia.Services;
using Curia.ViewModels;
using WpfUserControl = System.Windows.Controls.UserControl;

namespace Curia.Views.Pages;

public partial class AgentHubPage : WpfUserControl, INavigableView<AgentHubViewModel>
{
    private sealed record FrontmatterFieldDoc(string Field, string Type, string Required, string Description);

    public AgentHubViewModel ViewModel { get; }
    private readonly LlmClientService _llmClientService;

    private bool _isInitialized;
    private System.Windows.Media.Brush? _tabActiveBrush;
    private System.Windows.Media.Brush? _tabInactiveBrush;

    public AgentHubPage(AgentHubViewModel viewModel, LlmClientService llmClientService)
    {
        ViewModel = viewModel;
        _llmClientService = llmClientService;
        DataContext = ViewModel;
        InitializeComponent();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized)
        {
            _isInitialized = true;
            var appRes = Application.Current.Resources;
            _tabActiveBrush = appRes.Contains("AppSurface2") ? (System.Windows.Media.Brush)appRes["AppSurface2"] : System.Windows.Media.Brushes.Gray;
            _tabInactiveBrush = System.Windows.Media.Brushes.Transparent;
            UpdateTabButtonStyles();
        }

        try
        {
            await ViewModel.EnsureInitializedAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AgentHubPage] Initialization failed: {ex}");
        }
    }

    // ─── Tab switching ────────────────────────────────────────────────────

    private void OnAgentsTabClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedLibraryTab = Curia.ViewModels.LibraryTab.Agents;
        UpdateTabButtonStyles();
    }

    private void OnRulesTabClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedLibraryTab = Curia.ViewModels.LibraryTab.Rules;
        UpdateTabButtonStyles();
    }

    private void OnSkillsTabClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedLibraryTab = Curia.ViewModels.LibraryTab.Skills;
        UpdateTabButtonStyles();
    }

    private void UpdateTabButtonStyles()
    {
        if (_tabActiveBrush == null) return;
        var active = _tabActiveBrush;
        var inactive = _tabInactiveBrush ?? System.Windows.Media.Brushes.Transparent;

        AgentsTabButton.Background = ViewModel.IsAgentsTabSelected ? active : inactive;
        RulesTabButton.Background = ViewModel.IsRulesTabSelected ? active : inactive;
        SkillsTabButton.Background = ViewModel.IsSkillsTabSelected ? active : inactive;

        var appRes = Application.Current.Resources;
        var textBrush = appRes.Contains("AppText") ? (System.Windows.Media.Brush)appRes["AppText"] : System.Windows.Media.Brushes.White;
        var subtextBrush = appRes.Contains("AppSubtext0") ? (System.Windows.Media.Brush)appRes["AppSubtext0"] : System.Windows.Media.Brushes.Gray;

        AgentsTabButton.Foreground = ViewModel.IsAgentsTabSelected ? textBrush : subtextBrush;
        RulesTabButton.Foreground = ViewModel.IsRulesTabSelected ? textBrush : subtextBrush;
        SkillsTabButton.Foreground = ViewModel.IsSkillsTabSelected ? textBrush : subtextBrush;
    }

    private void OnProjectScopeRadioClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedScopeType = DeploymentScopeType.Project;
    }

    private void OnGlobalScopeRadioClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedScopeType = DeploymentScopeType.Global;
    }

    private void OnFolderScopeRadioClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedScopeType = DeploymentScopeType.Folder;
    }

    private void OnBrowseFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select folder to deploy agents/skills into",
            Multiselect = false
        };
        if (!string.IsNullOrWhiteSpace(ViewModel.FolderPath) && Directory.Exists(ViewModel.FolderPath))
            dialog.InitialDirectory = ViewModel.FolderPath;
        if (dialog.ShowDialog() != true) return;
        ViewModel.FolderPath = dialog.FolderName;
    }

    // ─── Library list selection ───────────────────────────────────────────

    private void OnAgentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel.SelectedAgentDefinition != null)
        {
            RuleListBox.UnselectAll();
            SkillListBox.UnselectAll();
        }
    }

    private void OnRuleSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel.SelectedRuleDefinition != null)
        {
            AgentListBox.UnselectAll();
            SkillListBox.UnselectAll();
        }
    }

    // ─── Library list selection ───────────────────────────────────────────

    private void OnSkillSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel.SelectedSkillDefinition != null)
        {
            AgentListBox.UnselectAll();
            RuleListBox.UnselectAll();
        }
    }

    private void OnSkillListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src &&
            ItemsControl.ContainerFromElement(SkillListBox, src) is ListBoxItem)
            OnEditSkillClick(sender, e);
    }

    // ─── + New unified button ─────────────────────────────────────────────

    private void OnNewItemClick(object sender, RoutedEventArgs e)
    {
        switch (ViewModel.SelectedLibraryTab)
        {
            case Curia.ViewModels.LibraryTab.Agents:
                OnNewAgentClick(sender, e);
                break;
            case Curia.ViewModels.LibraryTab.Rules:
                OnNewRuleClick(sender, e);
                break;
            case Curia.ViewModels.LibraryTab.Skills:
                OnNewSkillClick(sender, e);
                break;
        }
    }

    // ─── Agent CRUD buttons ───────────────────────────────────────────────

    private async void OnNewAgentClick(object sender, RoutedEventArgs e)
    {
        var result = await ShowItemDialogAsync("New Agent", "", "", "", "", "", "", "", true);
        if (result == null) return;
        ViewModel.SaveAgent(
            null,
            result.Name,
            result.Description,
            result.Content,
            result.FrontmatterClaude,
            result.FrontmatterCodex,
            result.FrontmatterCopilot,
            result.FrontmatterGemini);
    }

    private async void OnEditAgentClick(object sender, RoutedEventArgs e)
    {
        var def = ViewModel.SelectedAgentDefinition;
        if (def == null) return;

        var (body, fmExtra) = ViewModel.GetAgentContentForEdit(def);
        var result = await ShowItemDialogAsync(
            "Edit Agent",
            def.Name,
            def.Description,
            body,
            fmExtra,
            def.FrontmatterCodex,
            def.FrontmatterCopilot,
            def.FrontmatterGemini,
            true);
        if (result == null) return;
        ViewModel.SaveAgent(
            def.Id,
            result.Name,
            result.Description,
            result.Content,
            result.FrontmatterClaude,
            result.FrontmatterCodex,
            result.FrontmatterCopilot,
            result.FrontmatterGemini);
    }

    private void OnAgentListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src &&
            ItemsControl.ContainerFromElement(AgentListBox, src) is ListBoxItem)
            OnEditAgentClick(sender, e);
    }

    private void OnRuleListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src &&
            ItemsControl.ContainerFromElement(RuleListBox, src) is ListBoxItem)
            OnEditRuleClick(sender, e);
    }

    private void OnEditSelectedClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedAgentDefinition != null)
        {
            OnEditAgentClick(sender, e);
            return;
        }

        if (ViewModel.SelectedRuleDefinition != null)
        {
            OnEditRuleClick(sender, e);
            return;
        }

        if (ViewModel.SelectedSkillDefinition != null)
        {
            OnEditSkillClick(sender, e);
        }
    }

    private void OnDeleteAgentClick(object sender, RoutedEventArgs e)
    {
        var def = ViewModel.SelectedAgentDefinition;
        if (def == null) return;

        if (ShowConfirmDialog($"Delete agent '{def.Name}'?", "This cannot be undone."))
            ViewModel.DeleteAgent(def.Id);
    }

    // ─── Rule CRUD buttons ────────────────────────────────────────────────

    private async void OnNewRuleClick(object sender, RoutedEventArgs e)
    {
        var result = await ShowItemDialogAsync("New Context Rule", "", "", "", "", "", "", "", false);
        if (result == null) return;
        ViewModel.SaveRule(null, result.Name, result.Description, result.Content);
    }

    private void OnDeleteSelectedClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedAgentDefinition != null)
        {
            OnDeleteAgentClick(sender, e);
            return;
        }

        if (ViewModel.SelectedRuleDefinition != null)
        {
            OnDeleteRuleClick(sender, e);
            return;
        }

        if (ViewModel.SelectedSkillDefinition != null)
        {
            OnDeleteSkillClick(sender, e);
        }
    }

    private async void OnEditRuleClick(object sender, RoutedEventArgs e)
    {
        var def = ViewModel.SelectedRuleDefinition;
        if (def == null) return;

        var content = ViewModel.SelectedPreviewContent;
        var result = await ShowItemDialogAsync("Edit Context Rule", def.Name, def.Description, content, "", "", "", "", false);
        if (result == null) return;
        ViewModel.SaveRule(def.Id, result.Name, result.Description, result.Content);
    }

    private void OnDeleteRuleClick(object sender, RoutedEventArgs e)
    {
        var def = ViewModel.SelectedRuleDefinition;
        if (def == null) return;

        if (ShowConfirmDialog($"Delete rule '{def.Name}'?", "This cannot be undone."))
            ViewModel.DeleteRule(def.Id);
    }

    // ─── Skill CRUD buttons ───────────────────────────────────────────────

    private async void OnNewSkillClick(object sender, RoutedEventArgs e)
    {
        var result = await ShowSkillDialogAsync("New Skill", "", "", "");
        if (result == null) return;
        ViewModel.SaveSkill(null, result.Value.Name, result.Value.Description, result.Value.Content);
    }

    private async void OnEditSkillClick(object sender, RoutedEventArgs e)
    {
        var def = ViewModel.SelectedSkillDefinition;
        if (def == null) return;

        var isReadOnly = def.IsBuiltIn;
        var content = ViewModel.GetSkillContentForEdit(def);
        var result = await ShowSkillDialogAsync(
            isReadOnly ? "View Built-in Skill" : "Edit Skill",
            def.Name,
            def.Description,
            content,
            def.ContentDirectory,
            isReadOnly);
        if (result == null) return;
        if (isReadOnly) return;
        ViewModel.SaveSkill(def.Id, result.Value.Name, result.Value.Description, result.Value.Content);
    }

    private void OnDeleteSkillClick(object sender, RoutedEventArgs e)
    {
        var def = ViewModel.SelectedSkillDefinition;
        if (def == null || def.IsBuiltIn) return;

        if (ShowConfirmDialog($"Delete skill '{def.Name}'?", "This cannot be undone."))
            ViewModel.DeleteSkill(def.Id);
    }

    // ─── Skill dialog ─────────────────────────────────────────────────────

    private sealed record ParsedSkillMarkdown(
        string Name,
        string Description,
        List<string> ParameterLines,
        string Body);

    private record struct SkillDialogResult(string Name, string Description, string Content);

    private Task<SkillDialogResult?> ShowSkillDialogAsync(
        string title,
        string name,
        string description,
        string content,
        string? skillFolderPath = null,
        bool isReadOnly = false)
    {
        var tcs = new TaskCompletionSource<SkillDialogResult?>();
        var parsed = ParseSkillMarkdown(content);
        var effectiveName = string.IsNullOrWhiteSpace(parsed.Name) ? name : parsed.Name;
        var effectiveDescription = string.IsNullOrWhiteSpace(parsed.Description) ? description : parsed.Description;
        var effectiveParameters = string.Join(Environment.NewLine, parsed.ParameterLines);

        var appRes = Application.Current.Resources;
        var surface = (MediaBrush)appRes["AppSurface0"];
        var surface1 = (MediaBrush)appRes["AppSurface1"];
        var surface2 = (MediaBrush)appRes["AppSurface2"];
        var text = (MediaBrush)appRes["AppText"];
        var subtext = (MediaBrush)appRes["AppSubtext0"];
        var accent = appRes.Contains("AppBlue") ? (MediaBrush)appRes["AppBlue"] : text;

        var titleBar = BuildTitleBar(title, surface1, text, subtext, accent, out var closeBtn);

        var nameLabel = new System.Windows.Controls.TextBlock { Text = "Name", Foreground = subtext, FontSize = 11, Margin = new Thickness(0, 0, 0, 3) };
        var nameBox = new System.Windows.Controls.TextBox
        {
            Text = effectiveName,
            Background = surface1,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            FontSize = 12,
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 0, 0, 10),
            IsReadOnly = isReadOnly
        };

        var descLabel = new System.Windows.Controls.TextBlock { Text = "Description", Foreground = subtext, FontSize = 11, Margin = new Thickness(0, 0, 0, 3) };
        var descBox = new System.Windows.Controls.TextBox
        {
            Text = effectiveDescription,
            Background = surface1,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            FontSize = 12,
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 0, 0, 10),
            IsReadOnly = isReadOnly
        };

        var parametersLabel = new System.Windows.Controls.TextBlock { Text = "Other Parameters", Foreground = subtext, FontSize = 11, Margin = new Thickness(0, 0, 0, 3) };
        var parametersBox = new System.Windows.Controls.TextBox
        {
            Text = effectiveParameters,
            Background = surface1,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            FontSize = 12,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.NoWrap,
            AcceptsReturn = true,
            Height = 44,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            IsReadOnly = isReadOnly
        };

        var contentLabel = new System.Windows.Controls.TextBlock { Text = "SKILL.md content (body only)", Foreground = subtext, FontSize = 11, Margin = new Thickness(0, 0, 0, 3) };
        var contentBox = new System.Windows.Controls.TextBox
        {
            Text = parsed.Body.TrimStart('\r', '\n'),
            Background = surface1,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            FontSize = 12,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Padding = new Thickness(6, 4, 6, 4),
            MinHeight = 200,
            MaxHeight = 320,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            IsReadOnly = isReadOnly
        };

        var contentPanel = new StackPanel { Margin = new Thickness(16, 12, 16, 8) };
        contentPanel.Children.Add(nameLabel);
        contentPanel.Children.Add(nameBox);
        contentPanel.Children.Add(descLabel);
        contentPanel.Children.Add(descBox);
        contentPanel.Children.Add(parametersLabel);
        contentPanel.Children.Add(parametersBox);
        contentPanel.Children.Add(contentLabel);
        contentPanel.Children.Add(contentBox);

        var openFolderBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Open Folder",
            Appearance = ControlAppearance.Secondary,
            Height = 28,
            Padding = new Thickness(10, 4, 10, 4),
            FontSize = 11,
            Margin = new Thickness(16, 0, 0, 10),
            IsEnabled = !string.IsNullOrEmpty(skillFolderPath) && Directory.Exists(skillFolderPath),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        openFolderBtn.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(skillFolderPath) && Directory.Exists(skillFolderPath))
                Process.Start("explorer.exe", skillFolderPath);
        };

        var saveBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Save",
            Appearance = ControlAppearance.Primary,
            MinWidth = 90,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 0),
            Visibility = isReadOnly ? Visibility.Collapsed : Visibility.Visible
        };
        var cancelBtn = new Wpf.Ui.Controls.Button
        {
            Content = isReadOnly ? "Close" : "Cancel",
            Appearance = ControlAppearance.Secondary,
            MinWidth = 80,
            Height = 32,
            IsCancel = true
        };
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 12)
        };
        footer.Children.Add(saveBtn);
        footer.Children.Add(cancelBtn);

        var root = new Grid { Background = surface };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(contentPanel, 1);
        Grid.SetRow(openFolderBtn, 2);
        Grid.SetRow(footer, 3);
        root.Children.Add(titleBar);
        root.Children.Add(contentPanel);
        root.Children.Add(openFolderBtn);
        root.Children.Add(footer);

        var owner = Window.GetWindow(this);
        var dialog = new Window
        {
            Title = "",
            Owner = owner,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize,
            WindowStyle = WindowStyle.None,
            Width = 560,
            MinHeight = 400,
            Background = surface,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            SizeToContent = SizeToContent.Height,
            Content = root
        };

        System.Windows.Shell.WindowChrome.SetWindowChrome(dialog,
            new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(4),
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false
            });

        titleBar.MouseLeftButtonDown += (_, ev) =>
        {
            if (ev.LeftButton == MouseButtonState.Pressed) dialog.DragMove();
        };

        SkillDialogResult? dialogResult = null;

        saveBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text)) { nameBox.Focus(); return; }
            var skillMarkdown = BuildSkillMarkdown(
                nameBox.Text.Trim(),
                descBox.Text.Trim(),
                parametersBox.Text,
                contentBox.Text,
                parsed.ParameterLines);
            dialogResult = new SkillDialogResult(nameBox.Text.Trim(), descBox.Text.Trim(), skillMarkdown);
            dialog.Close();
        };
        if (!isReadOnly)
        {
            dialog.PreviewKeyDown += (_, ev) =>
            {
                if (ev.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                    saveBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            };
        }
        closeBtn.Click += (_, _) => dialog.Close();
        cancelBtn.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => tcs.TrySetResult(dialogResult);

        dialog.ShowDialog();
        return tcs.Task;
    }

    private static ParsedSkillMarkdown ParseSkillMarkdown(string rawContent)
    {
        if (string.IsNullOrEmpty(rawContent))
            return new ParsedSkillMarkdown("", "", [], "");

        var match = Regex.Match(
            rawContent,
            @"\A---\r?\n(?<fm>[\s\S]*?)\r?\n---\r?\n?",
            RegexOptions.CultureInvariant);

        if (!match.Success)
            return new ParsedSkillMarkdown("", "", [], rawContent);

        var frontmatter = match.Groups["fm"].Value;
        var body = rawContent[match.Length..];
        var name = "";
        var description = "";
        var parameters = new List<string>();

        foreach (var line in frontmatter.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            var idx = trimmed.IndexOf(':');
            if (idx <= 0)
            {
                parameters.Add(trimmed);
                continue;
            }

            var key = trimmed[..idx].Trim();
            var value = trimmed[(idx + 1)..].Trim();
            value = UnquoteYaml(value);

            if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
                name = value;
            else if (key.Equals("description", StringComparison.OrdinalIgnoreCase))
                description = value;
            else
                parameters.Add(trimmed);
        }

        return new ParsedSkillMarkdown(name, description, parameters, body);
    }

    private static string BuildSkillMarkdown(
        string name,
        string description,
        string parameterText,
        string body,
        List<string> _)
    {
        var lines = new List<string>
        {
            $"name: {QuoteYaml(name)}",
            $"description: {QuoteYaml(description)}"
        };

        foreach (var rawLine in parameterText.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed == "---")
                continue;

            var idx = trimmed.IndexOf(':');
            if (idx > 0)
            {
                var key = trimmed[..idx].Trim();
                if (key.Equals("name", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("description", StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            lines.Add(trimmed);
        }

        var normalizedBody = body.TrimStart('\r', '\n');
        return $"---\n{string.Join("\n", lines)}\n---\n\n{normalizedBody}";
    }

    private static string UnquoteYaml(string value)
    {
        if (value.Length >= 2 &&
            ((value.StartsWith('"') && value.EndsWith('"')) ||
             (value.StartsWith('\'') && value.EndsWith('\''))))
        {
            return value[1..^1];
        }
        return value;
    }

    private static string QuoteYaml(string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }

    // ─── Other buttons ────────────────────────────────────────────────────


    private void OnExportLibraryZipClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Win32SaveFileDialog
        {
            Filter = "ZIP (*.zip)|*.zip",
            FileName = $"agent_hub_library_{DateTime.Now:yyyyMMdd_HHmm}.zip",
            AddExtension = true,
            DefaultExt = ".zip"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            ViewModel.ExportLibraryZip(dialog.FileName);
            ViewModel.StatusMessage = $"{ViewModel.ScopeStatusLabel} / Exported library ZIP: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"Export error: {ex.Message}";
        }
    }

    private void OnImportFromDirectoryClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select directory containing agents and/or skills",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var result = ViewModel.ImportFromDirectory(dialog.FolderName);
            ViewModel.StatusMessage = result.Errors.Count > 0
                ? $"Import: {result.ToStatusMessage()} — {string.Join("; ", result.Errors)}"
                : $"Import from directory: {result.ToStatusMessage()}";
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"Import directory error: {ex.Message}";
        }
    }

    private void OnImportLibraryZipClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Win32OpenFileDialog
        {
            Filter = "ZIP (*.zip)|*.zip|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            ViewModel.ImportLibraryZip(dialog.FileName);
            ViewModel.StatusMessage = $"{ViewModel.ScopeStatusLabel} / Imported library ZIP: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"Import ZIP error: {ex.Message}";
        }
    }

    private async void OnAiBuilderClick(object sender, RoutedEventArgs e)
    {
        var ai = await ShowAiBuilderInputDialogAsync();
        if (ai == null) return;

        try
        {
            var dialogTitle = ai.Value.IsAgent ? "AI Builder - Review & Save (Agent)" : "AI Builder - Review & Save (Rule)";
            var result = await ShowItemDialogAsync(dialogTitle, ai.Value.Name, ai.Value.Description, ai.Value.Content, "", "", "", "", ai.Value.IsAgent);
            if (result == null) { ViewModel.StatusMessage = "AI Builder cancelled"; return; }
            if (ai.Value.IsAgent)
            {
                ViewModel.SaveAgent(null, result.Name, result.Description, result.Content,
                    result.FrontmatterClaude, result.FrontmatterCodex, result.FrontmatterCopilot, result.FrontmatterGemini);
            }
            else
            {
                ViewModel.SaveRule(null, result.Name, result.Description, result.Content);
            }
            ViewModel.StatusMessage = $"AI Builder: saved '{result.Name}'";
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"AI Builder error: {ex.Message}";
        }
    }

    // ─── Dialog helpers ───────────────────────────────────────────────────

    private record ItemDialogResult(
        string Name,
        string Description,
        string Content,
        string FrontmatterClaude,
        string FrontmatterCodex,
        string FrontmatterCopilot,
        string FrontmatterGemini);

    private Task<ItemDialogResult?> ShowItemDialogAsync(
        string title,
        string name,
        string description,
        string content,
        string frontmatterClaude,
        string frontmatterCodex,
        string frontmatterCopilot,
        string frontmatterGemini,
        bool isAgentDialog)
    {
        var tcs = new TaskCompletionSource<ItemDialogResult?>();

        var appRes = Application.Current.Resources;
        var surface = (MediaBrush)appRes["AppSurface0"];
        var surface1 = (MediaBrush)appRes["AppSurface1"];
        var surface2 = (MediaBrush)appRes["AppSurface2"];
        var text = (MediaBrush)appRes["AppText"];
        var subtext = (MediaBrush)appRes["AppSubtext0"];
        var accentBrush = appRes.Contains("AppBlue") ? (MediaBrush)appRes["AppBlue"] : text;

        // Title bar
        var titleBar = BuildTitleBar(title, surface1, text, subtext, accentBrush, out var closeBtn);

        // Name field
        var nameLabel = new System.Windows.Controls.TextBlock { Text = "Name", Foreground = subtext, FontSize = 11, Margin = new Thickness(0, 0, 0, 3) };
        var nameBox = new System.Windows.Controls.TextBox
        {
            Text = name,
            Background = surface1,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            FontSize = 12,
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 0, 0, 10)
        };

        // Description field
        var descLabel = new System.Windows.Controls.TextBlock { Text = "Description", Foreground = subtext, FontSize = 11, Margin = new Thickness(0, 0, 0, 3) };
        var descBox = new System.Windows.Controls.TextBox
        {
            Text = description,
            Background = surface1,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            FontSize = 12,
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var frontmatterHeaderPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 3),
            Visibility = isAgentDialog ? Visibility.Visible : Visibility.Collapsed
        };
        var frontmatterLabel = new System.Windows.Controls.TextBlock
        {
            Text = "Agent Config (Frontmatter/TOML, optional)",
            Foreground = subtext,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        var frontmatterHelpButton = new System.Windows.Controls.Button
        {
            Width = 20,
            Height = 20,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(0),
            Background = surface1,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Show frontmatter field reference"
        };
        frontmatterHelpButton.Content = new System.Windows.Controls.TextBlock
        {
            Text = "?",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            LineHeight = 12,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        frontmatterHelpButton.Click += (_, _) => ShowFrontmatterHelpDialog();
        frontmatterHeaderPanel.Children.Add(frontmatterLabel);
        frontmatterHeaderPanel.Children.Add(frontmatterHelpButton);
        var frontmatterBox = new System.Windows.Controls.TextBox
        {
            Text = frontmatterClaude,
            Background = surface1,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            FontSize = 12,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 0, 0, 10),
            Height = 96,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Visibility = isAgentDialog ? Visibility.Visible : Visibility.Collapsed
        };
        var frontmatterPlaceholderMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Claude"] = "tools: Read, Glob, Grep\nmodel: sonnet\npermissionMode: default\nmaxTurns: 30",
            ["Codex"] = "model = \"gpt-5.4-mini\"\nmodel_reasoning_effort = \"medium\"\nsandbox_mode = \"read-only\"\nnickname_candidates = [\"Mapper\", \"Scout\"]",
            ["Copilot"] = "tools:\n  - read_file\n  - find_text\nmodel: gpt-5.4-mini",
            ["Gemini"] = "tools:\n  - \"*\"\nmodel: gemini-3-preview\ntemperature: 0.2"
        };
        var frontmatterPlaceholder = new System.Windows.Controls.TextBlock
        {
            Text = frontmatterPlaceholderMap["Claude"],
            Foreground = subtext,
            Opacity = 0.8,
            Margin = new Thickness(10, 8, 10, 8),
            TextWrapping = TextWrapping.Wrap,
            IsHitTestVisible = false,
            Visibility = string.IsNullOrWhiteSpace(frontmatterBox.Text) ? Visibility.Visible : Visibility.Collapsed
        };
        var frontmatterBoxHost = new Grid
        {
            Margin = new Thickness(0, 0, 0, 10),
            Height = 96,
            Visibility = isAgentDialog ? Visibility.Visible : Visibility.Collapsed
        };
        frontmatterBox.Margin = new Thickness(0);
        frontmatterBoxHost.Children.Add(frontmatterBox);
        frontmatterBoxHost.Children.Add(frontmatterPlaceholder);

        var frontmatterCliLabel = new System.Windows.Controls.TextBlock
        {
            Text = "Frontmatter target",
            Foreground = subtext,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 3),
            Visibility = isAgentDialog ? Visibility.Visible : Visibility.Collapsed
        };
        var frontmatterCliCombo = new System.Windows.Controls.ComboBox
        {
            Background = surface1,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            FontSize = 12,
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 0, 0, 8),
            Visibility = isAgentDialog ? Visibility.Visible : Visibility.Collapsed
        };
        frontmatterCliCombo.Items.Add("Claude");
        frontmatterCliCombo.Items.Add("Codex");
        frontmatterCliCombo.Items.Add("Copilot");
        frontmatterCliCombo.Items.Add("Gemini");
        frontmatterCliCombo.SelectedIndex = 0;

        var frontmatterMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Claude"] = frontmatterClaude ?? "",
            ["Codex"] = frontmatterCodex ?? "",
            ["Copilot"] = frontmatterCopilot ?? "",
            ["Gemini"] = frontmatterGemini ?? ""
        };
        frontmatterBox.Text = frontmatterMap["Claude"];

        void UpdateFrontmatterPlaceholder()
        {
            var key = (frontmatterCliCombo.SelectedItem as string) ?? "Claude";
            if (!frontmatterPlaceholderMap.TryGetValue(key, out var placeholder))
                placeholder = "";
            frontmatterPlaceholder.Text = placeholder;
            frontmatterPlaceholder.Visibility = string.IsNullOrWhiteSpace(frontmatterBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        frontmatterCliCombo.SelectionChanged += (_, _) =>
        {
            var key = (frontmatterCliCombo.SelectedItem as string) ?? "Claude";
            if (!frontmatterMap.TryGetValue(key, out var value))
                value = "";
            frontmatterBox.Text = value;
            UpdateFrontmatterPlaceholder();
        };
        frontmatterBox.TextChanged += (_, _) =>
        {
            var key = (frontmatterCliCombo.SelectedItem as string) ?? "Claude";
            frontmatterMap[key] = frontmatterBox.Text;
            UpdateFrontmatterPlaceholder();
        };
        UpdateFrontmatterPlaceholder();

        // Content area
        var contentLabel = new System.Windows.Controls.TextBlock { Text = "Content (Markdown)", Foreground = subtext, FontSize = 11, Margin = new Thickness(0, 0, 0, 3) };
        var contentBox = new System.Windows.Controls.TextBox
        {
            Text = content,
            Background = surface1,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            FontSize = 12,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Padding = new Thickness(6, 4, 6, 4),
            MinHeight = 200,
            MaxHeight = 320,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        var contentPanel = new StackPanel
        {
            Margin = new Thickness(16, 12, 16, 8)
        };
        contentPanel.Children.Add(nameLabel);
        contentPanel.Children.Add(nameBox);
        contentPanel.Children.Add(descLabel);
        contentPanel.Children.Add(descBox);
        contentPanel.Children.Add(frontmatterHeaderPanel);
        contentPanel.Children.Add(frontmatterCliLabel);
        contentPanel.Children.Add(frontmatterCliCombo);
        contentPanel.Children.Add(frontmatterBoxHost);
        contentPanel.Children.Add(contentLabel);
        contentPanel.Children.Add(contentBox);

        // Footer
        var saveBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Save",
            Appearance = ControlAppearance.Primary,
            MinWidth = 90,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var cancelBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Cancel",
            Appearance = ControlAppearance.Secondary,
            MinWidth = 80,
            Height = 32,
            IsCancel = true
        };
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 12)
        };
        footer.Children.Add(saveBtn);
        footer.Children.Add(cancelBtn);

        var root = new Grid { Background = surface };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(contentPanel, 1);
        Grid.SetRow(footer, 2);
        root.Children.Add(titleBar);
        root.Children.Add(contentPanel);
        root.Children.Add(footer);

        var owner = Window.GetWindow(this);
        var dialog = new Window
        {
            Title = "",
            Owner = owner,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize,
            WindowStyle = WindowStyle.None,
            Width = 560,
            MinHeight = 400,
            Background = surface,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            SizeToContent = SizeToContent.Height,
            Content = root
        };

        System.Windows.Shell.WindowChrome.SetWindowChrome(dialog,
            new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(4),
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false
            });

        titleBar.MouseLeftButtonDown += (_, ev) =>
        {
            if (ev.LeftButton == MouseButtonState.Pressed)
                dialog.DragMove();
        };

        ItemDialogResult? dialogResult = null;

        saveBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text)) { nameBox.Focus(); return; }
            dialogResult = new ItemDialogResult(
                nameBox.Text.Trim(),
                descBox.Text.Trim(),
                contentBox.Text,
                frontmatterMap.GetValueOrDefault("Claude", ""),
                frontmatterMap.GetValueOrDefault("Codex", ""),
                frontmatterMap.GetValueOrDefault("Copilot", ""),
                frontmatterMap.GetValueOrDefault("Gemini", ""));
            dialog.Close();
        };
        dialog.PreviewKeyDown += (_, ev) =>
        {
            if (ev.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                saveBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        };
        closeBtn.Click += (_, _) => dialog.Close();
        cancelBtn.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => tcs.TrySetResult(dialogResult);

        dialog.ShowDialog();
        return tcs.Task;
    }

    private bool ShowConfirmDialog(string message, string subMessage)
    {
        var appRes = Application.Current.Resources;
        var surface = (MediaBrush)appRes["AppSurface0"];
        var surface1 = (MediaBrush)appRes["AppSurface1"];
        var surface2 = (MediaBrush)appRes["AppSurface2"];
        var text = (MediaBrush)appRes["AppText"];
        var subtext = (MediaBrush)appRes["AppSubtext0"];
        var danger = appRes.Contains("AppRed") ? (MediaBrush)appRes["AppRed"] : MediaBrushes.IndianRed;
        var accentBrush = danger;

        var titleBar = BuildTitleBar("Confirm", surface1, text, subtext, accentBrush, out var closeBtn);

        var msgText = new System.Windows.Controls.TextBlock
        {
            Text = message,
            Foreground = text,
            FontSize = 13,
            Margin = new Thickness(16, 14, 16, 4),
            TextWrapping = TextWrapping.Wrap
        };
        var subText = new System.Windows.Controls.TextBlock
        {
            Text = subMessage,
            Foreground = subtext,
            FontSize = 11,
            Margin = new Thickness(16, 0, 16, 14),
            TextWrapping = TextWrapping.Wrap
        };

        var deleteBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Delete",
            Appearance = ControlAppearance.Danger,
            MinWidth = 80,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var cancelBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Cancel",
            Appearance = ControlAppearance.Secondary,
            MinWidth = 80,
            Height = 32,
            IsCancel = true
        };
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 14)
        };
        footer.Children.Add(deleteBtn);
        footer.Children.Add(cancelBtn);

        var root = new Grid { Background = surface };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(msgText, 1);
        Grid.SetRow(subText, 2);
        Grid.SetRow(footer, 3);
        root.Children.Add(titleBar);
        root.Children.Add(msgText);
        root.Children.Add(subText);
        root.Children.Add(footer);

        var owner = Window.GetWindow(this);
        var dialog = new Window
        {
            Title = "",
            Owner = owner,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            Width = 360,
            Background = surface,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            SizeToContent = SizeToContent.Height,
            Content = root
        };

        System.Windows.Shell.WindowChrome.SetWindowChrome(dialog,
            new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(0),
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false
            });

        bool confirmed = false;
        titleBar.MouseLeftButtonDown += (_, ev) =>
        {
            if (ev.LeftButton == MouseButtonState.Pressed) dialog.DragMove();
        };
        deleteBtn.Click += (_, _) => { confirmed = true; dialog.Close(); };
        closeBtn.Click += (_, _) => dialog.Close();
        cancelBtn.Click += (_, _) => dialog.Close();

        dialog.ShowDialog();
        return confirmed;
    }

    private Task<(string Name, string Description, string Content, bool IsAgent)?> ShowAiBuilderInputDialogAsync()
    {
        var tcs = new TaskCompletionSource<(string Name, string Description, string Content, bool IsAgent)?>();

        var appRes = Application.Current.Resources;
        var surface = (MediaBrush)appRes["AppSurface0"];
        var surface1 = (MediaBrush)appRes["AppSurface1"];
        var surface2 = (MediaBrush)appRes["AppSurface2"];
        var text = (MediaBrush)appRes["AppText"];
        var subtext = (MediaBrush)appRes["AppSubtext0"];
        var accent = appRes.Contains("AppBlue") ? (MediaBrush)appRes["AppBlue"] : text;

        var titleBar = BuildTitleBar("AI Builder", surface1, text, subtext, accent, out var closeBtn);

        var agentRadio = new System.Windows.Controls.RadioButton
        {
            Content = "Agent",
            IsChecked = true,
            Foreground = text,
            FontSize = 12,
            Margin = new Thickness(0, 0, 20, 0)
        };
        var ruleRadio = new System.Windows.Controls.RadioButton
        {
            Content = "Context Rule",
            Foreground = text,
            FontSize = 12
        };
        var typePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 10)
        };
        typePanel.Children.Add(agentRadio);
        typePanel.Children.Add(ruleRadio);

        var label = new System.Windows.Controls.TextBlock
        {
            Text = "Describe the purpose (e.g. 'A strict C# code reviewer focused on SOLID principles')",
            Foreground = subtext,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        };
        var inputBox = new System.Windows.Controls.TextBox
        {
            Background = surface1,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            FontSize = 12,
            Padding = new Thickness(6, 4, 6, 4),
            Height = 80,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        var contentPanel = new StackPanel { Margin = new Thickness(16, 12, 16, 8) };
        contentPanel.Children.Add(typePanel);
        contentPanel.Children.Add(label);
        contentPanel.Children.Add(inputBox);

        var loadingPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(16, 0, 16, 8),
            Visibility = Visibility.Collapsed
        };
        loadingPanel.Children.Add(new Wpf.Ui.Controls.ProgressRing
        {
            IsIndeterminate = true,
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, 8, 0)
        });
        loadingPanel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Generating...",
            Foreground = subtext,
            FontSize = 11,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        });

        var errorText = new System.Windows.Controls.TextBlock
        {
            Foreground = MediaBrushes.IndianRed,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 0, 16, 8),
            Visibility = Visibility.Collapsed
        };

        var generateBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Generate",
            Appearance = ControlAppearance.Primary,
            MinWidth = 100,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var cancelBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Cancel",
            Appearance = ControlAppearance.Secondary,
            MinWidth = 80,
            Height = 32,
            IsCancel = true
        };
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 12)
        };
        footer.Children.Add(generateBtn);
        footer.Children.Add(cancelBtn);

        var root = new Grid { Background = surface };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(contentPanel, 1);
        Grid.SetRow(loadingPanel, 2);
        Grid.SetRow(errorText, 3);
        Grid.SetRow(footer, 4);
        root.Children.Add(titleBar);
        root.Children.Add(contentPanel);
        root.Children.Add(loadingPanel);
        root.Children.Add(errorText);
        root.Children.Add(footer);

        var owner = Window.GetWindow(this);
        var dialog = new Window
        {
            Title = "",
            Owner = owner,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            Width = 480,
            Background = surface,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            SizeToContent = SizeToContent.Height,
            Content = root
        };

        System.Windows.Shell.WindowChrome.SetWindowChrome(dialog,
            new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(0),
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false
            });

        (string Name, string Description, string Content, bool IsAgent)? result = null;
        titleBar.MouseLeftButtonDown += (_, ev) =>
        {
            if (ev.LeftButton == MouseButtonState.Pressed) dialog.DragMove();
        };
        inputBox.KeyDown += (_, ev) =>
        {
            if (ev.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                generateBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        };
        generateBtn.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(inputBox.Text)) return;

            var isAgent = agentRadio.IsChecked == true;
            inputBox.IsEnabled = false;
            agentRadio.IsEnabled = false;
            ruleRadio.IsEnabled = false;
            generateBtn.IsEnabled = false;
            cancelBtn.IsEnabled = false;
            errorText.Visibility = Visibility.Collapsed;
            loadingPanel.Visibility = Visibility.Visible;

            try
            {
                var systemPrompt = isAgent
                    ? "Generate a sub-agent definition based on the user's description.\n" +
                      "IMPORTANT: Do NOT include any folder paths, directory restrictions,\n" +
                      "or working directory references. Define only the role, skills, and best practices.\n\n" +
                      "Respond with a single JSON object (no markdown fences) with exactly these fields:\n" +
                      "- \"name\": concise slug in lowercase-with-hyphens (e.g. \"strict-csharp-reviewer\")\n" +
                      "- \"description\": one sentence describing when to use this agent\n" +
                      "- \"content\": the full Markdown agent definition (role, trigger conditions, skills, best practices)"
                    : "Generate a context rule definition (coding conventions, guidelines, or instructions for AI coding assistants).\n" +
                      "IMPORTANT: Do NOT include any folder paths or directory restrictions.\n\n" +
                      "Respond with a single JSON object (no markdown fences) with exactly these fields:\n" +
                      "- \"name\": concise slug in lowercase-with-hyphens (e.g. \"typescript-strict-conventions\")\n" +
                      "- \"description\": one sentence describing what this rule enforces\n" +
                      "- \"content\": the full Markdown rule content (conventions, rationale, examples)";
                var raw = await _llmClientService.ChatCompletionAsync(systemPrompt, inputBox.Text.Trim(), CancellationToken.None);

                var json = raw.Trim();
                if (json.StartsWith("```"))
                {
                    var firstNewline = json.IndexOf('\n');
                    var lastFence = json.LastIndexOf("```");
                    if (firstNewline >= 0 && lastFence > firstNewline)
                        json = json[(firstNewline + 1)..lastFence].Trim();
                }
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                result = (
                    root.GetProperty("name").GetString() ?? "",
                    root.GetProperty("description").GetString() ?? "",
                    root.GetProperty("content").GetString() ?? "",
                    isAgent
                );
                dialog.Close();
            }
            catch (Exception ex)
            {
                errorText.Text = ex.Message;
                errorText.Visibility = Visibility.Visible;
                loadingPanel.Visibility = Visibility.Collapsed;
                inputBox.IsEnabled = true;
                agentRadio.IsEnabled = true;
                ruleRadio.IsEnabled = true;
                generateBtn.IsEnabled = true;
                cancelBtn.IsEnabled = true;
            }
        };
        closeBtn.Click += (_, _) => dialog.Close();
        cancelBtn.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => tcs.TrySetResult(result);

        dialog.ShowDialog();
        return tcs.Task;
    }

    private void ShowFrontmatterHelpDialog()
    {
        var appRes = Application.Current.Resources;
        var surface = (MediaBrush)appRes["AppSurface0"];
        var surface1 = (MediaBrush)appRes["AppSurface1"];
        var surface2 = (MediaBrush)appRes["AppSurface2"];
        var text = (MediaBrush)appRes["AppText"];
        var subtext = (MediaBrush)appRes["AppSubtext0"];
        var accent = appRes.Contains("AppBlue") ? (MediaBrush)appRes["AppBlue"] : text;

        var claudeDocs = new List<FrontmatterFieldDoc>
        {
            new("name", "string", "Yes", "Unique identifier using lowercase letters and hyphens."),
            new("description", "string", "Yes", "Description used by Claude to decide delegation."),
            new("tools", "array/string", "No", "Tools the sub-agent can use. Inherits all tools if omitted."),
            new("disallowedTools", "array/string", "No", "Tools to block. Removed from inherited or explicit tool list."),
            new("model", "string", "No", "Model: sonnet, opus, haiku, full model ID, or inherit."),
            new("permissionMode", "string", "No", "Permission mode: default, acceptEdits, dontAsk, bypassPermissions, or plan."),
            new("maxTurns", "number", "No", "Maximum agent turns before the sub-agent stops."),
            new("skills", "array", "No", "Skills injected into context; parent skills are not inherited."),
            new("mcpServers", "array/object", "No", "Configured server names or inline server definitions."),
            new("hooks", "object", "No", "Lifecycle hooks scoped to this sub-agent."),
            new("memory", "string", "No", "Persistent memory scope: user, project, or local."),
            new("background", "boolean", "No", "Run this sub-agent as a background task if true."),
            new("effort", "string", "No", "Effort level: low, medium, high, max."),
            new("isolation", "string", "No", "Set worktree to run in an isolated temporary git worktree."),
            new("initialPrompt", "string", "No", "Auto-sent first turn when used as main session agent.")
        };
        var codexDocs = new List<FrontmatterFieldDoc>
        {
            new("name", "string", "Yes", "Agent name Codex uses when spawning or referring to this agent."),
            new("description", "string", "Yes", "Human-facing guidance for when Codex should use this agent."),
            new("developer_instructions", "string", "Yes", "Core instructions that define the agent behavior."),
            new("nickname_candidates", "string[]", "No", "Optional pool of display nicknames for spawned agents."),
            new("model", "string", "No", "Optional model override (e.g. gpt-5.4-mini)."),
            new("model_reasoning_effort", "string", "No", "Reasoning effort override (low/medium/high/xhigh)."),
            new("sandbox_mode", "string", "No", "Sandbox mode override (for example read-only).")
        };
        var geminiDocs = new List<FrontmatterFieldDoc>
        {
            new("name", "string", "Yes", "Unique identifier (slug) used as the tool name for the agent."),
            new("description", "string", "Yes", "Short description used by the main agent to decide invocation."),
            new("kind", "string", "No", "local (default) or remote."),
            new("tools", "array", "No", "Allowed tool names. Supports wildcards (*, mcp_*, mcp_server_*)."),
            new("model", "string", "No", "Specific model override (for example gemini-3-preview)."),
            new("temperature", "number", "No", "Model temperature (0.0 - 2.0)."),
            new("max_turns", "number", "No", "Maximum conversation turns for this agent."),
            new("timeout_mins", "number", "No", "Execution timeout in minutes.")
        };
        var copilotDocs = new List<FrontmatterFieldDoc>
        {
            new("name", "string", "No", "Display name for the custom agent."),
            new("description", "string", "Yes", "Description of purpose and capabilities."),
            new("target", "string", "No", "Target environment (vscode or github-copilot)."),
            new("tools", "list/string", "No", "Tool names. Comma-separated string or YAML array."),
            new("model", "string", "No", "Model override; inherits default when unset."),
            new("disable-model-invocation", "boolean", "No", "Disable automatic invocation by Copilot coding agent."),
            new("user-invocable", "boolean", "No", "Whether users can manually select this agent."),
            new("infer", "boolean", "No", "Retired; use disable-model-invocation and user-invocable."),
            new("mcp-servers", "object", "No", "Additional MCP servers/tools (not used in VS Code agent mode)."),
            new("metadata", "object", "No", "Arbitrary string key/value annotations.")
        };

        var titleBar = BuildTitleBar("Frontmatter Field Reference", surface1, text, subtext, accent, out var closeBtn);

        var dataGrid = new System.Windows.Controls.DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            IsReadOnly = true,
            HeadersVisibility = System.Windows.Controls.DataGridHeadersVisibility.Column,
            ItemsSource = claudeDocs,
            Background = surface1,
            Foreground = text,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            GridLinesVisibility = System.Windows.Controls.DataGridGridLinesVisibility.Horizontal,
            RowHeight = double.NaN,
            RowHeaderWidth = 0,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(16, 12, 16, 8)
        };
        dataGrid.Columns.Add(new System.Windows.Controls.DataGridTextColumn
        {
            Header = "Field",
            Binding = new System.Windows.Data.Binding(nameof(FrontmatterFieldDoc.Field)),
            Width = new System.Windows.Controls.DataGridLength(140)
        });
        dataGrid.Columns.Add(new System.Windows.Controls.DataGridTextColumn
        {
            Header = "Type",
            Binding = new System.Windows.Data.Binding(nameof(FrontmatterFieldDoc.Type)),
            Width = new System.Windows.Controls.DataGridLength(130)
        });
        dataGrid.Columns.Add(new System.Windows.Controls.DataGridTextColumn
        {
            Header = "Required",
            Binding = new System.Windows.Data.Binding(nameof(FrontmatterFieldDoc.Required)),
            Width = new System.Windows.Controls.DataGridLength(80)
        });
        var descColumn = new System.Windows.Controls.DataGridTextColumn
        {
            Header = "Description",
            Binding = new System.Windows.Data.Binding(nameof(FrontmatterFieldDoc.Description)),
            Width = new System.Windows.Controls.DataGridLength(1, System.Windows.Controls.DataGridLengthUnitType.Star)
        };
        dataGrid.Columns.Add(descColumn);
        var wrapStyle = new Style(typeof(System.Windows.Controls.TextBlock));
        wrapStyle.Setters.Add(new Setter(System.Windows.Controls.TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        wrapStyle.Setters.Add(new Setter(System.Windows.Controls.TextBlock.VerticalAlignmentProperty, VerticalAlignment.Top));
        descColumn.ElementStyle = wrapStyle;

        var dialogTitle = new System.Windows.Controls.TextBlock
        {
            Text = "Frontmatter Field Reference (Claude)",
            Foreground = text,
            FontSize = 12,
            Margin = new Thickness(16, 10, 16, 0)
        };

        void SetDocs(List<FrontmatterFieldDoc> docs, string titleSuffix)
        {
            dataGrid.ItemsSource = docs;
            dialogTitle.Text = $"Frontmatter Field Reference ({titleSuffix})";
        }

        var tabButtons = new WrapPanel
        {
            Margin = new Thickness(16, 8, 16, 0)
        };
        var claudeBtn = new Wpf.Ui.Controls.Button { Content = "Claude", Appearance = ControlAppearance.Secondary, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 6, 6) };
        var codexBtn = new Wpf.Ui.Controls.Button { Content = "Codex", Appearance = ControlAppearance.Secondary, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 6, 6) };
        var copilotBtn = new Wpf.Ui.Controls.Button { Content = "Copilot", Appearance = ControlAppearance.Secondary, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 6, 6) };
        var geminiBtn = new Wpf.Ui.Controls.Button { Content = "Gemini", Appearance = ControlAppearance.Secondary, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 6, 6) };
        tabButtons.Children.Add(claudeBtn);
        tabButtons.Children.Add(codexBtn);
        tabButtons.Children.Add(copilotBtn);
        tabButtons.Children.Add(geminiBtn);

        claudeBtn.Click += (_, _) => SetDocs(claudeDocs, "Claude");
        codexBtn.Click += (_, _) => SetDocs(codexDocs, "Codex");
        copilotBtn.Click += (_, _) => SetDocs(copilotDocs, "Copilot");
        geminiBtn.Click += (_, _) => SetDocs(geminiDocs, "Gemini");

        var closeButton = new Wpf.Ui.Controls.Button
        {
            Content = "Close",
            Appearance = ControlAppearance.Secondary,
            MinWidth = 90,
            Height = 32
        };
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 12)
        };
        footer.Children.Add(closeButton);

        var root = new Grid { Background = surface };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(dialogTitle, 1);
        Grid.SetRow(tabButtons, 2);
        Grid.SetRow(dataGrid, 3);
        Grid.SetRow(footer, 4);
        root.Children.Add(titleBar);
        root.Children.Add(dialogTitle);
        root.Children.Add(tabButtons);
        root.Children.Add(dataGrid);
        root.Children.Add(footer);

        var owner = Window.GetWindow(this);
        var dialog = new Window
        {
            Title = "",
            Owner = owner,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize,
            WindowStyle = WindowStyle.None,
            Width = 980,
            Height = 620,
            MinWidth = 760,
            MinHeight = 420,
            Background = surface,
            BorderBrush = surface2,
            BorderThickness = new Thickness(1),
            Content = root
        };

        System.Windows.Shell.WindowChrome.SetWindowChrome(dialog,
            new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(4),
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false
            });

        titleBar.MouseLeftButtonDown += (_, ev) =>
        {
            if (ev.LeftButton == MouseButtonState.Pressed)
                dialog.DragMove();
        };
        closeBtn.Click += (_, _) => dialog.Close();
        closeButton.Click += (_, _) => dialog.Close();

        SetDocs(claudeDocs, "Claude");
        dialog.ShowDialog();
    }

    private static Grid BuildTitleBar(
        string title,
        MediaBrush background,
        MediaBrush textBrush,
        MediaBrush subtextBrush,
        MediaBrush accentBrush,
        out System.Windows.Controls.Button closeBtnOut)
    {
        var titleBar = new Grid { Background = background, Height = 38 };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new System.Windows.Controls.TextBlock
        {
            Text = "●",
            Foreground = accentBrush,
            FontSize = 11,
            Margin = new Thickness(12, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(icon, 0);

        var titleText = new System.Windows.Controls.TextBlock
        {
            Text = title,
            Foreground = textBrush,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleText, 1);

        var closeBtn = new System.Windows.Controls.Button
        {
            Content = "✕",
            Width = 34,
            Height = 26,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = MediaBrushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = subtextBrush,
            FontSize = 13
        };
        Grid.SetColumn(closeBtn, 2);

        titleBar.Children.Add(icon);
        titleBar.Children.Add(titleText);
        titleBar.Children.Add(closeBtn);

        closeBtnOut = closeBtn;
        return titleBar;
    }
}
