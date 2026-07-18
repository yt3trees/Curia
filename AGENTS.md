# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Curia is a Windows desktop app (WPF + .NET 9) for managing multiple parallel projects from a system tray. Users toggle visibility with a global hotkey (Ctrl+Shift+P); normal window-close hides the app, Shift+Close exits it.

## Build & Publish

```bash
# Quick type check after changes (recommended — faster than full publish)
dotnet build Curia.csproj 2>&1 | tail -5

# Full build
dotnet build

# Publish single-file executable to bin\Publish\
# Always use this for final verification (not dotnet build alone)
dotnet publish -p:PublishProfile=SingleFile
```

Run the publish command from the repository root. The `publish.cmd` wrapper uses `pause`, so invoke dotnet directly in non-interactive shells.

There are no automated tests in this repository.

## Safety & Permissions

Allowed without prompting:
- Read any file in the repository
- Edit source files (.cs, .xaml, .json, .md)
- Run `dotnet build` or `dotnet publish -p:PublishProfile=SingleFile`

Ask the user first:
- Any `git` operation (commit, push, branch deletion, reset)
- Installing or upgrading NuGet packages
- Deleting files
- Modifying files outside the repository root

Requirements: Windows, .NET 9 SDK, Git CLI, PowerShell 7+. Python 3.10+ is optional (Asana sync).

## Key Entry Points

- `App.xaml.cs` — DI container registration; add new services/ViewModels/Pages here
- `MainWindow.xaml.cs` — page navigation control and window visibility logic
- `Views/Pages/` — DashboardPage, EditorPage, TimelinePage, SettingsPage
- `Models/AppConfig.cs` — settings model; add new settings fields here
- `_config/*.example` — config file templates

## Architecture

MVVM + Dependency Injection (`Microsoft.Extensions.DependencyInjection`). All services, ViewModels, and Pages are registered as singletons in `App.xaml.cs`.

### Key Layers

- `Models/` - Data structures (ProjectInfo, WorkstreamInfo, AppConfig, etc.)
- `Services/` - Business logic (singleton services injected into ViewModels)
- `ViewModels/` - CommunityToolkit.Mvvm with `[ObservableProperty]` / `[RelayCommand]`
- `Views/Pages/` - XAML pages; navigation controlled by `MainWindow.xaml.cs`

### Core Services

