using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using Curia.Models;

namespace Curia.Services;

/// <summary>
/// AI Decision Log 生成のオーケストレーション。
/// 候補検出 (DetectCandidatesAsync)、ドラフト生成 (GenerateDraftAsync)、
/// 改訂 (RefineAsync) の 3 ステップを提供する。
/// </summary>
public class DecisionLogGeneratorService
{
    private readonly LlmClientService    _llm;
    private readonly FileEncodingService _encoding;
    private readonly ConfigService       _configService;
    private readonly AsanaTaskParser     _asanaParser;

    public DecisionLogGeneratorService(LlmClientService llm, FileEncodingService encoding, ConfigService configService, AsanaTaskParser asanaParser)
    {
        _llm           = llm;
        _encoding      = encoding;
        _configService = configService;
        _asanaParser   = asanaParser;
    }

    // -----------------------------------------------------------------------
    // 候補検出
    // -----------------------------------------------------------------------

    /// <summary>
    /// focus_history の直近バックアップと現在の current_focus.md を比較し、
    /// 暗黙の意思決定パターンを LLM で検出する。
    /// focus_history が存在しない場合は空リストを返す (エラーにしない)。
    /// </summary>
    public async Task<List<DetectedDecision>> DetectCandidatesAsync(
        ProjectInfo project,
        string? workstreamId,
        CancellationToken ct = default)
    {
        var (focusPath, histDir) = ResolveFocusPaths(project, workstreamId);

        if (!Directory.Exists(histDir) || !File.Exists(focusPath))
            return [];

        // 今日以外の最新バックアップを取得
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var backups = Directory.GetFiles(histDir, "*.md")
            .OrderByDescending(f => Path.GetFileNameWithoutExtension(f))
            .ToList();

        var previousBackup = backups.FirstOrDefault(f =>
            !Path.GetFileNameWithoutExtension(f).Equals(today, StringComparison.OrdinalIgnoreCase))
            ?? backups.FirstOrDefault();

        if (previousBackup == null) return [];

        var (previousContent, _) = await _encoding.ReadFileAsync(previousBackup);
        var (currentContent,  _) = await _encoding.ReadFileAsync(focusPath);

        if (previousContent.Trim() == currentContent.Trim()) return [];

        var diff = BuildAddedLinesDiff(previousContent, currentContent);
        if (string.IsNullOrWhiteSpace(diff)) return [];

        var systemPrompt = BuildDetectSystemPrompt();
        var userPrompt   = $"## Added lines (recent changes to current_focus.md)\n\n{diff}";
        var response     = await _llm.ChatCompletionAsync(systemPrompt, userPrompt, ct);
        return ParseDetectedDecisions(response);
    }

    private static string BuildAddedLinesDiff(string previous, string current)
    {
        var prevLines = previous.Split('\n')
            .Select(l => l.TrimEnd())
            .ToHashSet(StringComparer.Ordinal);

        var sb = new StringBuilder();
        foreach (var line in current.Split('\n').Select(l => l.TrimEnd()))
        {
            if (!string.IsNullOrWhiteSpace(line) && !prevLines.Contains(line))
                sb.AppendLine("+ " + line);
        }
        return sb.ToString();
    }

    private static List<DetectedDecision> ParseDetectedDecisions(string response)
    {
        try
        {
            var trimmed = response.Trim();
            // LLM が ```json ... ``` で囲む場合に対応
            if (trimmed.StartsWith("```"))
            {
                var start = trimmed.IndexOf('\n') + 1;
                var end   = trimmed.LastIndexOf("```");
                if (start > 0 && end > start)
                    trimmed = trimmed[start..end].Trim();
            }

            if (JsonNode.Parse(trimmed) is not JsonArray arr) return [];

            var result = new List<DetectedDecision>();
            foreach (var item in arr)
            {
                if (item == null) continue;
                result.Add(new DetectedDecision
                {
                    Summary    = item["summary"]?.GetValue<string>()  ?? "",
                    Evidence   = item["evidence"]?.GetValue<string>() ?? "",
                    Status     = item["status"]?.GetValue<string>()   ?? "confirmed",
                    IsSelected = true,
                });
            }
            return result;
        }
        catch
        {
            return [];
        }
    }

