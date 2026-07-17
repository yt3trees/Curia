using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Curia.Models;

namespace Curia.Services;

/// <summary>
/// Asana タスクを解析して current_focus.md の更新提案を生成する。
/// SKILL.md (update-focus-from-asana) の Step 1-6 に相当するオーケストレーション。
/// </summary>
public class FocusUpdateService
{
    private readonly LlmClientService _llm;
    private readonly AsanaTaskParser  _parser;
    private readonly FileEncodingService _encoding;

    public FocusUpdateService(
        LlmClientService llm,
        AsanaTaskParser parser,
        FileEncodingService encoding)
    {
        _llm      = llm;
        _parser   = parser;
        _encoding = encoding;
    }

    // -----------------------------------------------------------------------
    /// <summary>
    /// 更新提案を生成する。
    /// workstreamId が null または空文字 → general モード
    /// workstreamId が指定されている → workstream モード
    /// </summary>
    public async Task<FocusUpdateResult> GenerateProposalAsync(
        ProjectInfo project,
        string? workstreamId,
        CancellationToken ct = default,
        string? capturedContext = null,
        FocusActivitySignals? signals = null)
    {
        // Step 1: パス解決
        var (focusPath, asanaPath, workMode, resolvedWsId) =
            ResolvePaths(project, workstreamId);

        // Step 2: ファイル存在チェック
        if (!File.Exists(focusPath))
            throw new FileNotFoundException(
                $"current_focus.md not found: {focusPath}\n" +
                "Please set up the Context Compression Layer first.");

        // Step 3: バックアップ
        var (backupPath, backupStatus) = await CreateBackupAsync(focusPath);

        // Step 4: Asana タスク解析 (ファイルが存在しない場合は空として扱う)
        var asanaTasks = File.Exists(asanaPath)
            ? _parser.ParseFile(asanaPath)
            : new AsanaTaskParseResult();

        // Whole project モード: アクティブな各 workstream のタスクも収集
        var obsidianNotes = Path.Combine(project.AiContextPath, "obsidian_notes");
        var workstreamTasks = new List<(string id, string label, AsanaTaskParseResult tasks)>();
        if (workMode == WorkMode.General)
        {
            foreach (var ws in project.Workstreams.Where(w => !w.IsClosed))
            {
                var wsAsanaPath = Path.Combine(obsidianNotes, "workstreams", ws.Id, "tasks.md");
                if (File.Exists(wsAsanaPath))
                    workstreamTasks.Add((ws.Id, ws.Label, _parser.ParseFile(wsAsanaPath)));
            }
        }

        // Step 5: current_focus.md 読み込み
        var (currentContent, _) = await _encoding.ReadFileAsync(focusPath);

        // project_summary.md (任意)
        var summaryPath = Path.Combine(project.AiContextContentPath, "project_summary.md");
        string? projectSummary = null;
        if (File.Exists(summaryPath))
        {
            var (summaryContent, _) = await _encoding.ReadFileAsync(summaryPath);
            projectSummary = summaryContent;
        }

        // Step 6: LLM に更新提案を生成させる
        var systemPrompt = BuildSystemPrompt(signals);
        var userPrompt   = BuildUserPrompt(project.Name, workstreamId, currentContent, asanaTasks, projectSummary, workstreamTasks, capturedContext, signals);
        var proposed     = await _llm.ChatCompletionAsync(systemPrompt, userPrompt, ct);

        // サマリ生成
        var summary = BuildSummary(asanaTasks, workMode, resolvedWsId);

        return new FocusUpdateResult
        {
            CurrentContent  = currentContent,
            ProposedContent = proposed.Trim(),
            BackupPath      = backupPath,
            BackupStatus    = backupStatus,
            TargetFocusPath = focusPath,
            WorkMode        = workMode,
            WorkstreamId    = resolvedWsId,
            Summary         = summary,
            DebugSystemPrompt = _llm.LastSystemPrompt,
            DebugUserPrompt   = _llm.LastUserPrompt,
            DebugResponse     = _llm.LastResponse,
        };
    }