| Service | Responsibility |
|---|---|
| ConfigService | JSON config at `%USERPROFILE%\.curia\` (new default) or `Documents\Projects\_config\` (legacy, auto-detected); override via `CURIA_CONFIG_DIR` env var. Files: settings.json, asana_global.json, hidden_projects.json, pinned_folders.json |
| ProjectDiscoveryService | Recursively scans Local and Box project roots; 5-minute TTL cache; detects junction status, focus age, decision log count, uncommitted git changes |
| TodayQueueService | Reads/prioritizes tasks from `tasks.md` into overdue/today/soon/normal buckets |
| AsanaSyncService | Syncs Asana API tasks to Markdown files; maps workstreams via asana_config.json per project |
| ContextCompressionLayerService | Manages AI context files (current_focus.md, decision_log, project_summary.md); extracts embedded skills from assembly to disk on first run |
| StandupGeneratorService | Generates daily standup Markdown on startup and hourly (6am+) |
| HotkeyService | Registers global hotkey (Win32 P/Invoke); re-registers on config change without restart |
| TrayService | System tray icon via Windows Forms `NotifyIcon` |
| FileEncodingService | Async file I/O with BOM-based encoding detection (UTF-8/UTF-8BOM/SJIS/UTF-16); preserves encoding on write |
| ScriptRunnerService | Runs PowerShell/Python scripts async; dispatches output to UI thread; cancellable |
| LlmClientService | Sends chat completion requests to OpenAI or Azure OpenAI; reads provider/key/model/endpoint from settings; supports Test Connection |
| AsanaTaskParser | Parses `tasks.md` into structured task lists for use in LLM prompts |
| FocusUpdateService | Orchestrates the "Update Focus" flow: loads context files (Asana tasks plus activity signals from FocusSignalCollectorService), builds prompt, calls LlmClientService, saves backup to `focus_history/`, and returns proposed diff |
| FocusSignalCollectorService | Collects local-only activity signals for Update Focus: pinned folder file activity, dated `shared/_work` folders, git commits/uncommitted files, and matching `capture_log.md` entries within a lookback window |
| ProposalInboxService | Persistent inbox for background-generated AI proposals at `[config_dir]\proposals\*.json` (archive in `_archive\`, 30-day retention); tracks Pending count, expires proposals whose target file was manually edited, broadcasts `ProposalInboxChangedMessage` |
| ProposalSchedulerService | Timer-based background generation of Focus update proposals: selects projects whose activity signals are newer than current_focus.md, honors per-day generation cap and pending-duplicate guard, feeds results into ProposalInboxService |
| DecisionLogService | Reads and parses decision log Markdown files from `_ai-context/decision_log/` (and per-workstream subdirs) |
| DecisionLogGeneratorService | AI-driven decision log generation: detects candidates from focus_history diff, generates draft, refines via LLM in three separate async steps |
| CaptureService | Global Capture orchestration: AI-classifies freeform input, creates Asana tasks (with dedup guard), appends to project files |
| MeetingNotesService | Parses meeting notes and propagates extracted decisions/actions to decision_log, current_focus.md, open_issues.md via LLM |
| TeamTaskParser | Parses `team-tasks.md` into `TeamMemberCard` list (per-member task rows with due dates, project tags, Asana GIDs) |
| AgentHubService | Manages agent definitions, rules, and skills in `agent_hub/` under the config dir; extracts built-in skills from the assembly on first run |
| AgentDeploymentService | Deploys/undeploys agent definition files to project or global target directories for Claude Code and other CLI tools |
| StateSnapshotService | Exports current project and task state to `curator_state.json` in the config dir for external consumption |

### WPF-Specific Patterns

- Window visibility: the app moves the window to (-32000, -32000) instead of `Hide()` to avoid DWM re-initialization flashing. A FlashBlocker Grid hides partial redraws during movement. See `Win32Interop.cs` and `MainWindow.xaml.cs`.
- Page navigation uses wpf-ui 3.x `INavigationService`. Cross-page navigation (e.g., Dashboard → Editor with a specific file) is done via callbacks set on ViewModels (`OnOpenInEditor`, `OnOpenInTimeline`) rather than direct service calls.
- Markdown editing uses AvalonEdit with `Assets/Markdown.xshd` for syntax highlighting. EditorPage also has a diff view mode (DiffPlex) toggled via `IsDiffViewActive`; `DiffLineBackgroundRenderer` highlights changed lines against the original file content.
- `GlobalUsings.cs` aliases WPF `Application` over WinForms to resolve namespace conflicts.
- Cross-ViewModel communication uses CommunityToolkit.Mvvm `WeakReferenceMessenger`. `StatusUpdateMessage` broadcasts editor state (project, file, encoding, dirty flag) from `EditorViewModel` to `MainWindowViewModel` for status bar updates. `AiEnabledChangedMessage` is sent by `SettingsViewModel` when the "Enable AI Features" toggle changes, and received by `EditorViewModel` to show or hide the "Update Focus" toolbar button.

### Configuration

Runtime config is loaded from `%USERPROFILE%\.curia\` (new default) or `%USERPROFILE%\Documents\Projects\_config\` (legacy, auto-detected). See `_config/` directory for `.example` templates:
- `settings.json` - workspace roots, hotkey, auto-refresh, Asana sync settings, and LLM/AI settings (`LlmProvider`, `LlmApiKey`, `LlmModel`, `LlmEndpoint`, `LlmApiVersion`, `AiEnabled`)
- `asana_global.json` - Asana token, workspace/user GIDs, personal project GIDs

LLM settings are configured in `Settings > LLM API`. Supported providers are `openai`, `azure_openai`, `claude_code` (Claude Code CLI), `gemini_cli` (Gemini CLI), `codex_cli` (Codex CLI), and `github_copilot` (GitHub Copilot CLI). CLI-based providers delegate authentication to the CLI tool itself; the API Key field is retained in settings for easy switching back to an API-based provider. "Enable AI Features" can only be turned on after a successful Test Connection.

### Managed Folder Layout

```
Local Projects Root/
  MyProject/
    development/source/     # local-only repos
    shared/  (junction)     # → [cloudSyncRoot]/MyProject/
    _ai-context/
      context/ (junction)   # → [obsidianVaultRoot]/Projects/MyProject/ai-context/
```

Projects are classified by tier (`full`/`mini`) and category (`project`/`domain`), detected from path conventions.

## AI Features

New AI-powered features must follow these patterns.

### Gating

- Never call `LlmClientService` unless `settings.AiEnabled` is `true`.
- In each ViewModel that exposes an AI action, declare an `[ObservableProperty] bool isAiEnabled` and initialize it from settings. Bind UI button/control visibility to this property.
- React to real-time toggle changes by registering for `AiEnabledChangedMessage` in the ViewModel constructor:

```csharp
IsAiEnabled = _configService.LoadSettings().AiEnabled;
WeakReferenceMessenger.Default.Register<AiEnabledChangedMessage>(this,
    (_, msg) => IsAiEnabled = msg.Enabled);