    // -----------------------------------------------------------------------
    // ドラフト生成
    // -----------------------------------------------------------------------

    /// <summary>
    /// ユーザー入力 + 選択された検出候補 + コンテキストファイル群 → 構造化ドラフトを生成する。
    /// </summary>
    public async Task<DecisionLogDraftResult> GenerateDraftAsync(
        string userInput,
        IReadOnlyList<DetectedDecision> selectedCandidates,
        string status,
        string trigger,
        ProjectInfo project,
        string? workstreamId,
        IReadOnlyList<string>? attachedFilePaths = null,
        CancellationToken ct = default)
    {
        var language     = _configService.LoadSettings().LlmLanguage;
        var systemPrompt = BuildDraftSystemPrompt(language);
        var userPrompt   = await BuildDraftUserPromptAsync(
            userInput, selectedCandidates, status, trigger, project, workstreamId, attachedFilePaths);

        var response = await _llm.ChatCompletionAsync(systemPrompt, userPrompt, ct);
        var (draftContent, suggestedFileName, resolvedTension) = ParseDraftResponse(response);

        return new DecisionLogDraftResult
        {
            DraftContent      = draftContent,
            SuggestedFileName = suggestedFileName,
            ResolvedTension   = resolvedTension,
            DebugSystemPrompt = _llm.LastSystemPrompt,
            DebugUserPrompt   = _llm.LastUserPrompt,
            DebugResponse     = _llm.LastResponse,
        };
    }

    private async Task<string> BuildDraftUserPromptAsync(
        string userInput,
        IReadOnlyList<DetectedDecision> selectedCandidates,
        string status,
        string trigger,
        ProjectInfo project,
        string? workstreamId,
        IReadOnlyList<string>? attachedFilePaths = null)
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var sb    = new StringBuilder();

        sb.AppendLine("## Decision to record");
        if (!string.IsNullOrWhiteSpace(userInput))
            sb.AppendLine(userInput);
        foreach (var c in selectedCandidates.Where(c => c.IsSelected))
        {
            sb.AppendLine($"- {c.Summary}");
            if (!string.IsNullOrWhiteSpace(c.Evidence))
                sb.AppendLine($"  (Evidence: {c.Evidence})");
        }
        sb.AppendLine();

        sb.AppendLine("## Metadata");
        sb.AppendLine($"- Status: {status}");
        sb.AppendLine($"- Trigger: {trigger}");
        sb.AppendLine($"- Date: {today}");
        sb.AppendLine($"- Project: {project.Name}");
        if (!string.IsNullOrWhiteSpace(workstreamId))
            sb.AppendLine($"- Workstream: {workstreamId}");
        sb.AppendLine();

        sb.AppendLine("## Context files");
        sb.AppendLine();

        // current_focus.md (必須)
        var (focusPath, _) = ResolveFocusPaths(project, workstreamId);
        if (File.Exists(focusPath))
        {
            var (focusContent, _) = await _encoding.ReadFileAsync(focusPath);
            sb.AppendLine("### current_focus.md");
            sb.AppendLine(focusContent);
            sb.AppendLine();
        }

        // project_summary.md (任意)
        var summaryPath = Path.Combine(project.AiContextContentPath, "project_summary.md");
        if (File.Exists(summaryPath))
        {
            var (summaryContent, _) = await _encoding.ReadFileAsync(summaryPath);
            sb.AppendLine("### project_summary.md (background)");
            sb.AppendLine(summaryContent);
            sb.AppendLine();
        }

        // open_issues.md (任意)
        var openIssuesPath = ResolveOpenIssuesPath(project, workstreamId);
        if (openIssuesPath != null)
        {
            var (openIssuesContent, _) = await _encoding.ReadFileAsync(openIssuesPath);
            sb.AppendLine("### open_issues.md (check if this decision resolves any item)");
            sb.AppendLine(openIssuesContent);
            sb.AppendLine();
        }