    /// <summary>レビュー済みの提案を適用し、当日の focus_history スナップショットを保存する。</summary>
    public async Task ApplyProposalAsync(FocusUpdateResult result, string content, CancellationToken ct = default)
    {
        var finalContent = UpdateDateLine(content);
        await _encoding.WriteFileAsync(result.TargetFocusPath, finalContent, "UTF8", ct);

        var historyDirectory = Path.Combine(Path.GetDirectoryName(result.TargetFocusPath)!, "focus_history");
        Directory.CreateDirectory(historyDirectory);
        var snapshotPath = Path.Combine(historyDirectory, $"{DateTime.Now:yyyy-MM-dd}.md");
        await _encoding.WriteFileAsync(snapshotPath, finalContent, "UTF8", ct);
    }

    private static string UpdateDateLine(string content)
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var dateLine = new Regex(@"(?m)^(更新:\s*|Last Updated:\s*)\d{4}-\d{2}-\d{2}.*$");
        return dateLine.IsMatch(content)
            ? dateLine.Replace(content, match => match.Groups[1].Value + today, 1)
            : content;
    }

    // -----------------------------------------------------------------------
    private static (string focusPath, string asanaPath, WorkMode workMode, string wsId)
        ResolvePaths(ProjectInfo project, string? workstreamId)
    {
        var aiCtxContent  = project.AiContextContentPath;
        var obsidianNotes = Path.Combine(project.AiContextPath, "obsidian_notes");

        if (!string.IsNullOrWhiteSpace(workstreamId))
        {
            // workstream モード
            var wsFocusPath  = Path.Combine(aiCtxContent, "workstreams", workstreamId, "current_focus.md");
            var wsAsanaPath  = Path.Combine(obsidianNotes, "workstreams", workstreamId, "tasks.md");

            // workstream の current_focus.md がなければ general にフォールバック
            var focusPath = File.Exists(wsFocusPath)
                ? wsFocusPath
                : Path.Combine(aiCtxContent, "current_focus.md");

            // workstream の tasks.md がなければ root にフォールバック
            var asanaPath = File.Exists(wsAsanaPath)
                ? wsAsanaPath
                : Path.Combine(obsidianNotes, "tasks.md");

            return (focusPath, asanaPath, WorkMode.SharedWork, workstreamId);
        }
        else
        {
            // general モード
            var focusPath = Path.Combine(aiCtxContent, "current_focus.md");
            var asanaPath = Path.Combine(obsidianNotes, "tasks.md");
            return (focusPath, asanaPath, WorkMode.General, "");
        }
    }

    private async Task<(string path, BackupStatus status)> CreateBackupAsync(string focusPath)
    {
        var dir     = Path.GetDirectoryName(focusPath)!;
        var histDir = Path.Combine(dir, "focus_history");
        Directory.CreateDirectory(histDir);

        var backupPath = Path.Combine(histDir, $"{DateTime.Now:yyyy-MM-dd}.md");
        if (File.Exists(backupPath))
            return (backupPath, BackupStatus.AlreadyExists);

        var (content, enc) = await _encoding.ReadFileAsync(focusPath);
        await _encoding.WriteFileAsync(backupPath, content, enc);
        return (backupPath, BackupStatus.Created);
    }

    // -----------------------------------------------------------------------
    // プロンプト設計
    // -----------------------------------------------------------------------
    private static string BuildSystemPrompt(FocusActivitySignals? signals = null)
    {
        var basePrompt = BaseSystemPrompt;
        if (signals == null || signals.IsEmpty) return basePrompt;

        return basePrompt + "\n\n" + """
        ## Using activity signals

        - Recent file activity, work folder names, git activity, and quick captures show what the user
          actually worked on. Use them to judge freshness and priority for "What I'm working on",
          alongside Asana task data.
        - Never assert what was done based on a file name or commit message alone. Only write about
          an activity when it clearly corresponds to an existing Asana task or an existing line in the
          document. If a signal is ambiguous and cannot be matched to something concrete, skip it rather
          than guessing.
        - Recent work folder names (under shared/_work, formatted as "yyyy-MM-dd [scope] feature-name")
          are a stronger signal than file names or commit messages, since the user typed the feature
          name themselves when creating the folder. Still rephrase into natural prose — do not paste
          the raw folder suffix (underscores/hyphens) verbatim.
        - If an activity is genuinely new information (not covered by Asana tasks or existing document
          content) and clearly meaningful, you may add a short natural-language line for it — never a
          literal file name, folder suffix, or raw commit message. Apply the same rephrasing rules as
          elsewhere.
        """;
    }

    private const string BaseSystemPrompt = """
        You are an assistant that updates current_focus.md based on Asana task data.

        ## Output rules

        - Output ONLY the full updated content of current_focus.md. No explanations, no preamble, no markdown fences.
        - Never truncate. Always output the complete file from the first line to the last.

        ## Update rules

        1. Keep user-written lines that are still relevant. You may modify or remove lines when they
           are clearly outdated or superseded by Asana task data (e.g., a task has been completed).
        2. PRESERVE the existing Markdown heading/section structure exactly (do not add, remove, or rename sections).
        3. For each in-progress Asana task ([担当] owner tasks with 🔄 or high priority [ ]):
           - If a matching item already exists in the file → keep it as is (no duplicate).
           - If it is missing from "What I'm working on" or "Next up" sections → add it,
             rephrased to match the document's existing writing style.
        4. For "Not started, other" tasks ([担当]):
           - These are upcoming tasks. Add them to the "Next up" (or equivalent) section if not already
             present, rephrased to match the document's existing writing style.
           - Do not add them to "What I'm working on".
        5. For each completed Asana task (✅ or [x]):
           - If a matching item exists in the file → remove that line entirely.
           - Do not leave completed tasks in the document.
        6. [コラボ] (collaborator) tasks:
           - Exclude them unless already present in the file.
           - If already present → keep them (do not add [完了] based on collab tasks alone).
           - Exception: if a collab task is due today or tomorrow AND clearly requires the user's action, you may mention it, prefixed with [コラボ].
        7. Update the date line at the bottom with today's date. The line may use "更新: YYYY-MM-DD" or "Last Updated: YYYY-MM-DD" format — preserve whichever format is already used. Do NOT add a second date line.
        8. Do not fabricate information. Only use data present in the provided task lists.

        ## Writing style

        - Asana task titles are raw identifiers, not final prose. Always rephrase them.
        - When a task has "(notes: ...)", read the notes to understand the actual content and intent
          of the task. Use the notes as the primary source for writing natural, meaningful text —
          not the title alone. If the notes clarify what the task is really about, write based on
          that understanding rather than translating the title literally.
        - Mirror the document's voice: if existing items are phrased as short action-oriented notes,
          keep additions short and action-oriented. If they are narrative phrases, write narratively.
        - Do not list every Asana task mechanically. Add only tasks that represent genuinely new
          information not already captured by existing entries. Prefer fewer, more meaningful
          additions over exhaustive coverage.
        - If a task title is too vague or cryptic and has no useful notes, skip it rather
          than pasting it verbatim.
        - When adding a new item that has a due date, include the due date inline in a natural way
          (e.g., "(due: MM/DD)" or similar short notation). Do not add due dates to items that
          already exist in the document.
        """;

    private static string BuildUserPrompt(
        string projectName,
        string? workstreamId,
        string currentContent,
        AsanaTaskParseResult asanaTasks,
        string? projectSummary = null,
        IReadOnlyList<(string id, string label, AsanaTaskParseResult tasks)>? workstreamTasks = null,
        string? capturedContext = null,
        FocusActivitySignals? signals = null)
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var sb = new StringBuilder();

        sb.AppendLine($"## Context");
        sb.AppendLine($"- Project: {projectName}");
        if (!string.IsNullOrWhiteSpace(workstreamId))
            sb.AppendLine($"- Workstream: {workstreamId}");
        sb.AppendLine($"- Today: {today}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(capturedContext))
        {
            sb.AppendLine("## User-provided context (treat as priority context for this update)");
            sb.AppendLine(capturedContext.Trim());
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(projectSummary))
        {
            sb.AppendLine("## project_summary.md (background context — do not modify, use for understanding only)");
            sb.AppendLine("```");
            sb.AppendLine(projectSummary);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        sb.AppendLine("## Current current_focus.md");
        sb.AppendLine(currentContent);
        sb.AppendLine();

        sb.AppendLine("## Asana tasks: In-progress [Owner]");
        if (asanaTasks.InProgress.Count == 0)
        {
            sb.AppendLine("(none)");
        }
        else
        {
            foreach (var t in asanaTasks.InProgress)
            {
                var due = t.DueDate != null ? $"  [due: {t.DueDate}]" : "";
                var prio = !string.IsNullOrEmpty(t.Priority) ? $"  [priority: {t.Priority}]" : "";
                sb.AppendLine($"- {t.Title}{due}{prio}");
                if (!string.IsNullOrWhiteSpace(t.Description))
                    sb.AppendLine($"  (notes: {t.Description})");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Asana tasks: Completed [Owner]");
        if (asanaTasks.Completed.Count == 0)
        {
            sb.AppendLine("(none)");
        }
        else
        {
            foreach (var t in asanaTasks.Completed)
                sb.AppendLine($"- {t.Title}");
        }

        sb.AppendLine();
        var highPriorityNotStarted = asanaTasks.NotStarted
            .Where(t => t.Priority is "High")
            .ToList();
        var otherNotStarted = asanaTasks.NotStarted
            .Where(t => t.Priority is not "High")
            .ToList();

        sb.AppendLine("## Asana tasks: Not started, high priority [Owner]");
        if (highPriorityNotStarted.Count == 0)
        {
            sb.AppendLine("(none)");
        }
        else
        {
            foreach (var t in highPriorityNotStarted)
            {
                var due = t.DueDate != null ? $"  [due: {t.DueDate}]" : "";
                sb.AppendLine($"- {t.Title}{due}");
                if (!string.IsNullOrWhiteSpace(t.Description))
                    sb.AppendLine($"  (notes: {t.Description})");
            }
        }

        if (otherNotStarted.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Asana tasks: Not started, other [Owner]");
            foreach (var t in otherNotStarted)
            {
                var due = t.DueDate != null ? $"  [due: {t.DueDate}]" : "";
                var prio = !string.IsNullOrEmpty(t.Priority) ? $"  [priority: {t.Priority}]" : "";
                var parent = t.ParentTitle != null ? $"  [subtask of: {t.ParentTitle}]" : "";
                sb.AppendLine($"- {t.Title}{due}{prio}{parent}");
                if (!string.IsNullOrWhiteSpace(t.Description))
                    sb.AppendLine($"  (notes: {t.Description})");
            }
        }

        // 期日が近いコラボタスク (今日・翌日) を参考情報として渡す
        var urgentCollab = asanaTasks.Collaborating
            .Where(t => t.DueDate != null &&
                DateTime.TryParse(t.DueDate, out var d) &&
                (d.Date - DateTime.Today).TotalDays <= 1)
            .ToList();
        if (urgentCollab.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Asana tasks: Urgent collab [Collab] (due today or tomorrow — include only if user action clearly needed)");
            foreach (var t in urgentCollab)
                sb.AppendLine($"- {t.Title}  [due: {t.DueDate}]");
        }

        // Whole project モード: ワークストリームごとのタスクを追記
        if (workstreamTasks != null && workstreamTasks.Count > 0)
        {
            foreach (var (wsId, wsLabel, wsTasks) in workstreamTasks)
            {
                var displayName = string.IsNullOrWhiteSpace(wsLabel) ? wsId : wsLabel;

                sb.AppendLine();
                sb.AppendLine($"## Workstream [{displayName}] — In-progress [Owner]");
                if (wsTasks.InProgress.Count == 0)
                {
                    sb.AppendLine("(none)");
                }
                else
                {
                    foreach (var t in wsTasks.InProgress)
                    {
                        var due  = t.DueDate != null ? $"  [due: {t.DueDate}]" : "";
                        var prio = !string.IsNullOrEmpty(t.Priority) ? $"  [priority: {t.Priority}]" : "";
                        sb.AppendLine($"- {t.Title}{due}{prio}");
                    }
                }

                var wsOther = wsTasks.NotStarted.Where(t => t.Priority is not "High").ToList();
                var wsHigh  = wsTasks.NotStarted.Where(t => t.Priority is "High").ToList();

                if (wsHigh.Count > 0)
                {
                    sb.AppendLine($"## Workstream [{displayName}] — Not started, high priority [Owner]");
                    foreach (var t in wsHigh)
                    {
                        var due = t.DueDate != null ? $"  [due: {t.DueDate}]" : "";
                        sb.AppendLine($"- {t.Title}{due}");
                    }
                }

                if (wsOther.Count > 0)
                {
                    sb.AppendLine($"## Workstream [{displayName}] — Not started, other [Owner]");
                    foreach (var t in wsOther)
                    {
                        var due    = t.DueDate != null ? $"  [due: {t.DueDate}]" : "";
                        var prio   = !string.IsNullOrEmpty(t.Priority) ? $"  [priority: {t.Priority}]" : "";
                        var parent = t.ParentTitle != null ? $"  [subtask of: {t.ParentTitle}]" : "";
                        sb.AppendLine($"- {t.Title}{due}{prio}{parent}");
                    }
                }
            }
        }

        if (signals != null && !signals.IsEmpty)
            AppendActivitySignals(sb, signals);

        sb.AppendLine();
        sb.AppendLine("## Instruction");
        sb.AppendLine("Apply the update rules to produce the complete updated current_focus.md.");
        sb.AppendLine("Output the full file content only. Do not include any explanation.");

        return sb.ToString();
    }

    private static void AppendActivitySignals(StringBuilder sb, FocusActivitySignals signals)
    {
        var sinceText = signals.Since.ToString("yyyy-MM-dd");

        if (signals.PinnedFolderFiles.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"## Recent file activity (pinned folders — what the user actually touched, since {sinceText})");
            foreach (var f in signals.PinnedFolderFiles)
                sb.AppendLine($"- [{f.PinnedFolderLabel}] {f.RelativePath}  (modified: {f.ModifiedAt:yyyy-MM-dd})");
        }

        if (signals.RecentWorkFolders.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"## Recent work folders (dated work sessions under shared/_work, since {sinceText})");
            foreach (var w in signals.RecentWorkFolders)
            {
                var scope = string.IsNullOrWhiteSpace(w.WorkstreamLabel) ? "general" : w.WorkstreamLabel;
                sb.AppendLine($"- {w.Date:yyyy-MM-dd}  [{scope}] {w.FeatureName}");
            }
        }

        if (signals.RecentCommits.Count > 0 || signals.UncommittedFiles.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"## Recent git activity (commits since {sinceText} / uncommitted files)");
            foreach (var c in signals.RecentCommits)
                sb.AppendLine($"- {c.Date}  [{c.RepoName}] {c.Message}");
            if (signals.UncommittedFiles.Count > 0)
            {
                sb.AppendLine("Uncommitted files:");
                foreach (var f in signals.UncommittedFiles)
                    sb.AppendLine($"- {f}");
            }
        }

        if (signals.Captures.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Recent quick captures mentioning this project");
            foreach (var c in signals.Captures)
                sb.AppendLine($"- [{c.Timestamp:yyyy-MM-dd HH:mm}] {c.Body}");
        }
    }

    // -----------------------------------------------------------------------
    /// <summary>
    /// 既存の提案に対してユーザーの自然言語指示を適用し、更新後の全文を返す。
    /// 会話履歴 (history) を渡すことで複数回の Refine でも文脈が維持される。
    /// </summary>
    public async Task<string> RefineAsync(
        string initialUserPrompt,
        string initialProposed,
        string instructions,
        IReadOnlyList<(string instruction, string result)> history,
        CancellationToken ct = default)
    {
        var systemPrompt = BuildSystemPrompt();

        // 会話履歴を構築: 初回生成のやり取り → 過去の Refine → 今回の指示
        var messages = new List<(string role, string content)>();
        messages.Add(("user",      initialUserPrompt));
        messages.Add(("assistant", initialProposed));
        foreach (var (instr, result) in history)
        {
            messages.Add(("user",      instr));
            messages.Add(("assistant", result));
        }
        messages.Add(("user", instructions));

        var refined = await _llm.ChatWithHistoryAsync(systemPrompt, messages, ct);
        return refined.Trim();
    }

    private static string BuildSummary(
        AsanaTaskParseResult asanaTasks, WorkMode workMode, string wsId)
    {
        var sb = new StringBuilder();
        if (workMode == WorkMode.SharedWork && !string.IsNullOrEmpty(wsId))
            sb.AppendLine($"Workstream: {wsId}");

        sb.AppendLine($"In-progress tasks: {asanaTasks.InProgress.Count}");
        sb.AppendLine($"Completed tasks: {asanaTasks.Completed.Count}");
        sb.AppendLine($"Not started tasks: {asanaTasks.NotStarted.Count}");

        return sb.ToString().Trim();
    }
}