```

### LLM Calls

- All LLM API calls go through `LlmClientService` — never make direct HTTP calls to LLM providers.
- Use `ChatCompletionAsync` for single-turn (system + user); use `ChatWithHistoryAsync` for multi-turn with conversation history.
- `LlmClientService` throws `InvalidOperationException` if the API key is not configured — callers do not need to re-check the key themselves.
- Services orchestrating LLM workflows (e.g., `FocusUpdateService`) must accept a `CancellationToken` and pass it through to `LlmClientService`.

```csharp
// Single-turn (system + user)
string result = await _llmClient.ChatCompletionAsync(systemPrompt, userPrompt, cancellationToken);

// Multi-turn with conversation history
var messages = new List<ChatMessage>
{
    new(ChatRole.System, systemPrompt),
    new(ChatRole.User, userTurn1),
    new(ChatRole.Assistant, assistantTurn1),
    new(ChatRole.User, userTurn2),
};
string result = await _llmClient.ChatWithHistoryAsync(messages, cancellationToken);
```

### Settings

- `AiEnabled` can only be set to `true` after a successful `TestLlmConnectionAsync`. Enforce this by binding the toggle's `IsEnabled` to `AiToggleCanEnable` (see `SettingsViewModel` pattern).
- When `AiEnabled` changes, persist immediately to settings and broadcast `AiEnabledChangedMessage` via `WeakReferenceMessenger` (see `SettingsViewModel.OnAiEnabledChanged`).

## Popup / Dialog Windows

When adding modal dialogs, follow the patterns in `DashboardPage.xaml.cs` (canonical reference). See `.codex/skills/projectcurator-popup-window/SKILL.md` for detailed guidelines: theme resources, dark-mode support, WPF control conventions, and Win32 interop guardrails. Use English-only text in UI elements.

Existing dialog/window implementations for reference:
- `Views\ProposalReviewDialog.cs` - diff review with Accept/Reject (used by FocusUpdateService flow)
- `Views\CaptureWindow.cs` - frameless floating capture input window
- `Views\DecisionLogViewerDialog.cs` - scrollable decision log list/detail dialog
- `Views\TeamViewDialog.cs` - team member task cards dialog

### WPF Control Best Practices

- ComboBox: do not set an explicit `Height`. Use `Padding` instead (e.g., `Padding = new Thickness(6, 4, 4, 4)`) and let the control auto-size. A fixed height causes the text to be clipped at the bottom.
- `ItemsControl`/`ListBox`: do not set both `DisplayMemberPath` and `ItemTemplate` at the same time. WPF throws `InvalidOperationException` (`DisplayMemberPath and ItemTemplate cannot both be set`). Use only one:
  - simple text row: `DisplayMemberPath`
  - custom row layout (trimming/wrap/icon/multi-column): `ItemTemplate`
- `ListBox` in themed dialogs: avoid default OS selection visuals. Define `ItemContainerStyle` (or override selection brushes) so selected/hover states use app theme brushes (`AppSurface*`, `AppText`) for consistent dark-mode appearance.
- Code-behind dialog windows with `SizeToContent = SizeToContent.Height`: always set `MinHeight = 0` explicitly. The wpf-ui `ControlsDictionary` applies a `MinHeight` via style setters to `Window`, which takes precedence over `SizeToContent` and makes the window far taller than its content. A local value (`MinHeight = 0`) overrides the style setter and lets `SizeToContent` size the window correctly.
- `WindowChrome` on `NoResize` dialogs: do NOT apply `WindowChrome` when `ResizeMode = ResizeMode.NoResize`. `WindowChrome` is only required for `ResizeMode.CanResize` + `WindowStyle.None` (to remove the DWM white resize border). Applying it to a `NoResize` dialog interferes with `SizeToContent` and causes the window to render with excess height.
- Frameless resizable windows: when using `WindowStyle = WindowStyle.None` with `ResizeMode = ResizeMode.CanResize`, the DWM adds a white resize border. Apply `WindowChrome` immediately after window creation to remove it while keeping resize functionality:

```csharp
System.Windows.Shell.WindowChrome.SetWindowChrome(dialog,
    new System.Windows.Shell.WindowChrome
    {
        CaptionHeight         = 0,
        ResizeBorderThickness = new Thickness(4),
        GlassFrameThickness   = new Thickness(0),
        UseAeroCaptionButtons = false
    });
```

## NuGet Dependencies

- `wpf-ui` 3.x - Fluent Design controls and navigation
- `AvalonEdit` 6.x - Syntax-highlighted Markdown editor
- `CommunityToolkit.Mvvm` 8.x - MVVM source generators
- `Microsoft.Extensions.DependencyInjection` 9.x - DI container
- `DiffPlex` - Line-level diff computation for Editor diff view mode
