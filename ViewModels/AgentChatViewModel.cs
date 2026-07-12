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
    private TaskCompletionSource<bool>? _approvalTcs;
    private readonly HashSet<string> _autoApprovedTools = new(StringComparer.OrdinalIgnoreCase);
    private bool _historyLoaded;
    private readonly object _initializationLock = new();
    private Task? _initializationTask;

    public ObservableCollection<AgentChatMessage> Messages { get; } = [];
    public ObservableCollection<AgentChatSessionSummary> Sessions { get; } = [];
    public IReadOnlyList<AgentToolDescriptor> Tools { get; }

    [ObservableProperty] private string inputText = "";
    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private bool isAiEnabled;
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private bool isToolsPanelVisible;
    [ObservableProperty] private bool isHistoryPanelVisible;
    [ObservableProperty] private AgentChatSessionSummary? selectedSession;
    [ObservableProperty] private AgentChatMessage? pendingApproval;

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
            (_, message) =>
            {
                IsAiEnabled = message.Enabled;
                if (!message.Enabled)
                {
                    ResolvePendingApproval(false);
                    _runCts?.Cancel();
                }
            });
        WeakReferenceMessenger.Default.Register<AgentCompatibilityChangedMessage>(this,
            (_, _) => RefreshAvailability());
    }

    partial void OnIsAiEnabledChanged(bool value) => OnPropertyChanged(nameof(CanUseAgent));

    public void RefreshAvailability()
    {
        IsAiEnabled = _config.LoadSettings().AiEnabled;
        OnPropertyChanged(nameof(CanUseAgent));
    }

    public async Task InitializeAsync()
    {
        RefreshAvailability();
        Task initialization;
        lock (_initializationLock)
            initialization = _initializationTask ??= InitializeCoreAsync();
        await initialization;
    }

    private async Task InitializeCoreAsync()
    {
        if (_historyLoaded) return;
        _historyLoaded = true;
        var messages = await _historyService.LoadLatestSessionAsync();
        foreach (var message in messages) Messages.Add(message);
        if (messages.Count > 0) StatusMessage = "Restored the most recent chat session.";
        await RefreshSessionsAsync();
    }

    [RelayCommand]
    private async Task SendAsync()
        => await SubmitAsync(InputText);

    public async Task SubmitAsync(string text)
    {
        await InitializeAsync();
        RefreshAvailability();
        var input = text.Trim();
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
                RequestApprovalAsync, AddProgressMessage, _runCts.Token);
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
            ResolvePendingApproval(false);
            _runCts?.Dispose();
            _runCts = null;
            IsRunning = false;
            await SaveHistoryAsync();
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        ResolvePendingApproval(false);
        _runCts?.Cancel();
    }

    [RelayCommand]
    private void NewSession()
    {
        if (IsRunning) return;
        Messages.Clear();
        _autoApprovedTools.Clear();
        PendingApproval = null;
        _historyService.StartNewSession();
        StatusMessage = "";
        SelectedSession = null;
    }

    [RelayCommand]
    private void ToggleToolsPanel() => IsToolsPanelVisible = !IsToolsPanelVisible;

    [RelayCommand]
    private async Task ToggleHistoryPanelAsync()
    {
        IsHistoryPanelVisible = !IsHistoryPanelVisible;
        if (IsHistoryPanelVisible) await RefreshSessionsAsync();
    }

    [RelayCommand]
    private async Task LoadSessionAsync(AgentChatSessionSummary? session)
    {
        if (session == null || IsRunning) return;
        var messages = await _historyService.LoadSessionAsync(session.Path);
        Messages.Clear();
        foreach (var message in messages) Messages.Add(message);
        _autoApprovedTools.Clear();
        SelectedSession = session;
        IsHistoryPanelVisible = false;
        StatusMessage = $"Restored chat from {session.UpdatedAt:g}.";
    }

    [RelayCommand]
    private async Task DeleteSessionAsync(AgentChatSessionSummary? session)
    {
        if (session == null || IsRunning) return;
        await _historyService.DeleteSessionAsync(session.Path);
        if (SelectedSession == session)
        {
            Messages.Clear();
            _autoApprovedTools.Clear();
            PendingApproval = null;
            _historyService.StartNewSession();
            SelectedSession = null;
            StatusMessage = "Deleted session. Started a new chat.";
        }
        await RefreshSessionsAsync();
    }

    [RelayCommand]
    private async Task RunMorningPreparationAsync()
        => await SubmitAsync("Prepare my morning briefing. Check today's schedule, my today task queue, and the latest standup. Summarize the priorities and any conflicts.");

    private async Task RefreshSessionsAsync()
    {
        var sessions = await _historyService.ListSessionsAsync();
        Sessions.Clear();
        foreach (var session in sessions) Sessions.Add(session);
    }

    private void AddProgressMessage(AgentChatMessage message)
    {
        if (Application.Current.Dispatcher.CheckAccess())
        {
            Messages.Add(message);
        }
        else Application.Current.Dispatcher.Invoke(() =>
        {
            Messages.Add(message);
        });
    }

    [RelayCommand]
    private void Approve(AgentChatMessage? message) => ResolvePendingApproval(true, message);

    [RelayCommand]
    private void Reject(AgentChatMessage? message) => ResolvePendingApproval(false, message);

    private Task<bool> RequestApprovalAsync(AgentToolCall toolCall)
    {
        if (_autoApprovedTools.Contains(toolCall.Tool)) return Task.FromResult(true);
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var message = new AgentChatMessage
            {
                Kind = AgentMessageKind.Approval,
                ToolCall = toolCall,
                Text = "Approval required before this action is performed.",
                Timestamp = DateTime.Now
            };
            _approvalTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            PendingApproval = message;
            Messages.Add(message);
            return _approvalTcs.Task;
        }).Task.Unwrap();
    }

    private void ResolvePendingApproval(bool approved, AgentChatMessage? message = null)
    {
        if (_approvalTcs == null || PendingApproval == null || (message != null && message != PendingApproval)) return;
        if (approved && PendingApproval.AutoApproveForSession && PendingApproval.ToolCall != null)
            _autoApprovedTools.Add(PendingApproval.ToolCall.Tool);
        PendingApproval.IsApprovalResolved = true;
        PendingApproval.Text = approved ? "Approved" : "Rejected";
        _approvalTcs.TrySetResult(approved);
        _approvalTcs = null;
        PendingApproval = null;
    }

    private async Task SaveHistoryAsync()
    {
        try { await _historyService.SaveAsync(Messages); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}