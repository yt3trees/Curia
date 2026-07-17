# AI Features

[< Back to README](../README.md)

All AI features require `Enable AI Features` to be on (Settings > LLM API). Supported providers: OpenAI, Azure OpenAI, Claude Code CLI, Gemini CLI, Codex CLI, and GitHub Copilot CLI.

<a id="ai-features-overview"></a>
## AI Features Overview

The AI features in Curia are categorized into three main situations: Planning (Dashboard), Updating/Logging (Editor), and Capturing thoughts (Global).

```mermaid
flowchart LR
    %% Base
    Profile>👤 User Profile<br>Sets tone & context everywhere]

    %% Categories
    subgraph Dashboard ["📊 Overviews & Planning (Dashboard)"]
        direction TB
        WN["💡 What's Next<br>Suggests next actions"]
        CB["📋 Context Briefing<br>Resume work seamlessly"]
        TP["📅 Today's Plan<br>Time-blocked day plan"]
    end

    subgraph Editor ["📝 Updating & Logging (Editor)"]
        direction TB
        UF["🎯 Update Focus<br>Sync focus from Asana + recent activity"]
        DL["⚖️ AI Decision Log<br>Appends structured logs"]
        IM["👥 Import Meeting Notes<br>Auto-extract tasks & decisions"]
    end

    subgraph Global ["⚡ Fleeting Thoughts (Global)"]
        direction TB
        QC["🪟 Quick Capture<br>Ctrl+Shift+C<br>AI-routed inbox"]
    end

    subgraph Wiki ["📚 Knowledge Base (Wiki)"]
        direction TB
        WI["📥 Import<br>Import source, generate summary page"]
        WQ["💬 Query<br>Ask questions against the Wiki"]
        WL["🔍 Lint<br>Detect contradictions & orphan pages"]
    end

    subgraph CommandPalette ["🔍 Cross-Project Search (Command Palette)"]
        direction TB
        AC["❓ Ask Curia<br>? prefix — natural language Q&A<br>across all project data"]
    end

    Profile -.-> Dashboard
    Profile -.-> Editor
    Profile -.-> Global
    Profile -.-> Wiki
    Profile -.-> CommandPalette
```

<a id="setup"></a>
## Setup

### API-based providers (OpenAI / Azure OpenAI)

1. Open `Settings > LLM API`
2. Choose `OpenAI` or `Azure OpenAI`, enter API Key and Model (Azure also needs Endpoint / API Version)
3. Click `Test Connection`
4. Once the test passes, toggle `Enable AI Features` on and save

### CLI-based providers (Claude Code CLI / Gemini CLI / Codex CLI)

These providers delegate authentication to the CLI tool itself — no API Key is required in Curia settings.

| Provider | CLI tool | Auth setup |
|---|---|---|
| Claude Code CLI | `claude` | Run `claude` once to authenticate |
| Gemini CLI | `gemini` | Run `gemini auth login` |
| Codex CLI | `codex` | Set `OPENAI_API_KEY` env var, or enter the key in Settings to have Curia pass it automatically |
| GitHub Copilot CLI | `copilot` | Sign in via `gh auth login` with Copilot access |

1. Install and authenticate the CLI tool (see table above)
2. Open `Settings > LLM API`, choose the CLI provider, and optionally set Model
3. Click `Test Connection`
4. Once the test passes, toggle `Enable AI Features` on and save

The API Key field remains visible when switching to a CLI provider so you can switch back to an API-based provider at any time without re-entering the key.

### CLI invocation details

Curia runs each CLI tool as a subprocess via `cmd.exe /c` on Windows (so `.cmd` scripts are resolved correctly). The prompt text is formatted as a single block combining the system prompt and conversation history before being passed to the CLI.

Model selection is not supported for CLI-based providers — each CLI uses its own default model.

#### Claude Code CLI

```
claude --print --output-format json
```

- Prompt is passed via **stdin**
- `--print` enables non-interactive (headless) mode
- `--output-format json` returns structured JSON; Curia extracts the `result` field

#### Gemini CLI

```
gemini --prompt "TEXT" --yolo --output-format text
```

- Prompt is passed as the `--prompt` argument value (not stdin)
- `--yolo` auto-approves all tool actions (required to prevent interactive approval prompts)
- `--output-format text` returns plain text output

#### Codex CLI

