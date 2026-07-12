using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Curia.Models;
using Curia.Services;
using Curia.Services.Agent;

namespace Curia.ViewModels;

public partial class AgentChatViewModel : ObservableObject
{
    private readonly AgentOrchestratorService _orchestrator;
    private readonly ConfigService _config;
    private readonly AgentToolRegistry _toolRegistry;
    private readonly AgentChatHistoryService _historyService;
    private CancellationTokenSource? _runCts;
    private bool _historyLoaded;

    public ObservableCollection<AgentChatMessage> Messages { get; } = [];
    public IReadOnlyList<AgentToolDescriptor> Tools { get; }

    [ObservableProperty] private string inputText = "";
    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private bool isAiEnabled;
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private bool isToolsPanelVisible;

    public bool CanUseAgent
    {
        get
        {
            var settings = _config.LoadSettings();
            return IsAiEnabled && settings.AgentCompatibilityOk
                && settings.AgentCompatibilityCheckedFor == $"{settings.LlmProvider}|{settings.LlmModel}";
        }
    }

    public AgentChatViewModel(AgentOrchestratorService orchestrator, ConfigService config, AgentToolRegistry toolRegistry,
        AgentChatHistoryService historyService)
    {
        _orchestrator = orchestrator;
        _config = config;
        _toolRegistry = toolRegistry;
        _historyService = historyService;
        Tools = _toolRegistry.GetDescriptors();
        IsAiEnabled = config.LoadSettings().AiEnabled;
        WeakReferenceMessenger.Default.Register<AiEnabledChangedMessage>(this,
            (_, message) => IsAiEnabled = message.Enabled);
    }

    partial void OnIsAiEnabledChanged(bool value) => OnPropertyChanged(nameof(CanUseAgent));

    public void RefreshAvailability()
    {
        IsAiEnabled = _config.LoadSettings().AiEnabled;
        OnPropertyChanged(nameof(CanUseAgent));
    }

    public async Task InitializeAsync()
    {
        if (_historyLoaded) return;
        _historyLoaded = true;
        var messages = await _historyService.LoadLatestSessionAsync();
        foreach (var message in messages) Messages.Add(message);
        if (messages.Count > 0) StatusMessage = "Restored the most recent chat session.";
        RefreshAvailability();
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        RefreshAvailability();
        var input = InputText.Trim();
        if (input.Length == 0 || IsRunning) return;
        if (!CanUseAgent)
        {
            StatusMessage = "Enable AI and pass the agent compatibility check in Settings.";
            return;
        }

        var userMessage = new AgentChatMessage { Kind = AgentMessageKind.User, Text = input, Timestamp = DateTime.Now };
        Messages.Add(userMessage);
        await SaveHistoryAsync();
        InputText = "";
        IsRunning = true;
        StatusMessage = "Working...";
        _runCts = new CancellationTokenSource();
        try
        {
            var answer = await _orchestrator.RunTurnAsync(Messages.Take(Messages.Count - 1).ToList(), input,
                _ => Task.FromResult(false), AddProgressMessage, _runCts.Token);
            Messages.Add(answer);
            StatusMessage = "";
        }
        catch (OperationCanceledException)
        {
            Messages.Add(new AgentChatMessage { Kind = AgentMessageKind.Error, Text = "Cancelled", Timestamp = DateTime.Now });
            StatusMessage = "Cancelled";
        }
        catch (Exception ex)
        {
            Messages.Add(new AgentChatMessage { Kind = AgentMessageKind.Error, Text = ex.Message, Timestamp = DateTime.Now });
            StatusMessage = "Error";
        }
        finally
        {
            _runCts?.Dispose();
            _runCts = null;
            IsRunning = false;
            await SaveHistoryAsync();
        }
    }

    [RelayCommand]
    private void Cancel() => _runCts?.Cancel();

    [RelayCommand]
    private void NewSession()
    {
        if (IsRunning) return;
        Messages.Clear();
        _historyService.StartNewSession();
        StatusMessage = "";
    }

    [RelayCommand]
    private void ToggleToolsPanel() => IsToolsPanelVisible = !IsToolsPanelVisible;

    private void AddProgressMessage(AgentChatMessage message)
    {
        if (Application.Current.Dispatcher.CheckAccess())
        {
            Messages.Add(message);
            _ = SaveHistoryAsync();
        }
        else Application.Current.Dispatcher.Invoke(() =>
        {
            Messages.Add(message);
            _ = SaveHistoryAsync();
        });
    }

    private async Task SaveHistoryAsync()
    {
        try { await _historyService.SaveAsync(Messages); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}