        // tasks.md (任意) — current_focus.md が古い場合の補完コンテキスト
        var asanaTasksPath = ResolveAsanaTasksPath(project, workstreamId);
        if (File.Exists(asanaTasksPath))
        {
            var parsed = _asanaParser.ParseFile(asanaTasksPath);
            var tasksSb = new StringBuilder();
            if (parsed.InProgress.Count > 0)
            {
                tasksSb.AppendLine("In-progress [Owner]:");
                foreach (var t in parsed.InProgress)
                {
                    var due = t.DueDate != null ? $"  [due: {t.DueDate}]" : "";
                    tasksSb.AppendLine($"- {t.Title}{due}");
                    if (!string.IsNullOrWhiteSpace(t.Description))
                        tasksSb.AppendLine($"  (notes: {t.Description})");
                }
            }
            var highPrio = parsed.NotStarted.Where(t => t.Priority is "High").ToList();
            if (highPrio.Count > 0)
            {
                tasksSb.AppendLine("Not started, high priority [Owner]:");
                foreach (var t in highPrio)
                {
                    var due = t.DueDate != null ? $"  [due: {t.DueDate}]" : "";
                    tasksSb.AppendLine($"- {t.Title}{due}");
                }
            }
            var tasksText = tasksSb.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(tasksText))
            {
                sb.AppendLine("### tasks.md (current task state — use to supplement stale current_focus.md)");
                sb.AppendLine(tasksText);
                sb.AppendLine();
            }
        }

        // 直近 decision_log 1-2件 (任意)
        var decisionLogDir = ResolveDecisionLogDir(project, workstreamId);
        if (Directory.Exists(decisionLogDir))
        {
            var recentLogs = Directory.GetFiles(decisionLogDir, "*.md")
                .OrderByDescending(f => Path.GetFileNameWithoutExtension(f))
                .Take(2)
                .ToList();

            if (recentLogs.Count > 0)
            {
                sb.AppendLine("### Recent decision logs (for tone/granularity reference)");
                foreach (var logFile in recentLogs)
                {
                    var (logContent, _) = await _encoding.ReadFileAsync(logFile);
                    sb.AppendLine($"#### {Path.GetFileNameWithoutExtension(logFile)}");
                    sb.AppendLine(logContent);
                    sb.AppendLine();
                }
            }
        }

        // 添付ファイル (任意)
        if (attachedFilePaths != null)
        {
            foreach (var filePath in attachedFilePaths)
            {
                if (!File.Exists(filePath)) continue;
                var (attachedContent, _) = await _encoding.ReadFileAsync(filePath);
                sb.AppendLine($"### Attached: {Path.GetFileName(filePath)}");
                sb.AppendLine(attachedContent);
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static (string content, string fileName, string? resolvedTension)
        ParseDraftResponse(string response)
    {
        // "---" セパレーターの後にメタデータが来る
        var separatorIdx = response.LastIndexOf("\n---\n");
        if (separatorIdx < 0) separatorIdx = response.LastIndexOf("\n---");

        string draftContent;
        string fileName = "decision";
        string? resolvedTension = null;

        if (separatorIdx >= 0)
        {
            draftContent = response[..separatorIdx].Trim();
            var meta = response[(separatorIdx + 4)..].Trim();

            foreach (var line in meta.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("FILENAME:", StringComparison.OrdinalIgnoreCase))
                    fileName = t["FILENAME:".Length..].Trim();
                else if (t.StartsWith("RESOLVED_TENSION:", StringComparison.OrdinalIgnoreCase))
                {
                    var tension = t["RESOLVED_TENSION:".Length..].Trim();
                    resolvedTension = string.Equals(tension, "none", StringComparison.OrdinalIgnoreCase)
                        ? null : tension;
                }
            }
        }
        else
        {
            draftContent = response.Trim();
        }

        fileName = SanitizeSnakeCase(fileName);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "decision";

        return (draftContent, fileName, resolvedTension);
    }

    private static string SanitizeSnakeCase(string input)
    {
        var sb = new StringBuilder();
        foreach (var ch in input.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) || ch == '_') sb.Append(ch);
            else if (ch is ' ' or '-')                 sb.Append('_');
        }
        return sb.ToString().Trim('_');
    }

    // -----------------------------------------------------------------------
    // Refine
    // -----------------------------------------------------------------------

    /// <summary>
    /// 会話履歴を保持したままユーザー指示でドラフトを改訂する。
    /// FocusUpdateService.RefineAsync と同パターン。
    /// </summary>
    public async Task<string> RefineAsync(
        string initialUserPrompt,
        string initialDraft,
        string instructions,
        IReadOnlyList<(string instruction, string result)> history,
        CancellationToken ct = default)
    {
        var language     = _configService.LoadSettings().LlmLanguage;
        var systemPrompt = BuildDraftSystemPrompt(language);
        var messages = new List<(string role, string content)>
        {
            ("user",      initialUserPrompt),
            ("assistant", initialDraft),
        };
        foreach (var (instr, result) in history)
        {
            messages.Add(("user",      instr));
            messages.Add(("assistant", result));
        }
        messages.Add(("user", instructions));

        var refined = await _llm.ChatWithHistoryAsync(systemPrompt, messages, ct);
        return refined.Trim();
    }

    /// <summary>レビュー済みのドラフトを対象プロジェクトの decision_log に保存する。</summary>
    public async Task<string> SaveDraftAsync(
        ProjectInfo project,
        string? workstreamId,
        DecisionLogDraftResult draft,
        string? content = null,
        string? fileName = null,
        CancellationToken ct = default)
    {
        var logDirectory = ResolveDecisionLogDir(project, workstreamId);
        Directory.CreateDirectory(logDirectory);
        var topic = SanitizeSnakeCase((fileName ?? draft.SuggestedFileName).Trim());
        if (string.IsNullOrWhiteSpace(topic)) topic = "decision";
        var path = GetUniqueDecisionLogPath(logDirectory, $"{DateTime.Now:yyyy-MM-dd}_{topic}.md");
        await _encoding.WriteFileAsync(path, content ?? draft.DraftContent, "UTF8", ct);
        return path;
    }

    private static string GetUniqueDecisionLogPath(string directory, string baseFileName)
    {
        var path = Path.Combine(directory, baseFileName);
        if (!File.Exists(path)) return path;
        var name = Path.GetFileNameWithoutExtension(baseFileName);
        var extension = Path.GetExtension(baseFileName);
        foreach (var suffix in "abcdefghijklmnopqrstuvwxyz")
        {
            var candidate = Path.Combine(directory, $"{name}_{suffix}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(directory, $"{name}_{DateTime.Now:HHmmss}{extension}");
    }

    // -----------------------------------------------------------------------
    // パス解決ヘルパー
    // -----------------------------------------------------------------------

    private static (string focusPath, string histDir) ResolveFocusPaths(
        ProjectInfo project, string? workstreamId)
    {
        string focusPath;
        if (!string.IsNullOrWhiteSpace(workstreamId))
        {
            var wsFocusPath = Path.Combine(
                project.AiContextContentPath, "workstreams", workstreamId, "current_focus.md");
            focusPath = File.Exists(wsFocusPath)
                ? wsFocusPath
                : Path.Combine(project.AiContextContentPath, "current_focus.md");
        }
        else
        {
            focusPath = Path.Combine(project.AiContextContentPath, "current_focus.md");
        }

        var histDir = Path.Combine(Path.GetDirectoryName(focusPath)!, "focus_history");
        return (focusPath, histDir);
    }

    private static string? ResolveOpenIssuesPath(ProjectInfo project, string? workstreamId)
    {
        if (!string.IsNullOrWhiteSpace(workstreamId))
        {
            var wsPath = Path.Combine(
                project.AiContextContentPath, "workstreams", workstreamId, "open_issues.md");
            if (File.Exists(wsPath)) return wsPath;
        }
        var rootPath = Path.Combine(project.AiContextContentPath, "open_issues.md");
        return File.Exists(rootPath) ? rootPath : null;
    }

    private static string ResolveAsanaTasksPath(ProjectInfo project, string? workstreamId)
    {
        var obsidianNotes = Path.Combine(project.AiContextPath, "obsidian_notes");
        if (!string.IsNullOrWhiteSpace(workstreamId))
        {
            var wsPath = Path.Combine(obsidianNotes, "workstreams", workstreamId, "tasks.md");
            if (File.Exists(wsPath)) return wsPath;
        }
        return Path.Combine(obsidianNotes, "tasks.md");
    }

    private static string ResolveDecisionLogDir(ProjectInfo project, string? workstreamId) =>
        !string.IsNullOrWhiteSpace(workstreamId)
            ? Path.Combine(project.AiContextContentPath, "workstreams", workstreamId, "decision_log")
            : Path.Combine(project.AiContextContentPath, "decision_log");

    // -----------------------------------------------------------------------
    // プロンプト定数
    // -----------------------------------------------------------------------

    private static string BuildDetectSystemPrompt() => """
        You are an assistant that detects implicit decisions in document changes.

        ## Input
        You receive lines added to a focus document (prefixed with +).

        ## Detection rules
        Detect lines that indicate a decision was made:
        - "Using X instead of Y" / "Switched to X"
        - "Adopted X" / "Going with X" / "Chose X"
        - "Dropped X" / "Decided against X"
        - Any statement comparing alternatives and reaching a conclusion
        - Tentative decisions: "For now, using X" / "Temporarily X"

        Do NOT detect:
        - Minor wording changes or formatting fixes
        - Factual observations without a choice
        - Hypothetical statements ("if we...", "might...")
        - Task status updates (e.g., "[完了]", "finished X")

        ## Output
        Return a JSON array of detected decisions. Each item:
        {"summary": "...", "evidence": "...", "status": "confirmed|tentative"}

        If no decisions detected, return [].
        Output ONLY the JSON array, no explanation.
        """;

    private static string BuildDraftSystemPrompt(string language)
    {
        if (language.Equals("Japanese", StringComparison.OrdinalIgnoreCase))
            return """
                You are an assistant that creates structured decision log entries.

                ## Output rules
                - Output the decision log in Markdown following the template below.
                - Title and filename must be in Japanese. Use a natural Japanese noun phrase for the filename (e.g. りんごの選択, データベースの選定, 認証方式の変更).
                - Body text should match the language of the user's input and context files.
                - Options section must list at least 2 alternatives (if info is insufficient, note what was implicitly rejected).
                - Why section must cite specific reasoning (not "AI recommended").
                - Revisit Trigger must be a measurable condition.
                - If information is insufficient, write "TBD" for that field.

                ## Template
                # Decision: {Japanese Title}

                > Date: {YYYY-MM-DD}
                > Status: Confirmed / Tentative
                > Trigger: {AI session / Meeting / Solo decision}

                ## Context
                {2-3 sentences based on current_focus.md and project_summary.md}

                ## Options

                ### Option A: {Name}
                - Pros:
                - Cons:

                ### Option B: {Name}
                - Pros:
                - Cons:

                ## Chosen
                Option {X}: {Name}

                ## Why
                {2-4 sentences}

                ## Risk
                -

                ## Revisit Trigger
                -

                ## Additional output (after --- separator)
                After the decision log content, output a separator line "---" followed by:
                FILENAME: {自然な日本語名詞句 e.g. りんごの選択}
                RESOLVED_TENSION: {item text from open_issues.md that this decision resolves, or "none"}
                """;

        return """
            You are an assistant that creates structured decision log entries.

            ## Output rules
            - Output the decision log in Markdown following the template below.
            - Title and filename must be in English (snake_case for filename).
            - Body text should match the language of the user's input and context files.
            - Options section must list at least 2 alternatives (if info is insufficient, note what was implicitly rejected).
            - Why section must cite specific reasoning (not "AI recommended").
            - Revisit Trigger must be a measurable condition.
            - If information is insufficient, write "TBD" for that field.

            ## Template
            # Decision: {English Title}

            > Date: {YYYY-MM-DD}
            > Status: Confirmed / Tentative
            > Trigger: {AI session / Meeting / Solo decision}

            ## Context
            {2-3 sentences based on current_focus.md and project_summary.md}

            ## Options

            ### Option A: {Name}
            - Pros:
            - Cons:

            ### Option B: {Name}
            - Pros:
            - Cons:

            ## Chosen
            Option {X}: {Name}

            ## Why
            {2-4 sentences}

            ## Risk
            -

            ## Revisit Trigger
            -

            ## Additional output (after --- separator)
            After the decision log content, output a separator line "---" followed by:
            FILENAME: {english_snake_case_topic}
            RESOLVED_TENSION: {item text from open_issues.md that this decision resolves, or "none"}
            """;
    }
}