```
codex exec --skip-git-repo-check --json
```

- Prompt is passed via **stdin** (same as Claude Code CLI)
- `--skip-git-repo-check` allows execution outside a Git repository
- `--json` enables NDJSON structured output; Curia extracts `item.completed` / `agent_message` entries
- If API Key is set in Settings, it is passed as the `OPENAI_API_KEY` environment variable
- stdin is used instead of a positional argument to avoid Windows command-line length limits on large prompts

#### GitHub Copilot CLI

```
copilot -p "TEXT"
```

- Prompt is passed as the `-p` argument value
- Output is plain text; no additional parsing needed

<a id="user-profile"></a>
## User Profile

Enter a free-text description of your role, priorities, and working style in `Settings > LLM API > User Profile`. This text is prepended as a `## User Profile` section to the system prompt of every LLM call, so the model has your context without repeating it in each prompt.

Example:

```
Role: Engineering manager. I work across 3-4 parallel projects.
Prefer concise bullet points. Flag overloaded days rather than packing in tasks.
Language: respond in Japanese unless the document is already in English.
```

<a id="ask-curia"></a>
## Ask Curia (Command Palette)

<a id="ask-curia-command-palette"></a>
### Ask Curia

Press `Ctrl+K` to open the Command Palette, then type `?` followed by a natural-language question. Curia searches across all AI context files in every managed project and returns an answer with cited sources.

```
?What database approach did we decide on for the Alpha project?
?What was Bさん asked to handle?
?What did we discuss about the migration last month?
```

**What is searched**

| Source | Location |
|---|---|
| Decision Log | `_ai-context/decision_log/**/*.md` |
| Focus History | `_ai-context/context/focus_history/*.md` |
| Meeting Notes | `_ai-context/meeting_notes/**/*.md` |
| Tasks | `_ai-context/obsidian_notes/tasks.md` (per task) |

**How it works**

The query runs in two LLM stages. Stage 1 scans metadata (title + first 500 characters) from up to 300 candidate files across all projects and selects the 8 most relevant. Stage 2 sends the full content of those files and generates a cited answer.

**Answer panel**

- Inline citation references (e.g. `[focus_history/2026-04-11.md:L4]`) are shortened to just the filename for readability.
- The Sources section at the bottom of each answer lists the explicitly cited files. Click any source to open it directly in the Editor.
- If the file cannot be matched to a project, the path is copied to the clipboard instead.

**Multi-turn conversation**

After receiving an answer, the search box resets to `?` so you can ask a follow-up question immediately. Previous Q&A turns are shown in a scrollable conversation panel and their content is passed to the LLM as context.

- The conversation persists if you click outside the palette and reopen it.
- Click **New** in the conversation header to start a fresh session.
- Press `Escape` to close the palette (conversation is preserved).
- Press `Escape` while a query is in progress to cancel it.

**Requirements**

- `Enable AI Features` must be on.
- Projects must be discovered by Curia (visible in Dashboard).

<a id="global"></a>
## Global

<a id="quick-capture-global-hotkey"></a>
### Quick Capture (Global Hotkey)

Press `Ctrl+Shift+C` from anywhere on your desktop to open a lightweight capture window. Type a free-text note and press Enter. If AI Features is enabled, an LLM classifies the input and routes it automatically:

| Category | Destination |
|---|---|
| `task` | Creates a task in Asana via API (requires confirmation before submitting) |
| `tension` | Appends to the project's `open_issues.md` |
| `focus_update` | Opens Editor and triggers the Update Focus flow with your input as additional context |
| `decision` | Opens Editor and launches the AI Decision Log flow |
| `memo` | Appends a timestamped entry to `_config/capture_log.md` |

When AI Features is disabled, you can still use Quick Capture by selecting the category and project manually.

<a id="dashboard"></a>
## Dashboard

<a id="whats-next-dashboard"></a>
### What's Next

Click the lightbulb icon in the Dashboard toolbar to get 3-5 AI-prioritized action suggestions across all projects. The model analyzes overdue tasks, stale focus files, uncommitted changes, and unrecorded decisions, then ranks actions by urgency. Each suggestion has an Open button to navigate directly to the relevant file.

<img src="../_assets/ai-feature/WhatsNext.png" width="70%" alt="What's Next dialog" />

