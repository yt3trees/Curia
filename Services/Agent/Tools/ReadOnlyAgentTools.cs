using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Curia.Models;

namespace Curia.Services.Agent.Tools;

internal static class AgentToolArguments
{
    public static string String(JsonObject arguments, string name) => arguments[name]?.GetValue<string>()?.Trim() ?? "";

    public static ProjectInfo? ResolveProject(IEnumerable<ProjectInfo> projects, string requested, out string? error)
    {
        error = null;
        var matches = projects.Where(p => p.Name.Equals(requested, StringComparison.OrdinalIgnoreCase)
            || p.DisplayName.Equals(requested, StringComparison.OrdinalIgnoreCase)
            || p.HiddenKey.Equals(requested, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1) return matches[0];
        error = matches.Count == 0 ? $"Project not found: {requested}" : $"Project name is ambiguous: {requested}. Use the exact display name.";
        return null;
    }

    public static AgentToolResult JsonResult(object value, string summary) => new()
    {
        Success = true,
        Content = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }),
        DisplaySummary = summary
    };
}

public class ListProjectsTool : ICuriaAgentTool
{
    private readonly ProjectDiscoveryService _discovery;
    public ListProjectsTool(ProjectDiscoveryService discovery) => _discovery = discovery;
    public AgentToolDescriptor Descriptor { get; } = new()
    {
        Name = "list_projects", RiskLevel = ToolRiskLevel.ReadOnly,
        Description = "Lists managed projects with tier, category, focus age, decision-log count, and git-change status.",
        ParametersSchema = "{}"
    };
    public async Task<AgentToolResult> ExecuteAsync(JsonObject arguments, CancellationToken ct)
    {
        var projects = await _discovery.GetProjectInfoListAsync(ct: ct);
        return AgentToolArguments.JsonResult(projects.Select(p => new
        {
            p.Name, p.DisplayName, p.Tier, p.Category, p.FocusAge, p.DecisionLogCount, p.HasUncommittedChanges,
            Workstreams = p.Workstreams.Where(w => !w.IsClosed).Select(w => new { w.Id, w.Label })
        }), $"{projects.Count} projects found");
    }
}

public class GetTodayTasksTool : ICuriaAgentTool
{
    private readonly ProjectDiscoveryService _discovery;
    private readonly TodayQueueService _queue;
    public GetTodayTasksTool(ProjectDiscoveryService discovery, TodayQueueService queue) => (_discovery, _queue) = (discovery, queue);
    public AgentToolDescriptor Descriptor { get; } = new()
    {
        Name = "get_today_tasks", RiskLevel = ToolRiskLevel.ReadOnly,
        Description = "Gets prioritized outstanding tasks. Optionally filter bucket: overdue, today, soon, or normal.",
        ParametersSchema = "{\"bucket\": \"optional: overdue|today|soon|normal\", \"limit\": " + "optional number" + "}"
    };
    public async Task<AgentToolResult> ExecuteAsync(JsonObject arguments, CancellationToken ct)
    {
        var bucket = AgentToolArguments.String(arguments, "bucket").ToLowerInvariant();
        if (bucket is not "" and not "overdue" and not "today" and not "soon" and not "normal")
            return new AgentToolResult { Success = false, Content = "bucket must be overdue, today, soon, or normal.", DisplaySummary = "Invalid bucket" };
        var limit = arguments["limit"]?.GetValue<int?>() ?? 50;
        limit = Math.Clamp(limit, 1, 200);
        var projects = await _discovery.GetProjectInfoListAsync(ct: ct);
        var tasks = await Task.Run(() => _queue.GetAllTasksSorted(projects, limit), ct);
        if (bucket.Length > 0) tasks = tasks.Where(t => t.DueBucket == bucket).ToList();
        return AgentToolArguments.JsonResult(tasks.Select(t => new
        {
            Project = t.ProjectDisplayName, t.WorkstreamLabel, t.Title, t.ParentTitle, DueDate = t.DueDate?.ToString("yyyy-MM-dd"), t.DueBucket, t.AsanaUrl, t.IsSubtask
        }), $"{tasks.Count} tasks found");
    }
}

