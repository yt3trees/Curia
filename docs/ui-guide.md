# UI Guide

[< Back to README](../README.md)

## Table of Contents

- [Dashboard](#dashboard)
  - [AI Features on Dashboard](#ai-features-on-dashboard)
- [Editor](#editor)
  - [AI Features in Editor](#ai-features-in-editor)
- [Timeline](#timeline)
- [Git Repos](#git-repos)
- [Asana Sync](#asana-sync)
- [Wiki](#wiki)
- [Agent Hub](#agent-hub)
- [Setup - New Project](#setup---new-project)
- [Setup - Workstreams](#setup---workstreams)

## Dashboard

Overview of all projects with health indicators, update freshness, and Today Queue at the bottom.

![](../_assets/Dashboard.png)

![](../_assets/Dashboard-Card.png)

This is the standard project card view with health signals, repository status, and quick actions.

- Use the top bar to refresh the view, set auto refresh (Off / 10 / 15 / 30 / 60 min), and show hidden projects.
- Each project card gives a quick health check: project name, tier (FULL/MINI), optional DOMAIN tag, link status dots, decision log count, and uncommitted repo count.
- Click the uncommitted badge to see repository-by-repository change details.
- `Focus` and `Summary` show how old each file is (in days), and the background color changes as files get older.
- The 30-day mini activity bar is clickable and opens Timeline.
- Card buttons help you move straight into work: open folder, open terminal (or launch Claude/Gemini/Codex), open Editor, and pin work folders.
- Workstreams can be expanded per project. From each row, you can open `current_focus.md`, open the workstream `_work` folder (right-click to create today's work folder), or pin a recent folder. A Team View button appears on workstream rows (and on the project card) when Team View is configured; clicking it opens the Team View dialog.
- Team View dialog shows each team member's Asana tasks grouped by project, with due dates and overdue indicators (⚠). A Sync button fetches the latest tasks from Asana and updates `team-tasks.md`.
- `Pinned Folders` appears when you pin at least one folder. You can open, unpin, drag to reorder, or clear all pins.
- `Today Queue` reads unchecked tasks from `tasks.md` files and shows them by urgency (Overdue, Today, In Nd, No due).
- In each Today Queue row, you can open the task in Asana, snooze it until tomorrow, or mark it done in Asana.
- Today Queue also has project/workstream filters, Show All (Top 10 vs up to 100), unsnooze all, manual refresh, and fixed/resizable height mode.

### AI Features on Dashboard

<img src="../_assets/ai-feature/WhatsNext.png" width="60%" alt="What's Next dialog" />

When AI Features is enabled, the What's Next button in the top bar shows 3-5 prioritized actions across all projects, with an `Open` button for direct navigation and `Copy` for plain-text export.

<img src="../_assets/ai-feature/ContextBriefing.png" width="60%" alt="Context Briefing dialog" />

When AI Features is enabled, each project card also shows a Briefing button that generates a project-specific context-switch summary (Where you left off / Suggested next steps / Key context) with `Copy`, `Open in Editor`, and `View Debug`.

<img src="../_assets/ai-feature/TodaysPlan.png" width="60%" alt="Today's Plan dialog" />

Today's Plan dialog (AI) provides a time-blocked day plan (for example, Morning / Afternoon), with `Open`, `Copy`, `Save`, and `View Debug` actions.

## Editor

Tree-based file browser for AI context files (`current_focus.md`, `decision_log`, etc.) with syntax-highlighted Markdown editing.

![](../_assets/Editor.png)

- Project selector dropdown at the top left to switch between projects
- Tree view on the left lists AI context files: `current_focus.md`, `file_map.md`, `project_summary.md`, `open_issues.md`, `decision_log/`, `focus_history/`, `obsidian_notes/`, `workstreams/`, `CLAUDE.md`, `AGENTS.md`
- Syntax-highlighted Markdown editor on the right with section-based coloring
- Toolbar buttons: Refresh, Dec Log (quick decision log entry), P (pin folder), Save
- Full file path displayed in the header bar
- Status bar at the bottom shows the current project and file name

### AI Features in Editor

<img src="../_assets/ai-feature/UpdateFocusFromAsana.png" width="60%" alt="Update Focus dialog" />

Update Focus (AI) reads `tasks.md` and recent activity signals (pinned folder files, dated work folders, git activity, quick captures), sends context to the configured LLM, and shows a diff-based proposal dialog; it supports workstream filtering, natural-language refinement, and `View Debug`, and saves a backup to `focus_history/`.

<img src="../_assets/ai-feature/AI-DecisionLog_1.png" width="60%" alt="AI Decision Log dialog step 1" />
<img src="../_assets/ai-feature/AI-DecisionLog_2.png" width="60%" alt="AI Decision Log dialog step 2" />

AI Decision Log (Dec Log in AI mode) detects implicit decisions from recent `focus_history`, accepts decision metadata (Status/Trigger/attachments), generates a structured draft (Options / Why / Risk / Revisit Trigger), supports refinement and debug view, and saves to `decision_log/YYYY-MM-DD_{topic}.md`.

<img src="../_assets/ai-feature/ImportMeetingNotes_1.png" width="60%" alt="Import Meeting Notes dialog step 1" />
<img src="../_assets/ai-feature/ImportMeetingNotes_2.png" width="60%" alt="Import Meeting Notes dialog step 2" />

Import Meeting Notes (AI) analyzes raw notes in a single pass and previews Decisions / Focus / Tensions / Asana Tasks tabs; you can choose what to apply, inspect prompt/response via `View Debug`, and `current_focus.md` is backed up before overwrite.

## Timeline

Chronological view of project activity filtered by project and time period.

![](../_assets/Timeline.png)

- Project dropdown to filter by a specific project (e.g. GenAi [Domain])
- Period dropdown to set the time range (e.g. 30 days)
- Graph scope selector to choose between single project and all projects
- Entries tab shows a list of timeline entries ([Focus], [Decision], [Work]) with dates (including day of week); [Work] entries come from `shared/_work/` date folders (e.g. `20260321_fix-login-bug`) and clicking them opens the folder in Explorer
- Graph tab visualizes activity trends over the selected period; Work folder events are counted alongside Focus and Decision entries

## Git Repos

Scans workspace roots and lists repositories with remote URLs, branches, and last commit dates.

![](../_assets/GitRepos.png)

- Project dropdown to filter repositories by project
- Scan button to trigger a recursive repository search under workspace roots
- Save to Cloud / Load from Cloud buttons to back up or restore clone metadata
- Copy Clone Script button generates a shell script to re-clone all listed repositories
- Table columns: Project, Repository, Remote URL, Branch, Last Commit date

## Asana Sync

Configure per-project Asana sync with scheduling, workstream mapping, and section filters.

See [Asana Setup](asana-setup.md) for full setup steps and page reference.

## Wiki

A per-project knowledge base that the LLM builds and maintains incrementally. Import meeting notes, design documents, or any text source, and the LLM auto-generates summary, entity, and concept pages with cross-links.

### How to Create a Wiki

1. Go to the Wiki tab and select a project from the dropdown at the top right
2. Enter a Domain (e.g., ERP / Web / Infra) and click Initialize Wiki
3. The directory structure is created under `_ai-context/context/wiki/`

Once a Wiki exists, three sub-views become available.

### Pages

Left pane shows a category-based page tree; right pane shows the selected page's Markdown. Use the search bar to filter by title or path, and click "Open in Editor" to edit directly.

Pages are organized into four categories (sources, entities, concepts, analysis). For category details, see [Wiki Features](wiki-features.md).

#### Import Source

Click "+ Import Source" at the bottom or drag and drop a file onto the Wiki tab. Supported formats: `.md` / `.txt` / `.pdf` / `.docx`.

When AI Features is enabled, the LLM generates new and updated pages; each change is shown in a diff review dialog before saving. For the full import flow and prompt details, see [Wiki Features](wiki-features.md).

### Query (requires AI Features)

Ask questions about the Wiki in natural language.

1. Type your question and click Ask (or press Enter)
2. The LLM uses `index.md` to find relevant pages and generates an answer
3. Review the answer and referenced pages, then optionally click "Save as Wiki Page" to save to `pages/analysis/`
4. Past Q&A sessions appear in the History panel in chronological order

### Lint

Validate Wiki consistency using static checks and LLM analysis. Click "Run Lint" to detect broken links, orphan pages, stale content, contradictions, and missing topics. LLM checks (Contradiction / Missing) run only when AI Features is enabled.

For the full check list and prompt details, see [Wiki Features](wiki-features.md#lint).

## Agent Hub

Manage reusable agent/rule definitions and deploy them per project and per CLI from one page.

![](../_assets/AgentHub.png)

See [AI Agent Collaboration](ai-agent-collaboration.md#agent-hub-multi-cli-deployment) for full details on the library, deployment matrix, and AI Builder.

## Setup - New Project

Create new projects, check existing structures, archive, and convert tiers from a single page.

![](../_assets/Setup-NewProject.png)

New Project tab:

- Project Name: select an existing project to auto-fill Tier/Category, add ExternalSharePath, or run AI Context Setup on it
- Tier: `full (standard)` or `mini`
- Category: `project (time-bound)` or `domain`
- ExternalSharePath (optional): custom path per files for shared data
- Also run AI Context Setup: when checked, junctions for `_ai-context/context/` and `_ai-context/obsidian_notes/` are created automatically
- Overwrite existing skills (-Force): re-deploys `.claude/skills/`, `.codex/skills/`, `.gemini/skills/`, and `.github/skills/` even if they already exist
- Setup Project button creates the folder structure, junctions, and skill files
- Output area shows the log of operations performed

Check tab:

- Validates an existing project's folder structure, junctions, and skill files
- Reports missing or broken items so you can fix them

Archive tab:

- Moves a project to an archive location and cleans up junctions

Convert Tier tab:

- Converts a project between `full` and `mini` tiers, adjusting folder structure accordingly

## Setup - Workstreams

Manage workstreams within a project: create, rename labels, and close/reopen.

![](../_assets/Setup-Workstreams.png)

- Project selector dropdown with Reload button
- Add Workstream section: enter a Workstream ID (kebab-case), an optional label, and an optional display label, then click Create Workstream
- Existing Workstreams list shows each workstream's ID, label, and status (Active / Closed)
- Close button marks a workstream as Closed; Reopen restores it to Active
- Save Labels persists any label changes
- Output area shows the log of operations performed

