using System.IO;
using System.Text.Json.Nodes;
using Curia.Models;

namespace Curia.Services.Agent.Tools;

public class GetScheduleTool : ICuriaAgentTool
{
    private readonly ScheduleService _schedule;
    public GetScheduleTool(ScheduleService schedule) => _schedule = schedule;
    public AgentToolDescriptor Descriptor { get; } = new() { Name = "get_schedule", RiskLevel = ToolRiskLevel.ReadOnly, Description = "Gets scheduled blocks for today or the current week.", ParametersSchema = "{\"range\":\"optional today|week (default today)\"}" };
    public Task<AgentToolResult> ExecuteAsync(JsonObject arguments, CancellationToken ct)
    {
        var range = AgentToolArguments.String(arguments, "range").ToLowerInvariant();
        if (range is not "" and not "today" and not "week") return Task.FromResult(new AgentToolResult { Success = false, Content = "range must be today or week.", DisplaySummary = "Invalid range" });
        var monday = DateTime.Today.AddDays(-((7 + (int)DateTime.Today.DayOfWeek - (int)DayOfWeek.Monday) % 7));
        var blocks = _schedule.GetBlocksForWeek(monday).Where(b => range == "week" || (b.Kind == ScheduleBlockKind.Timed ? b.StartAt?.Date == DateTime.Today : b.StartDate <= DateTime.Today && b.EndDate >= DateTime.Today));
        return Task.FromResult(AgentToolArguments.JsonResult(blocks.Select(b => new { b.TitleSnapshot, b.ProjectShortName, b.Kind, b.StartAt, b.StartDate, b.EndDate, b.Note }), $"{blocks.Count()} schedule blocks found"));
    }
}

public class GetTeamTasksTool : ICuriaAgentTool
{
    private readonly TeamTaskParser _parser; private readonly ConfigService _config;
    public GetTeamTasksTool(TeamTaskParser parser, ConfigService config) => (_parser, _config) = (parser, config);
    public AgentToolDescriptor Descriptor { get; } = new() { Name = "get_team_tasks", RiskLevel = ToolRiskLevel.ReadOnly, Description = "Gets team member tasks from team-tasks.md.", ParametersSchema = "{}" };
    public Task<AgentToolResult> ExecuteAsync(JsonObject arguments, CancellationToken ct)
    {
        var path = Path.Combine(_config.LoadSettings().ObsidianVaultRoot, "team-tasks.md");
        var (members, lastSync) = _parser.Parse(path);
        return Task.FromResult(AgentToolArguments.JsonResult(new { LastSync = lastSync, Members = members }, $"{members.Count} team members found"));
    }
}

public class SearchWikiTool : ICuriaAgentTool
{
    private readonly WikiService _wiki; private readonly ConfigService _config;
    public SearchWikiTool(WikiService wiki, ConfigService config) => (_wiki, _config) = (wiki, config);
    public AgentToolDescriptor Descriptor { get; } = new() { Name = "search_wiki", IsAdvertised = false, DeprecatedSince = "2026-07-12", RiskLevel = ToolRiskLevel.ReadOnly, Description = "Searches titles and content of the managed wiki.", ParametersSchema = "{\"query\":\"required search text\",\"limit\":\"optional number\"}" };
    public Task<AgentToolResult> ExecuteAsync(JsonObject arguments, CancellationToken ct)
    {
        var query = AgentToolArguments.String(arguments, "query");
        if (query.Length == 0) return Task.FromResult(new AgentToolResult { Success = false, Content = "query is required.", DisplaySummary = "Query required" });
        var root = Path.Combine(_config.LoadSettings().ObsidianVaultRoot, "wiki");
        var limit = Math.Clamp(arguments["limit"]?.GetValue<int?>() ?? 20, 1, 100);
        var pages = _wiki.GetAllPages(root).Where(p => p.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || (p.Content?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)).Take(limit);
        return Task.FromResult(AgentToolArguments.JsonResult(pages.Select(p => new { p.Title, p.RelativePath, p.Category, p.LastModified, p.Content }), $"{pages.Count()} wiki pages found"));
    }
}

public class GetStateSnapshotTool : ICuriaAgentTool
{
    private readonly ProjectDiscoveryService _discovery; private readonly StateSnapshotService _snapshot;
    public GetStateSnapshotTool(ProjectDiscoveryService discovery, StateSnapshotService snapshot) => (_discovery, _snapshot) = (discovery, snapshot);
    public AgentToolDescriptor Descriptor { get; } = new() { Name = "get_state_snapshot", IsAdvertised = false, DeprecatedSince = "2026-07-12", RiskLevel = ToolRiskLevel.ReadOnly, Description = "Gets a complete current project and task state snapshot.", ParametersSchema = "{}" };
    public async Task<AgentToolResult> ExecuteAsync(JsonObject arguments, CancellationToken ct) => AgentToolArguments.JsonResult(await _snapshot.BuildAsync(await _discovery.GetProjectInfoListAsync(ct: ct), ct), "State snapshot created");
}

