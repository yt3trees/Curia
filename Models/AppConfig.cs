using System.Text.Json.Serialization;

namespace Curia.Models;

public class HotkeyConfig
{
    public string Modifiers { get; set; } = "Ctrl+Shift";
    public string Key { get; set; } = "P";
}

public class AsanaSyncConfig
{
    public bool Enabled { get; set; } = false;
    public int IntervalMin { get; set; } = 60;
    public bool SkipHiddenProjects { get; set; } = true;
}

/// <summary>
/// _config/asana_global.json に対応する設定クラス。
/// Asana API 認証情報とグローバル設定。
/// </summary>
public class AsanaGlobalConfig
{
    [JsonPropertyName("asana_token")]
    public string AsanaToken { get; set; } = "";

    [JsonPropertyName("workspace_gid")]
    public string WorkspaceGid { get; set; } = "";

    [JsonPropertyName("user_gid")]
    public string UserGid { get; set; } = "";

    [JsonPropertyName("personal_project_gids")]
    public List<string> PersonalProjectGids { get; set; } = [];
}

/// <summary>
/// window_state.json に対応する設定クラス。
/// </summary>
public class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

/// <summary>
/// settings.json に対応する設定クラス。
/// </summary>
public class AppSettings
{
    public bool DashboardTodayQueueVisible { get; set; } = true;
    public int DashboardTodayQueueLimit { get; set; } = 5;
    public int DashboardAutoRefreshMinutes { get; set; } = 0;
    public string LocalProjectsRoot { get; set; } = "";
    public string CloudSyncRoot { get; set; } = "";
    public string ObsidianVaultRoot { get; set; } = "";
    public HotkeyConfig? Hotkey { get; set; }
    public HotkeyConfig? CaptureHotkey { get; set; }
    public HotkeyConfig? CommandPaletteHotkey { get; set; }
    public AsanaSyncConfig? AsanaSync { get; set; }

    // LLM API 設定 (Update Focus 機能用)
    public string LlmProvider { get; set; } = "openai";         // "openai" | "azure_openai"
    public string LlmApiKey { get; set; } = "";
    public string LlmModel { get; set; } = "gpt-4o";
    public string LlmEndpoint { get; set; } = "";              // Azure OpenAI のエンドポイント URL
    public string LlmApiVersion { get; set; } = "2024-12-01-preview"; // Azure OpenAI の API バージョン
    public bool AiEnabled { get; set; } = false;               // AI 機能を有効にするか
    public bool FocusAutoUpdateBadgeEnabled { get; set; } = true;  // Dashboard の Auto-update focus アクション表示
    public int FocusSignalLookbackDays { get; set; } = 14;         // 行動シグナル収集期間の上限 (日)
    public int AgentMaxIterations { get; set; } = 10;
    public int AgentToolResultMaxChars { get; set; } = 30000;
    public bool AgentCompatibilityOk { get; set; } = false;
    public string AgentCompatibilityCheckedFor { get; set; } = "";
    public bool SilenceAlertEnabled { get; set; } = true;      // 沈黙アラート機能を有効にするか
    // 追加パラメータ (reasoning_effort, temperature, max_tokens 等)
    public Dictionary<string, string> LlmParameters { get; set; } = [];
    public string LlmUserProfile { get; set; } = "";
    // Capture 設定
    public bool CaptureTaskLogEnabled { get; set; } = false;   // task 起票成功時に tasks.md へ即時追記するか
    public string LlmLanguage { get; set; } = "English";       // LLM レスポンスの言語指定
    // Editor / Wiki フォントサイズ
    public int EditorFontSize { get; set; } = 14;
    public int MarkdownRenderFontSize { get; set; } = 13;
    // Editor / Wiki 文字色 (hex 文字列、空 = テーマデフォルト)
    public string EditorTextColor { get; set; } = "";
    public string MarkdownRenderTextColor { get; set; } = "";

    // Schedule / Outlook 連携
    public bool OutlookCalendarEnabled { get; set; } = false;

    // Schedule / ICS カレンダー連携
    public bool IcsCalendarEnabled { get; set; } = false;
    public string IcsCalendarUrl { get; set; } = "";
    // ICS イベントの除外ワードリスト (カンマ区切り、完全一致)
    public string IcsExcludeSubjects { get; set; } = "出社,在宅";
}