public class GetProjectTasksTool : ICuriaAgentTool
{
    private readonly ProjectDiscoveryService _discovery;
    private readonly AsanaTaskParser _parser;
    public GetProjectTasksTool(ProjectDiscoveryService discovery, AsanaTaskParser parser) => (_discovery, _parser) = (discovery, parser);
    public AgentToolDescriptor Descriptor { get; } = new()
    {
        Name = "get_project_tasks", RiskLevel = ToolRiskLevel.ReadOnly,
        Description = "Gets parsed tasks from a project's tasks.md. Optional workstream and status filters are supported.",
        ParametersSchema = "{\"project\": \"required project name\", \"workstream\": \"optional id\", \"status\": \"optional: in_progress|not_started|completed|collaborating\"}"
    };
    public async Task<AgentToolResult> ExecuteAsync(JsonObject arguments, CancellationToken ct)
    {
        var requested = AgentToolArguments.String(arguments, "project");
        if (requested.Length == 0) return new AgentToolResult { Success = false, Content = "project is required.", DisplaySummary = "Project required" };
        var projects = await _discovery.GetProjectInfoListAsync(ct: ct);
        var project = AgentToolArguments.ResolveProject(projects, requested, out var error);
        if (project == null) return new AgentToolResult { Success = false, Content = error!, DisplaySummary = "Project not found" };

        var workstream = AgentToolArguments.String(arguments, "workstream");
        var status = AgentToolArguments.String(arguments, "status").ToLowerInvariant();
        var paths = new List<(string label, string path)>();
        if (workstream.Length == 0) paths.Add(("root", Path.Combine(project.AiContextPath, "obsidian_notes", "tasks.md")));
        foreach (var ws in project.Workstreams.Where(w => !w.IsClosed && (workstream.Length == 0 || w.Id.Equals(workstream, StringComparison.OrdinalIgnoreCase))))
            paths.Add((ws.Id, Path.Combine(project.AiContextPath, "obsidian_notes", "workstreams", ws.Id, "tasks.md")));
        if (workstream.Length > 0 && paths.Count == 0)
            return new AgentToolResult { Success = false, Content = $"Workstream not found: {workstream}", DisplaySummary = "Workstream not found" };

        var all = new List<object>();
        foreach (var (label, path) in paths.Where(p => File.Exists(p.path)))
        {
            ct.ThrowIfCancellationRequested();
            var parsed = _parser.ParseFile(path);
            Add(parsed.InProgress, "in_progress"); Add(parsed.NotStarted, "not_started"); Add(parsed.Completed, "completed"); Add(parsed.Collaborating, "collaborating");
            void Add(IEnumerable<ParsedAsanaTask> tasks, string taskStatus)
            {
                if (status.Length > 0 && status != taskStatus) return;
                all.AddRange(tasks.Select(t => (object)new { Workstream = label, Status = taskStatus, t.Title, t.ParentTitle, t.Priority, t.DueDate, t.Description, t.Id }));
            }
        }
        return AgentToolArguments.JsonResult(all, $"{all.Count} tasks found in {project.DisplayName}");
    }
}

public class SearchDecisionLogsTool : ICuriaAgentTool
{
    private readonly ProjectDiscoveryService _discovery;
    private readonly DecisionLogService _decisionLogs;
    public SearchDecisionLogsTool(ProjectDiscoveryService discovery, DecisionLogService decisionLogs) => (_discovery, _decisionLogs) = (discovery, decisionLogs);
    public AgentToolDescriptor Descriptor { get; } = new()
    {
        Name = "search_decision_logs", RiskLevel = ToolRiskLevel.ReadOnly,
        Description = "Searches decision logs by keyword, optionally restricted to one project.",
        ParametersSchema = "{\"query\": \"required search keywords\", \"project\": \"optional project name\"}"
    };
    public async Task<AgentToolResult> ExecuteAsync(JsonObject arguments, CancellationToken ct)
    {
        var query = AgentToolArguments.String(arguments, "query");
        if (query.Length == 0) return new AgentToolResult { Success = false, Content = "query is required.", DisplaySummary = "Query required" };
        var projects = await _discovery.GetProjectInfoListAsync(ct: ct);
        var requested = AgentToolArguments.String(arguments, "project");
        if (requested.Length > 0)
        {
            var project = AgentToolArguments.ResolveProject(projects, requested, out var error);
            if (project == null) return new AgentToolResult { Success = false, Content = error!, DisplaySummary = "Project not found" };
            projects = [project];
        }
        var results = new List<object>();
        foreach (var project in projects)
        {
            ct.ThrowIfCancellationRequested();
            var logs = await _decisionLogs.GetDecisionLogsAsync(project.AiContextContentPath);
            results.AddRange(logs.Where(item => Matches(item, query)).Select(item => (object)new
            {
                Project = project.DisplayName, item.Title, item.Date, item.Status, item.Trigger, item.ChosenSummary, item.WhySummary, item.FilePath
            }));
        }
        return AgentToolArguments.JsonResult(results, $"{results.Count} decision logs found");
    }
    private static bool Matches(DecisionLogItem item, string query) => new[] { item.Title, item.Trigger, item.ChosenSummary, item.WhySummary }
        .Any(value => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);
}

public class AskKnowledgeBaseTool : ICuriaAgentTool
{
    private readonly CuriaQueryService _query;
    public AskKnowledgeBaseTool(CuriaQueryService query) => _query = query;
    public AgentToolDescriptor Descriptor { get; } = new()
    {
        Name = "ask_knowledge_base", RiskLevel = ToolRiskLevel.ReadOnly,
        Description = "Answers open-ended questions from indexed project knowledge, with source citations.",
        ParametersSchema = "{\"question\": \"required question\"}"
    };
    public async Task<AgentToolResult> ExecuteAsync(JsonObject arguments, CancellationToken ct)
    {
        var question = AgentToolArguments.String(arguments, "question");
        if (question.Length == 0) return new AgentToolResult { Success = false, Content = "question is required.", DisplaySummary = "Question required" };
        var answer = await _query.AskAsync(question, null, null, ct);
        return AgentToolArguments.JsonResult(new
        {
            answer.AnswerText,
            Citations = answer.Citations.Select(c => new { c.Path, Source = c.SourceType.ToString(), c.ProjectId, c.LineHint, c.Excerpt })
        }, answer.Citations.Count == 0 ? "Knowledge-base answer" : $"Knowledge-base answer with {answer.Citations.Count} citations");
    }
}