using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Curia.Helpers;
using Curia.Models;
using Curia.Services;
using Curia.Services.Agent;

namespace Curia.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ConfigService _configService;
    private readonly HotkeyService _hotkeyService;
    private readonly TrayService _trayService;
    private readonly LlmClientService _llmClientService;
    private readonly AgentCompatibilityService _agentCompatibilityService;
    private readonly OutlookCalendarService _outlookCalendarService;
    private readonly IcsCalendarService _icsCalendarService;
    private bool _loading;
    private readonly AsyncInitializationGate _initialization = new();

    // ホットキー
    [ObservableProperty]
    private bool hotkeyCtrl = true;

    [ObservableProperty]
    private bool hotkeyShift = true;

    [ObservableProperty]
    private bool hotkeyAlt;

    [ObservableProperty]
    private bool hotkeyWin;

    [ObservableProperty]
    private string hotkeyKey = "P";

    // キャプチャホットキー
    [ObservableProperty]
    private bool captureHotkeyCtrl = true;

    [ObservableProperty]
    private bool captureHotkeyShift = true;

    [ObservableProperty]
    private bool captureHotkeyAlt;

    [ObservableProperty]
    private bool captureHotkeyWin;

    [ObservableProperty]
    private string captureHotkeyKey = "C";

    // コマンドパレットホットキー
    [ObservableProperty]
    private bool commandPaletteHotkeyCtrl = true;

    [ObservableProperty]
    private bool commandPaletteHotkeyShift = true;

    [ObservableProperty]
    private bool commandPaletteHotkeyAlt;

    [ObservableProperty]
    private bool commandPaletteHotkeyWin;

    [ObservableProperty]
    private string commandPaletteHotkeyKey = "K";

    // スタートアップ
    [ObservableProperty]
    private bool startupEnabled;

    // Dashboard
    [ObservableProperty]
    private int todayQueueLimit = 5;

    [ObservableProperty]
    private int autoRefreshMinutes;

    // Workspace paths
    [ObservableProperty]
    private string localProjectsRoot = "";

    [ObservableProperty]
    private string cloudSyncRoot = "";

    [ObservableProperty]
    private string obsidianVaultRoot = "";

    [ObservableProperty]
    private string workspacePathsWarning = "";

    // Asana global config (_config/asana_global.json)
    [ObservableProperty]
    private string asanaToken = "";

    [ObservableProperty]
    private string asanaWorkspaceGid = "";

    [ObservableProperty]
    private string asanaUserGid = "";

    [ObservableProperty]
    private string asanaPersonalProjectGids = "";

    [ObservableProperty]
    private string asanaGlobalStatus = "";

    // LLM API settings
    [ObservableProperty]
    private string llmProvider = "openai";

    [ObservableProperty]
    private string llmApiKey = "";

    [ObservableProperty]
    private string llmModel = "gpt-4o";

    [ObservableProperty]
    private string llmEndpoint = "";

    [ObservableProperty]
    private string llmApiVersion = "2024-12-01-preview";

    // LLM 追加パラメータ (key = value 形式、1行1パラメータ)
    [ObservableProperty]
    private string llmParametersText = "";

    // LLM ユーザープロフィール (全 LLM 呼び出しのシステムプロンプトに付与)
    [ObservableProperty]
    private string llmUserProfile = "";

    // LLM レスポンス言語
    [ObservableProperty]
    private string llmLanguage = "English";

    [ObservableProperty]
    private string llmStatus = "";

    [ObservableProperty]
    private bool llmIsAzure;

    [ObservableProperty]
    private bool isCliProvider;

    [ObservableProperty]
    private bool aiEnabled;

    // Test Connection が成功するまでトグルを有効化できない
    [ObservableProperty]
    private bool aiToggleCanEnable;

    // Focus Auto-Update (行動シグナルからの current_focus 自動更新)
    [ObservableProperty]
    private bool focusAutoUpdateBadgeEnabled;

    [ObservableProperty]
    private int focusSignalLookbackDays;

    [ObservableProperty]
    private string agentCompatibilityStatus = "";

    // Capture
    [ObservableProperty]
    private bool captureTaskLogEnabled;

    // Silence Alert
    [ObservableProperty]
    private bool silenceAlertEnabled;

    // Schedule / Outlook 連携
    [ObservableProperty]
    private bool outlookCalendarEnabled;

    [ObservableProperty]
    private bool outlookAvailable;

    // Schedule / ICS 連携
    [ObservableProperty]
    private bool icsCalendarEnabled;

    [ObservableProperty]
    private string icsCalendarUrl = "";

    // Editor / Wiki フォントサイズ
    [ObservableProperty]
    private int editorFontSize = 14;

    [ObservableProperty]
    private int markdownRenderFontSize = 13;

    // Editor / Wiki 文字色 (RGB 各チャンネル 0-255)
    [ObservableProperty] private int editorColorR = 201;
    [ObservableProperty] private int editorColorG = 209;
    [ObservableProperty] private int editorColorB = 217;

    [ObservableProperty] private int markdownColorR = 201;
    [ObservableProperty] private int markdownColorG = 209;
    [ObservableProperty] private int markdownColorB = 217;

    // RGB から派生する hex 文字列 (保存・メッセージ送信用)
    public string EditorTextColor => $"#{EditorColorR:X2}{EditorColorG:X2}{EditorColorB:X2}";
    public string MarkdownRenderTextColor => $"#{MarkdownColorR:X2}{MarkdownColorG:X2}{MarkdownColorB:X2}";

    // About
    public string AppVersion { get; } =
        "v" + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0");

    public System.Windows.Media.Imaging.BitmapSource? AppIcon => _trayService.DiamondBitmapSource;

    // ホットキー表示更新コールバック (TrayService.UpdateHotkeyDisplay を呼ぶ)
    public Action<string>? OnHotkeyDisplayChanged;

    public SettingsViewModel(
        ConfigService configService,
        HotkeyService hotkeyService,
        TrayService trayService,
        LlmClientService llmClientService,
        AgentCompatibilityService agentCompatibilityService,
        OutlookCalendarService outlookCalendarService,
        IcsCalendarService icsCalendarService)
    {
        _configService = configService;
        _hotkeyService = hotkeyService;
        _trayService = trayService;
        _llmClientService = llmClientService;
        _agentCompatibilityService = agentCompatibilityService;
        _outlookCalendarService = outlookCalendarService;
        _icsCalendarService = icsCalendarService;
    }

    /// <summary>ディスクから設定を読み込む。ページ表示時に呼ぶ。</summary>
    public void Load()
    {
        _loading = true;
        try
        {
            var settings = _configService.LoadSettings();
            TodayQueueLimit = settings.DashboardTodayQueueLimit;
            AutoRefreshMinutes = settings.DashboardAutoRefreshMinutes;
            LocalProjectsRoot = settings.LocalProjectsRoot;
            CloudSyncRoot = settings.CloudSyncRoot;
            ObsidianVaultRoot = settings.ObsidianVaultRoot;
            UpdateWorkspacePathsWarning();

            var hk = settings.Hotkey ?? new HotkeyConfig();
            var mods = hk.Modifiers.Split('+',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            HotkeyCtrl = mods.Contains("Ctrl", StringComparer.OrdinalIgnoreCase);
            HotkeyShift = mods.Contains("Shift", StringComparer.OrdinalIgnoreCase);
            HotkeyAlt = mods.Contains("Alt", StringComparer.OrdinalIgnoreCase);
            HotkeyWin = mods.Contains("Win", StringComparer.OrdinalIgnoreCase);
            HotkeyKey = hk.Key;

            var chk = settings.CaptureHotkey ?? new HotkeyConfig { Modifiers = "Ctrl+Shift", Key = "C" };
            var capMods = chk.Modifiers.Split('+',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            CaptureHotkeyCtrl = capMods.Contains("Ctrl", StringComparer.OrdinalIgnoreCase);
            CaptureHotkeyShift = capMods.Contains("Shift", StringComparer.OrdinalIgnoreCase);
            CaptureHotkeyAlt = capMods.Contains("Alt", StringComparer.OrdinalIgnoreCase);
            CaptureHotkeyWin = capMods.Contains("Win", StringComparer.OrdinalIgnoreCase);
            CaptureHotkeyKey = chk.Key;

            var cpk = settings.CommandPaletteHotkey ?? new HotkeyConfig { Modifiers = "Ctrl+Shift", Key = "K" };
            var cpMods = cpk.Modifiers.Split('+',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            CommandPaletteHotkeyCtrl = cpMods.Contains("Ctrl", StringComparer.OrdinalIgnoreCase);
            CommandPaletteHotkeyShift = cpMods.Contains("Shift", StringComparer.OrdinalIgnoreCase);
            CommandPaletteHotkeyAlt = cpMods.Contains("Alt", StringComparer.OrdinalIgnoreCase);
            CommandPaletteHotkeyWin = cpMods.Contains("Win", StringComparer.OrdinalIgnoreCase);
            CommandPaletteHotkeyKey = cpk.Key;

            StartupEnabled = File.Exists(GetStartupShortcutPath());

            var asana = _configService.LoadAsanaGlobalConfig();
            AsanaToken = asana.AsanaToken;
            AsanaWorkspaceGid = asana.WorkspaceGid;
            AsanaUserGid = asana.UserGid;
            AsanaPersonalProjectGids = string.Join("\n", asana.PersonalProjectGids);
            AsanaGlobalStatus = "";

            // LLM
            LlmProvider    = settings.LlmProvider;
            LlmApiKey      = settings.LlmApiKey;
            LlmModel       = settings.LlmModel;
            LlmEndpoint    = settings.LlmEndpoint;
            LlmApiVersion  = settings.LlmApiVersion;
            LlmParametersText = string.Join("\n",
                settings.LlmParameters.Select(kv => $"{kv.Key} = {kv.Value}"));
            LlmUserProfile     = settings.LlmUserProfile;
            LlmLanguage        = string.IsNullOrWhiteSpace(settings.LlmLanguage) ? "English" : settings.LlmLanguage;
            LlmIsAzure         = settings.LlmProvider.Equals("azure_openai", StringComparison.OrdinalIgnoreCase);
            IsCliProvider      = LlmClientService.IsCliProvider(settings.LlmProvider);
            LlmStatus          = "";
            AiEnabled          = settings.AiEnabled;
            AiToggleCanEnable  = settings.AiEnabled; // 既にオンなら再テスト不要
            FocusAutoUpdateBadgeEnabled = settings.FocusAutoUpdateBadgeEnabled;
            FocusSignalLookbackDays     = settings.FocusSignalLookbackDays;
            AgentCompatibilityStatus = settings.AgentCompatibilityCheckedFor == $"{settings.LlmProvider}|{settings.LlmModel}"
                ? (settings.AgentCompatibilityOk ? "Compatible" : "Not compatible") : "Not checked for this provider/model";
            SilenceAlertEnabled    = settings.SilenceAlertEnabled;
            CaptureTaskLogEnabled  = settings.CaptureTaskLogEnabled;
            OutlookCalendarEnabled = settings.OutlookCalendarEnabled;
            OutlookAvailable       = _outlookCalendarService.IsOutlookAvailable();
            IcsCalendarEnabled     = settings.IcsCalendarEnabled;
            IcsCalendarUrl         = settings.IcsCalendarUrl;
            EditorFontSize         = settings.EditorFontSize;
            MarkdownRenderFontSize = settings.MarkdownRenderFontSize;
            if (TryParseHex(settings.EditorTextColor, out var er, out var eg, out var eb))
                { EditorColorR = er; EditorColorG = eg; EditorColorB = eb; }
            if (TryParseHex(settings.MarkdownRenderTextColor, out var mr, out var mg, out var mb))
                { MarkdownColorR = mr; MarkdownColorG = mg; MarkdownColorB = mb; }
        }
        finally
        {
            _loading = false;
        }
    }

    public Task EnsureInitializedAsync()
        => _initialization.EnsureAsync(() =>
        {
            Load();
            return Task.CompletedTask;
        });

    partial void OnStartupEnabledChanged(bool value)
    {
        if (_loading) return;
        var shortcutPath = GetStartupShortcutPath();
        if (value) CreateStartupShortcut(shortcutPath);
        else RemoveStartupShortcut(shortcutPath);
    }

    [RelayCommand]
    public void ApplyHotkey()
    {
        var mods = new List<string>();
        if (HotkeyCtrl) mods.Add("Ctrl");
        if (HotkeyShift) mods.Add("Shift");
        if (HotkeyAlt) mods.Add("Alt");
        if (HotkeyWin) mods.Add("Win");
        var modStr = string.Join("+", mods);
        if (string.IsNullOrWhiteSpace(HotkeyKey)) return;

        _hotkeyService.ReRegister(modStr, HotkeyKey.Trim());
        OnHotkeyDisplayChanged?.Invoke(_hotkeyService.HotkeyDisplayText);

        var settings = _configService.LoadSettings();
        settings.Hotkey = new HotkeyConfig { Modifiers = modStr, Key = HotkeyKey.Trim() };
        _configService.SaveSettings(settings);
    }

    [RelayCommand]
    public void ApplyCaptureHotkey()
    {
        var mods = new List<string>();
        if (CaptureHotkeyCtrl) mods.Add("Ctrl");
        if (CaptureHotkeyShift) mods.Add("Shift");
        if (CaptureHotkeyAlt) mods.Add("Alt");
        if (CaptureHotkeyWin) mods.Add("Win");
        var modStr = string.Join("+", mods);
        if (string.IsNullOrWhiteSpace(CaptureHotkeyKey)) return;

        _hotkeyService.ReRegisterCapture(modStr, CaptureHotkeyKey.Trim());

        var settings = _configService.LoadSettings();
        settings.CaptureHotkey = new HotkeyConfig { Modifiers = modStr, Key = CaptureHotkeyKey.Trim() };
        _configService.SaveSettings(settings);
    }

    [RelayCommand]
    public void ApplyCommandPaletteHotkey()
    {
        var mods = new List<string>();
        if (CommandPaletteHotkeyCtrl) mods.Add("Ctrl");
        if (CommandPaletteHotkeyShift) mods.Add("Shift");
        if (CommandPaletteHotkeyAlt) mods.Add("Alt");
        if (CommandPaletteHotkeyWin) mods.Add("Win");
        var modStr = string.Join("+", mods);
        if (string.IsNullOrWhiteSpace(CommandPaletteHotkeyKey)) return;

        _hotkeyService.ReRegisterCommandPalette(modStr, CommandPaletteHotkeyKey.Trim());

        var settings = _configService.LoadSettings();
        settings.CommandPaletteHotkey = new HotkeyConfig { Modifiers = modStr, Key = CommandPaletteHotkeyKey.Trim() };
        _configService.SaveSettings(settings);
    }

    [RelayCommand]
    public void Save()
    {
        var settings = _configService.LoadSettings();
        settings.DashboardTodayQueueLimit = TodayQueueLimit;
        settings.DashboardAutoRefreshMinutes = AutoRefreshMinutes;
        settings.LocalProjectsRoot = LocalProjectsRoot.Trim();
        settings.CloudSyncRoot = CloudSyncRoot.Trim();
        settings.ObsidianVaultRoot = ObsidianVaultRoot.Trim();
        // LLM 設定も同時に保存 (どちらの Save ボタンを押してもすべて反映される)
        settings.LlmProvider    = LlmProvider.Trim();
        settings.LlmApiKey      = LlmApiKey.Trim();
        settings.LlmModel       = LlmModel.Trim();
        settings.LlmEndpoint    = LlmEndpoint.Trim();
        settings.LlmApiVersion  = LlmApiVersion.Trim();
        settings.LlmParameters  = ParseLlmParametersText(LlmParametersText);
        settings.LlmUserProfile = LlmUserProfile;
        settings.LlmLanguage    = LlmLanguage.Trim();
        settings.AiEnabled             = AiEnabled;
        settings.FocusAutoUpdateBadgeEnabled = FocusAutoUpdateBadgeEnabled;
        settings.FocusSignalLookbackDays     = FocusSignalLookbackDays > 0 ? FocusSignalLookbackDays : 14;
        InvalidateAgentCompatibilityIfProviderChanged(settings);
        settings.SilenceAlertEnabled   = SilenceAlertEnabled;
        settings.CaptureTaskLogEnabled = CaptureTaskLogEnabled;
        settings.OutlookCalendarEnabled  = OutlookCalendarEnabled;
        settings.IcsCalendarEnabled      = IcsCalendarEnabled;
        settings.IcsCalendarUrl          = IcsCalendarUrl.Trim();
        settings.EditorFontSize          = EditorFontSize;
        settings.MarkdownRenderFontSize  = MarkdownRenderFontSize;
        settings.EditorTextColor         = EditorTextColor.Trim();
        settings.MarkdownRenderTextColor = MarkdownRenderTextColor.Trim();
        _configService.SaveSettings(settings);
        UpdateWorkspacePathsWarning();
        WeakReferenceMessenger.Default.Send(new FontSizeChangedMessage(EditorFontSize, MarkdownRenderFontSize));
        WeakReferenceMessenger.Default.Send(new TextColorChangedMessage(EditorTextColor.Trim(), MarkdownRenderTextColor.Trim()));
    }

    [RelayCommand]
    public void SaveLlm()
    {
        var settings = _configService.LoadSettings();
        settings.LlmProvider    = LlmProvider.Trim();
        settings.LlmApiKey      = LlmApiKey.Trim();
        settings.LlmModel       = LlmModel.Trim();
        settings.LlmEndpoint    = LlmEndpoint.Trim();
        settings.LlmApiVersion  = LlmApiVersion.Trim();
        settings.LlmParameters  = ParseLlmParametersText(LlmParametersText);
        settings.LlmUserProfile = LlmUserProfile;
        settings.LlmLanguage    = LlmLanguage.Trim();
        settings.AiEnabled      = AiEnabled;
        InvalidateAgentCompatibilityIfProviderChanged(settings);
        _configService.SaveSettings(settings);
        LlmStatus = $"Saved {DateTime.Now:HH:mm:ss}";
    }

    private void InvalidateAgentCompatibilityIfProviderChanged(AppSettings settings)
    {
        var current = $"{settings.LlmProvider}|{settings.LlmModel}";
        if (!string.Equals(settings.AgentCompatibilityCheckedFor, current, StringComparison.OrdinalIgnoreCase))
        {
            settings.AgentCompatibilityOk = false;
            settings.AgentCompatibilityCheckedFor = "";
            AgentCompatibilityStatus = "Not checked for this provider/model";
            WeakReferenceMessenger.Default.Send(new AgentCompatibilityChangedMessage(false));
        }
    }

    [RelayCommand]
    public async Task TestLlmConnectionAsync()
    {
        LlmStatus = "Testing...";
        try
        {
            var reply = await _llmClientService.TestConnectionAsync(CancellationToken.None);
            LlmStatus = $"OK: {reply.Trim()} ({DateTime.Now:HH:mm:ss})";
            AiToggleCanEnable = true;
        }
        catch (Exception ex)
        {
            LlmStatus = $"Error: {ex.Message}";
            AiToggleCanEnable = false;
            AiEnabled = false;
        }
    }

    [RelayCommand]
    public async Task TestAgentCompatibilityAsync()
    {
        LlmStatus = "Testing agent compatibility...";
        try
        {
            var passed = await _agentCompatibilityService.TestAsync(CancellationToken.None);
            AgentCompatibilityStatus = passed ? "Compatible" : "Not compatible";
            LlmStatus = passed ? "Agent compatibility check passed." : "Agent compatibility check failed.";
        }
        catch (Exception ex)
        {
            AgentCompatibilityStatus = "Check failed";
            LlmStatus = $"Agent compatibility error: {ex.Message}";
        }
    }

    partial void OnAiEnabledChanged(bool value)
    {
        if (_loading) return;
        var settings = _configService.LoadSettings();
        settings.AiEnabled = value;
        _configService.SaveSettings(settings);
        WeakReferenceMessenger.Default.Send(new AiEnabledChangedMessage(value));
    }

    partial void OnSilenceAlertEnabledChanged(bool value)
    {
        if (_loading) return;
        var settings = _configService.LoadSettings();
        settings.SilenceAlertEnabled = value;
        _configService.SaveSettings(settings);
        WeakReferenceMessenger.Default.Send(new SilenceAlertEnabledChangedMessage(value));
    }

    partial void OnIcsCalendarEnabledChanged(bool value)
    {
        if (_loading) return;
        var settings = _configService.LoadSettings();
        settings.IcsCalendarEnabled = value;
        settings.IcsCalendarUrl     = IcsCalendarUrl.Trim();
        _configService.SaveSettings(settings);
    }

    [ObservableProperty]
    private string icsStatus = "";

    [RelayCommand]
    private async Task TestIcsAsync()
    {
        var url = IcsCalendarUrl.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            IcsStatus = "Enter a URL first.";
            return;
        }
        // URL を保存してからテスト
        var settings = _configService.LoadSettings();
        settings.IcsCalendarUrl = url;
        _configService.SaveSettings(settings);

        IcsStatus = "Fetching...";
        try
        {
            var weekStart = ViewModels.WeeklyScheduleViewModel.GetMondayOf(DateTime.Today);
            var events = await _icsCalendarService.GetEventsForWeekAsync(url, weekStart);
            IcsStatus = events.Count > 0
                ? $"OK - {events.Count} event(s) this week."
                : "OK - No events found for this week (URL works).";
        }
        catch (Exception ex)
        {
            IcsStatus = $"Error: {ex.Message}";
        }
    }

    partial void OnLlmProviderChanged(string value)
    {
        if (_loading) return;
        LlmIsAzure    = value.Equals("azure_openai", StringComparison.OrdinalIgnoreCase);
        IsCliProvider  = LlmClientService.IsCliProvider(value);
    }

    [RelayCommand]
    public void SaveAsanaGlobal()
    {
        try
        {
            var gids = AsanaPersonalProjectGids
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();

            var config = new AsanaGlobalConfig
            {
                AsanaToken = AsanaToken.Trim(),
                WorkspaceGid = AsanaWorkspaceGid.Trim(),
                UserGid = AsanaUserGid.Trim(),
                PersonalProjectGids = gids,
            };

            _configService.SaveAsanaGlobalConfig(config);
            AsanaGlobalStatus = $"Saved {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            AsanaGlobalStatus = $"Error: {ex.Message}";
        }
    }

    private static string GetStartupShortcutPath()
    {
        var startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        return Path.Combine(startupFolder, "Curia.lnk");
    }

    private static void CreateStartupShortcut(string shortcutPath)
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule!.FileName;
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            var shell = (dynamic)Activator.CreateInstance(shellType)!;
            var shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = exePath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(exePath) ?? "";
            shortcut.Save();
        }
        catch { }
    }

    private static void RemoveStartupShortcut(string shortcutPath)
    {
        try { File.Delete(shortcutPath); }
        catch { }
    }

    partial void OnLocalProjectsRootChanged(string value)
    {
        if (_loading) return;
        UpdateWorkspacePathsWarning();
    }

    partial void OnCloudSyncRootChanged(string value)
    {
        if (_loading) return;
        UpdateWorkspacePathsWarning();
    }

    partial void OnObsidianVaultRootChanged(string value)
    {
        if (_loading) return;
        UpdateWorkspacePathsWarning();
    }

    partial void OnEditorColorRChanged(int value) => OnPropertyChanged(nameof(EditorTextColor));
    partial void OnEditorColorGChanged(int value) => OnPropertyChanged(nameof(EditorTextColor));
    partial void OnEditorColorBChanged(int value) => OnPropertyChanged(nameof(EditorTextColor));
    partial void OnMarkdownColorRChanged(int value) => OnPropertyChanged(nameof(MarkdownRenderTextColor));
    partial void OnMarkdownColorGChanged(int value) => OnPropertyChanged(nameof(MarkdownRenderTextColor));
    partial void OnMarkdownColorBChanged(int value) => OnPropertyChanged(nameof(MarkdownRenderTextColor));

    private const int DefaultColorR = 201;
    private const int DefaultColorG = 209;
    private const int DefaultColorB = 217;

    [RelayCommand]
    void AdjustEditorColor(string param)
    {
        var delta = param == "+" ? 5 : -5;
        EditorColorR = Math.Clamp(EditorColorR + delta, 0, 255);
        EditorColorG = Math.Clamp(EditorColorG + delta, 0, 255);
        EditorColorB = Math.Clamp(EditorColorB + delta, 0, 255);
    }

    [RelayCommand]
    void ResetEditorColor()
    {
        EditorColorR = DefaultColorR;
        EditorColorG = DefaultColorG;
        EditorColorB = DefaultColorB;
    }

    [RelayCommand]
    void AdjustMarkdownColor(string param)
    {
        var delta = param == "+" ? 5 : -5;
        MarkdownColorR = Math.Clamp(MarkdownColorR + delta, 0, 255);
        MarkdownColorG = Math.Clamp(MarkdownColorG + delta, 0, 255);
        MarkdownColorB = Math.Clamp(MarkdownColorB + delta, 0, 255);
    }

    [RelayCommand]
    void ResetMarkdownColor()
    {
        MarkdownColorR = DefaultColorR;
        MarkdownColorG = DefaultColorG;
        MarkdownColorB = DefaultColorB;
    }

    private static bool TryParseHex(string? hex, out int r, out int g, out int b)
    {
        r = g = b = 0;
        var s = hex?.TrimStart('#');
        if (s?.Length != 6) return false;
        try
        {
            r = Convert.ToInt32(s[0..2], 16);
            g = Convert.ToInt32(s[2..4], 16);
            b = Convert.ToInt32(s[4..6], 16);
            return true;
        }
        catch { return false; }
    }

    private static Dictionary<string, string> ParseLlmParametersText(string text)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = line.IndexOf('=');
            if (idx <= 0) continue;
            var key = line[..idx].Trim();
            var val = line[(idx + 1)..].Trim();
            if (key.Length > 0)
                dict[key] = val;
        }
        return dict;
    }

    private void UpdateWorkspacePathsWarning()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(LocalProjectsRoot)) missing.Add("Local Projects Root");
        if (string.IsNullOrWhiteSpace(CloudSyncRoot)) missing.Add("Cloud Sync Root");
        if (string.IsNullOrWhiteSpace(ObsidianVaultRoot)) missing.Add("Obsidian Vault Root");

        WorkspacePathsWarning = missing.Count == 0
            ? ""
            : $"Missing required paths: {string.Join(", ", missing)}. Some features may not work until saved.";
    }
}