public abstract class ProjectFileToolBase : ICuriaAgentTool
{
    protected readonly ProjectDiscoveryService Discovery; protected readonly FileEncodingService Files;
    protected ProjectFileToolBase(ProjectDiscoveryService discovery, FileEncodingService files) => (Discovery, Files) = (discovery, files);
    public abstract AgentToolDescriptor Descriptor { get; }
    protected abstract string? Resolve(ProjectInfo project);
    public async Task<AgentToolResult> ExecuteAsync(JsonObject arguments, CancellationToken ct)
    {
        var project = AgentToolArguments.ResolveProject(await Discovery.GetProjectInfoListAsync(ct: ct), AgentToolArguments.String(arguments, "project"), out var error);
        if (project == null) return new AgentToolResult { Success = false, Content = error!, DisplaySummary = "Project not found" };
        var path = Resolve(project);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return new AgentToolResult { Success = false, Content = "Requested file was not found.", DisplaySummary = "File not found" };
        var (content, _) = await Files.ReadFileAsync(path, ct);
        return AgentToolArguments.JsonResult(new { Project = project.DisplayName, Path = path, Content = content }, "File read");
    }
}

public class GetProjectContextTool : ICuriaAgentTool
{
    private static readonly string[] DefaultSections = ["focus", "summary", "open_issues"];
    private readonly ProjectDiscoveryService _discovery;
    private readonly FileEncodingService _files;

    public GetProjectContextTool(ProjectDiscoveryService discovery, FileEncodingService files) => (_discovery, _files) = (discovery, files);

    public AgentToolDescriptor Descriptor { get; } = new()
    {
        Name = "get_project_context",
        RiskLevel = ToolRiskLevel.ReadOnly,
        Description = "Reads one or more project context documents: focus, summary, and open_issues.",
        ParametersSchema = "{\"project\":\"required project name\",\"sections\":\"optional array: focus|summary|open_issues; defaults to all\"}"
    };

    public async Task<AgentToolResult> ExecuteAsync(JsonObject arguments, CancellationToken ct)
    {
        var project = AgentToolArguments.ResolveProject(await _discovery.GetProjectInfoListAsync(ct: ct), AgentToolArguments.String(arguments, "project"), out var error);
        if (project == null) return new AgentToolResult { Success = false, Content = error!, DisplaySummary = "Project not found" };

        var sections = arguments["sections"] is JsonArray suppliedSections
            ? suppliedSections.Select(value => value?.GetValue<string>() ?? "").ToArray()
            : DefaultSections;
        if (sections.Length == 0 || sections.Any(section => !DefaultSections.Contains(section, StringComparer.OrdinalIgnoreCase)))
            return new AgentToolResult { Success = false, Content = "sections must contain one or more of: focus, summary, open_issues.", DisplaySummary = "Invalid sections" };

        var documents = new JsonObject();
        foreach (var section in sections.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = section.ToLowerInvariant() switch
            {
                "focus" => project.FocusFile,
                "summary" => project.SummaryFile,
                "open_issues" => Path.Combine(project.AiContextContentPath, "open_issues.md"),
                _ => null
            };
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                documents[section] = new JsonObject { ["found"] = false };
                continue;
            }

            var (content, _) = await _files.ReadFileAsync(path, ct);
            documents[section] = new JsonObject { ["found"] = true, ["path"] = path, ["content"] = content };
        }

        return AgentToolArguments.JsonResult(new { Project = project.DisplayName, Sections = documents }, "Project context read");
    }
}

public class ReadCurrentFocusTool : ProjectFileToolBase
{
    public ReadCurrentFocusTool(ProjectDiscoveryService discovery, FileEncodingService files) : base(discovery, files) { }
    public override AgentToolDescriptor Descriptor { get; } = new() { Name = "read_current_focus", IsAdvertised = false, DeprecatedSince = "2026-07-12", RiskLevel = ToolRiskLevel.ReadOnly, Description = "Reads a project's current_focus.md.", ParametersSchema = "{\"project\":\"required project name\"}" };
    protected override string? Resolve(ProjectInfo project) => project.FocusFile;
}
public class ReadProjectSummaryTool : ProjectFileToolBase
{
    public ReadProjectSummaryTool(ProjectDiscoveryService discovery, FileEncodingService files) : base(discovery, files) { }
    public override AgentToolDescriptor Descriptor { get; } = new() { Name = "read_project_summary", IsAdvertised = false, DeprecatedSince = "2026-07-12", RiskLevel = ToolRiskLevel.ReadOnly, Description = "Reads a project's project_summary.md.", ParametersSchema = "{\"project\":\"required project name\"}" };
    protected override string? Resolve(ProjectInfo project) => project.SummaryFile;
}
public class GetOpenIssuesTool : ProjectFileToolBase
{
    public GetOpenIssuesTool(ProjectDiscoveryService discovery, FileEncodingService files) : base(discovery, files) { }
    public override AgentToolDescriptor Descriptor { get; } = new() { Name = "get_open_issues", IsAdvertised = false, DeprecatedSince = "2026-07-12", RiskLevel = ToolRiskLevel.ReadOnly, Description = "Reads a project's open_issues.md.", ParametersSchema = "{\"project\":\"required project name\"}" };
    protected override string? Resolve(ProjectInfo project) => Path.Combine(project.AiContextContentPath, "open_issues.md");
}
