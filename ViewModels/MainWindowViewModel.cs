using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Curia.Models;
using Curia.Services;

namespace Curia.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IRecipient<StatusUpdateMessage>
{
    private readonly ConfigService _config;
    // ---- ステータスバーの状態 ----
    [ObservableProperty]
    private string statusProject = "";

    [ObservableProperty]
    private string statusFile = "";

    [ObservableProperty]
    private string statusEncoding = "";

    [ObservableProperty]
    private bool statusDirty = false;

    [ObservableProperty]
    private bool isEditorActive = false;

    [ObservableProperty]
    private bool canUseAgent;

    public CommandPaletteViewModel CommandPaletteViewModel { get; }

    public MainWindowViewModel(CommandPaletteViewModel commandPaletteViewModel, ConfigService config)
    {
        CommandPaletteViewModel = commandPaletteViewModel;
        _config = config;
        RefreshAgentAvailability();
        // メッセージの受信登録
        WeakReferenceMessenger.Default.Register(this);
        WeakReferenceMessenger.Default.Register<AiEnabledChangedMessage>(this, (_, _) => RefreshAgentAvailability());
        WeakReferenceMessenger.Default.Register<AgentCompatibilityChangedMessage>(this, (_, _) => RefreshAgentAvailability());
    }

    // ステータス更新メッセージを受信
    public void Receive(StatusUpdateMessage message)
    {
        StatusProject = message.Project;
        StatusFile = message.File;
        StatusEncoding = message.Encoding;
        StatusDirty = message.IsDirty;
    }

    private void RefreshAgentAvailability()
    {
        var settings = _config.LoadSettings();
        CanUseAgent = settings.AiEnabled && settings.AgentCompatibilityOk
            && settings.AgentCompatibilityCheckedFor == $"{settings.LlmProvider}|{settings.LlmModel}";
    }
}
