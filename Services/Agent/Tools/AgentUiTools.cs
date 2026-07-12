using System.IO;
using System.Text.Json.Nodes;
using Curia.Models;
using Curia.Services;

namespace Curia.Services.Agent.Tools;

public class OpenInEditorTool : ICuriaAgentTool
{
    private readonly ProjectDiscoveryService _discovery; private readonly AgentUiActions _actions; private readonly AgentPathGuard _guard;
    public OpenInEditorTool(ProjectDiscoveryService discovery, AgentUiActions actions, AgentPathGuard guard) => (_discovery, _actions, _guard) = (discovery, actions, guard);
    public AgentToolDescriptor Descriptor { get; } = new() { Name = "open_in_editor", CapabilityRequirements = AgentToolCapability.UiNavigation | AgentToolCapability.ManagedRoots, RiskLevel = ToolRiskLevel.ReadOnly, Description = "Opens a managed file in Curia's Editor page.", ParametersSchema = "{\"project\":\"required project name\",\"path\":\"required file path\"}" };
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
public class NavigateToPageTool : ICuriaAgentTool
{
    private readonly ProjectDiscoveryService _discovery;
    private readonly AgentUiActions _actions;
    public NavigateToPageTool(ProjectDiscoveryService discovery, AgentUiActions actions) => (_discovery, _actions) = (discovery, actions);
    public AgentToolDescriptor Descriptor { get; } = new() { Name = "navigate_to_page", CapabilityRequirements = AgentToolCapability.UiNavigation, RiskLevel = ToolRiskLevel.ReadOnly, Description = "Navigates to a Curia page. For timeline, project optionally opens that project's timeline.", ParametersSchema = "{\"page\":\"required: dashboard|wiki|schedule|editor|timeline|settings\",\"project\":\"optional project name, only used for timeline\"}" };
    public async Task<AgentToolResult> ExecuteAsync(JsonObject arguments, CancellationToken ct)
    {
        var page = AgentToolArguments.String(arguments, "page").ToLowerInvariant();
        if (page is not ("dashboard" or "wiki" or "schedule" or "editor" or "timeline" or "settings")) return new AgentToolResult { Success = false, Content = "Unsupported page.", DisplaySummary = "Invalid page" };
        if (_actions.NavigateAsync == null) return new AgentToolResult { Success = false, Content = "Navigation is unavailable.", DisplaySummary = "Navigation unavailable" };
        ProjectInfo? project = null;
        var requestedProject = AgentToolArguments.String(arguments, "project");
        if (requestedProject.Length > 0)
        {
            if (page != "timeline") return new AgentToolResult { Success = false, Code = "invalid_argument", Content = "project is only supported when page is timeline.", DisplaySummary = "Invalid navigation" };
            project = AgentToolArguments.ResolveProject(await _discovery.GetProjectInfoListAsync(ct: ct), requestedProject, out var error);
            if (project == null) return new AgentToolResult { Success = false, Code = "project_not_found", Content = error!, DisplaySummary = "Project not found" };
        }
        await _actions.NavigateAsync(page, project);
        return new AgentToolResult { Success = true, Content = $"Navigated to {page}.", DisplaySummary = "Navigated" };
    }
}