<a id="context-briefing-dashboard-card"></a>
### Context Briefing (Dashboard Card)

Click the lightbulb icon on a project card to generate a project-specific resume briefing. The model reads `current_focus.md`, recent `decision_log` entries, `open_issues.md`, active/completed Asana tasks, and uncommitted repo signals, then outputs:

- `Where you left off` (short narrative summary)
- `Suggested next steps` (prioritized action list)
- `Key context` (compact facts to re-enter work quickly)

The dialog supports `Copy`, `Open in Editor`, and `View Debug` (prompt/response inspection).

<img src="../_assets/ai-feature/ContextBriefing.png" width="70%" alt="Context Briefing dialog" />

<a id="todays-plan-dashboard"></a>
### Today's Plan

Today's Plan dialog provides a time-blocked day plan (for example, Morning / Afternoon), with `Open`, `Copy`, `Save`, and `View Debug` actions.

<img src="../_assets/ai-feature/TodaysPlan.png" width="70%" alt="Today's Plan dialog" />

<a id="editor"></a>
## Editor

<a id="update-focus-from-asana-editor"></a>
### Update Focus

Click the `Update Focus` button in the Editor toolbar to generate a diff-based update proposal for the open `current_focus.md`. The model reads Asana task data, recent activity signals (pinned folder files, dated work folders, git activity, quick captures), and the existing file, then proposes changes while preserving your heading structure and writing style. A backup is saved to `focus_history/` automatically. Supports workstream filtering, natural-language refinement, and a debug view.

<img src="../_assets/ai-feature/UpdateFocusFromAsana.png" width="70%" alt="Update Focus dialog" />

<a id="ai-decision-log-editor"></a>
### AI Decision Log

Click `Dec Log` in the Editor toolbar (AI mode) to open the decision log assistant. Describe what was decided; the model generates a structured draft with Options / Why / Risk / Revisit Trigger sections. Supports natural-language refinement and optionally removes the resolved item from `open_issues.md`. Saves as `decision_log/YYYY-MM-DD_{topic}.md`.

<img src="../_assets/ai-feature/AI-DecisionLog_1.png" width="70%" alt="AI Decision Log dialog step 1" />
<img src="../_assets/ai-feature/AI-DecisionLog_2.png" width="70%" alt="AI Decision Log dialog step 2" />

<a id="import-meeting-notes-editor"></a>
### Import Meeting Notes

Click the `Import Meeting Notes` button in the Editor toolbar (or press `Ctrl+Enter` in the notes input dialog) to paste raw meeting notes and have the LLM analyze them in a single pass. The model produces four types of output, each shown in a separate tab of the preview dialog:

- Decisions tab: one checkbox per decision detected; click "Show draft" to preview the structured `decision_log` draft; uncheck any decision to skip it
- Focus tab: diff-based view of the AI-generated update to `current_focus.md`; the LLM rewrites the full file integrating new items while preserving the existing structure and writing style
- Tensions tab: preview of items to be appended to `open_issues.md` (technical questions, tradeoffs, concerns)
- Asana Tasks tab: list of action items proposed for Asana. Each task has its own controls:
  - Project ComboBox: defaults to the first entry in `personal_project_gids` (`asana_global.json`), or the workstream's mapped project if configured
  - Section ComboBox: auto-selected by matching `anken_aliases` (from `asana_config.json`) against the section name; falls back to `(none)`
  - Due Date picker (optional)
  - Set time checkbox: when checked, reveals Hour / Minute selectors (15-minute increments); produces an ISO 8601 `due_at` value with local timezone offset
  - Check each task to include; uncheck to skip

Select which items to apply and click `Apply Selected`. A `View Debug` button in the dialog shows the full LLM prompt and response. Decision logs are saved as `YYYY-MM-DD_{topic}.md`; `current_focus.md` is backed up to `focus_history/` before updating. Created Asana tasks are appended to `tasks.md` with their GID and due date.

<img src="../_assets/ai-feature/ImportMeetingNotes_1.png" width="70%" alt="Import Meeting Notes dialog step 1" />
<img src="../_assets/ai-feature/ImportMeetingNotes_2.png" width="70%" alt="Import Meeting Notes dialog step 2" />

<a id="wiki"></a>
## Wiki

Wiki documentation has been moved to [Wiki Features](wiki-features.md) to keep this page focused and easier to scan.

