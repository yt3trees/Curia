using System.IO;
using System.Text.Json.Nodes;
using Curia.Models;
using Curia.Services;

namespace Curia.Services.Agent.Tools;

public class OpenInEditorTool : ICuriaAgentTool
{
    private readonly ProjectDiscoveryService _discovery; private readonly AgentUiActions _actions; private readonly AgentPathGuard _guard;
    public OpenInEditorTool(ProjectDiscoveryService discovery, AgentUiActions actions, AgentPathGuard guard) => (_discovery, _actions, _guard) = (discovery, actions, guard);
    public AgentToolDescriptor Descriptor { get; } = new() { Name = "open_in_editor", RiskLevel = ToolRiskLevel.ReadOnly, Description = "Opens a managed file in Curia's Editor page.", ParametersSchema = "{\"project\":\"required project name\",\"path\":\"required file path\"}" };
    public async Task<AgentToolResult> ExecuteAsync(JsonObject arguments, CancellationToken ct)
    {
        var project = AgentToolArguments.ResolveProject(await _discovery.GetProjectInfoListAsync(ct: ct), AgentToolArguments.String(arguments, "project"), out var error);
        if (project == null) return Fail(error!);
        var requestedPath = AgentToolArguments.String(arguments, "path");
        if (!_guard.TryResolveWithinRoots(requestedPath, [project.Path, project.AiContextPath, project.AiContextContentPath], out var path, out error)) return Fail(error);
        if (!File.Exists(path)) return Fail("File not found.");
        if (!_guard.TryResolveWithinRoots(path, [project.Path, project.AiContextPath, project.AiContextContentPath], out path, out error)) return Fail(error);
        if (_actions.OpenInEditorAsync == null) return Fail("Editor navigation is unavailable.");
        await _actions.OpenInEditorAsync(project, path);
        return new AgentToolResult { Success = true, Content = $"Opened {path} in Editor.", DisplaySummary = "Opened in Editor" };
    }
    private static AgentToolResult Fail(string content) => new() { Success = false, Content = content, DisplaySummary = "Navigation unavailable" };
}
public class OpenInTimelineTool : ICuriaAgentTool
{
    private readonly ProjectDiscoveryService _discovery; private readonly AgentUiActions _actions;
    public OpenInTimelineTool(ProjectDiscoveryService discovery, AgentUiActions actions) => (_discovery, _actions) = (discovery, actions);
    public AgentToolDescriptor Descriptor { get; } = new() { Name = "open_in_timeline", RiskLevel = ToolRiskLevel.ReadOnly, Description = "Opens a project in Curia's Timeline page.", ParametersSchema = "{\"project\":\"required project name\"}" };
    public async Task<AgentToolResult> ExecuteAsync(JsonObject arguments, CancellationToken ct)
    {
        var project = AgentToolArguments.ResolveProject(await _discovery.GetProjectInfoListAsync(ct: ct), AgentToolArguments.String(arguments, "project"), out var error);
        if (project == null) return new AgentToolResult { Success = false, Content = error!, DisplaySummary = "Project not found" };
        if (_actions.OpenInTimelineAsync == null) return new AgentToolResult { Success = false, Content = "Timeline navigation is unavailable.", DisplaySummary = "Navigation unavailable" };
        await _actions.OpenInTimelineAsync(project);
        return new AgentToolResult { Success = true, Content = $"Opened {project.DisplayName} in Timeline.", DisplaySummary = "Opened in Timeline" };
    }
}
public class NavigateToPageTool : ICuriaAgentTool
{
    private readonly AgentUiActions _actions;
    public NavigateToPageTool(AgentUiActions actions) => _actions = actions;
    public AgentToolDescriptor Descriptor { get; } = new() { Name = "navigate_to_page", RiskLevel = ToolRiskLevel.ReadOnly, Description = "Navigates to a Curia page: dashboard, wiki, schedule, editor, timeline, or settings.", ParametersSchema = "{\"page\":\"required page name\"}" };
    public async Task<AgentToolResult> ExecuteAsync(JsonObject arguments, CancellationToken ct)
    {
        var page = AgentToolArguments.String(arguments, "page").ToLowerInvariant();
        if (page is not ("dashboard" or "wiki" or "schedule" or "editor" or "timeline" or "settings")) return new AgentToolResult { Success = false, Content = "Unsupported page.", DisplaySummary = "Invalid page" };
        if (_actions.NavigateAsync == null) return new AgentToolResult { Success = false, Content = "Navigation is unavailable.", DisplaySummary = "Navigation unavailable" };
        await _actions.NavigateAsync(page);
        return new AgentToolResult { Success = true, Content = $"Navigated to {page}.", DisplaySummary = "Navigated" };
    }
}

public class StartPomodoroTool : ICuriaAgentTool
{
    private readonly PomodoroService _pomodoro;
    private readonly ProjectDiscoveryService _discovery;
    public StartPomodoroTool(PomodoroService pomodoro, ProjectDiscoveryService discovery) => (_pomodoro, _discovery) = (pomodoro, discovery);
    public AgentToolDescriptor Descriptor { get; } = new() { Name = "start_pomodoro", RiskLevel = ToolRiskLevel.ReadOnly, Description = "Starts a Pomodoro session for an optional project and task.", ParametersSchema = "{\"project\":\"optional project name\",\"task\":\"optional task title\",\"minutes\":\"optional 5-120, default 25\"}" };
    public async Task<AgentToolResult> ExecuteAsync(JsonObject arguments, CancellationToken ct)
    {
        var projectName = AgentToolArguments.String(arguments, "project");
        ProjectInfo? project = null;
        if (projectName.Length > 0)
        {
            project = AgentToolArguments.ResolveProject(await _discovery.GetProjectInfoListAsync(ct: ct), projectName, out var error);
            if (project == null) return new AgentToolResult { Success = false, Content = error!, DisplaySummary = "Project not found" };
        }
        var minutes = Math.Clamp(arguments["minutes"]?.GetValue<int?>() ?? 25, 5, 120);
        _pomodoro.Start(new PomodoroSession { DurationMinutes = minutes, ProjectKey = project?.HiddenKey ?? "", ProjectName = project?.DisplayName ?? "", TaskTitle = AgentToolArguments.String(arguments, "task") });
        return new AgentToolResult { Success = true, Content = $"Started a {minutes}-minute Pomodoro session.", DisplaySummary = "Pomodoro started" };
    }
}