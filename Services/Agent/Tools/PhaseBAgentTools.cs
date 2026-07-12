using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Curia.Models;

namespace Curia.Services.Agent.Tools;

public class ReadFileTool : ICuriaAgentTool
{
    private readonly AgentPathGuard _guard;
    private readonly FileEncodingService _files;
    public ReadFileTool(AgentPathGuard guard, FileEncodingService files) => (_guard, _files) = (guard, files);
    public AgentToolDescriptor Descriptor { get; } = new() { Name = "read_file", RiskLevel = ToolRiskLevel.ReadOnly, Description = "Reads a file within managed Curia roots.", ParametersSchema = "{\"path\": \"required absolute managed path\"}" };
    public async Task<AgentToolResult> ExecuteAsync(JsonObject arguments, CancellationToken ct)
    {
        if (!_guard.TryResolve(AgentToolArguments.String(arguments, "path"), out var path, out var error)) return Fail(error);
        if (!File.Exists(path)) return Fail("File not found.");
        var (content, encoding) = await _files.ReadFileAsync(path, ct);
        return AgentToolArguments.JsonResult(new { Path = path, Encoding = encoding, Content = content }, "File read");
    }
    private static AgentToolResult Fail(string content) => new() { Success = false, Content = content, DisplaySummary = "Read denied" };
}

public class AppendToFileTool : ICuriaAgentTool
{
    private readonly AgentPathGuard _guard;
    private readonly FileEncodingService _files;
    public AppendToFileTool(AgentPathGuard guard, FileEncodingService files) => (_guard, _files) = (guard, files);
    public AgentToolDescriptor Descriptor { get; } = new() { Name = "append_to_file", RiskLevel = ToolRiskLevel.Write, Description = "Appends Markdown text to an existing file within managed Curia roots.", ParametersSchema = "{\"path\": \"required absolute managed path\", \"content\": \"required text\"}" };
    public async Task<AgentToolResult> ExecuteAsync(JsonObject arguments, CancellationToken ct)
    {
        if (!_guard.TryResolve(AgentToolArguments.String(arguments, "path"), out var path, out var error)) return Fail(error);
        if (!File.Exists(path)) return Fail("File not found. This tool never creates files.");
        var content = AgentToolArguments.String(arguments, "content");
        if (content.Length == 0) return Fail("content is required.");
        var (existing, encoding) = await _files.ReadFileAsync(path, ct);
        await _files.WriteFileAsync(path, existing.TrimEnd() + Environment.NewLine + content + Environment.NewLine, encoding, ct);
        return new AgentToolResult { Success = true, Content = $"Appended to {path}.", DisplaySummary = "Text appended" };
    }
    private static AgentToolResult Fail(string content) => new() { Success = false, Content = content, DisplaySummary = "Append denied" };
}

public class CreateTaskTool : ICuriaAgentTool
{
    private readonly ProjectDiscoveryService _discovery;
    private readonly CaptureService _capture;
    public CreateTaskTool(ProjectDiscoveryService discovery, CaptureService capture) => (_discovery, _capture) = (discovery, capture);
    public AgentToolDescriptor Descriptor { get; } = new() { Name = "create_task", RiskLevel = ToolRiskLevel.Write, Description = "Creates an Asana task in a managed project after approval.", ParametersSchema = "{\"project\":\"required project name\",\"title\":\"required task title\",\"due_on\":\"optional YYYY-MM-DD\",\"notes\":\"optional\",\"project_gid\":\"optional configured Asana project GID\"}" };
    public async Task<AgentToolResult> ExecuteAsync(JsonObject arguments, CancellationToken ct)
    {
        var projects = await _discovery.GetProjectInfoListAsync(ct: ct);
        var project = AgentToolArguments.ResolveProject(projects, AgentToolArguments.String(arguments, "project"), out var error);
        if (project == null) return Fail(error!);
        var title = AgentToolArguments.String(arguments, "title");
        if (title.Length == 0) return Fail("title is required.");
        var (gids, _) = _capture.LoadAsanaProjectGids(project);
        var gid = AgentToolArguments.String(arguments, "project_gid");
        if (gid.Length == 0) gid = gids.FirstOrDefault() ?? "";
        if (gid.Length == 0 || !gids.Contains(gid)) return Fail("No valid configured Asana project GID was found. Specify a configured project_gid.");
        var preview = new AsanaTaskCreatePreview { ProjectName = project.DisplayName, ProjectGid = gid, TaskName = title, Notes = AgentToolArguments.String(arguments, "notes"), DueOn = AgentToolArguments.String(arguments, "due_on") };
        var key = $"agent:{project.HiddenKey}:{gid}:{title}:{preview.DueOn}";
        var result = await _capture.CreateAsanaTaskAsync(preview, key, ct);
        return new AgentToolResult { Success = result.Success, Content = JsonSerializer.Serialize(result), DisplaySummary = result.Message };
    }
    private static AgentToolResult Fail(string content) => new() { Success = false, Content = content, DisplaySummary = "Task not created" };
}

public class CaptureNoteTool : ICuriaAgentTool
{
    private readonly CaptureService _capture;
    public CaptureNoteTool(CaptureService capture) => _capture = capture;
    public AgentToolDescriptor Descriptor { get; } = new() { Name = "capture_note", RiskLevel = ToolRiskLevel.Write, Description = "Adds a timestamped note to Curia's capture log after approval.", ParametersSchema = "{\"content\":\"required note text\"}" };
    public async Task<AgentToolResult> ExecuteAsync(JsonObject arguments, CancellationToken ct)
    {
        var content = AgentToolArguments.String(arguments, "content");
        if (content.Length == 0) return new AgentToolResult { Success = false, Content = "content is required.", DisplaySummary = "Note not captured" };
        await _capture.AppendCaptureLogEntryAsync(content, ct);
        return new AgentToolResult { Success = true, Content = "Note added to capture log.", DisplaySummary = "Note captured" };
    }
}

public class SyncAsanaTool : ICuriaAgentTool
{
    private readonly AsanaSyncService _sync;
    private readonly ConfigService _config;
    public SyncAsanaTool(AsanaSyncService sync, ConfigService config) => (_sync, _config) = (sync, config);
    public AgentToolDescriptor Descriptor { get; } = new() { Name = "sync_asana", RiskLevel = ToolRiskLevel.Write, Description = "Runs Asana synchronization after approval.", ParametersSchema = "{}" };
    public async Task<AgentToolResult> ExecuteAsync(JsonObject arguments, CancellationToken ct)
    {
        var lines = new List<string>();
        await _sync.RunAsync(lines.Add, _config.LoadSettings().AsanaSync?.SkipHiddenProjects ?? true, ct);
        return new AgentToolResult { Success = true, Content = string.Concat(lines), DisplaySummary = "Asana sync completed" };
    }
}

public class GenerateStandupTool : ICuriaAgentTool
{
    private readonly StandupGeneratorService _standup;
    public GenerateStandupTool(StandupGeneratorService standup) => _standup = standup;
    public AgentToolDescriptor Descriptor { get; } = new() { Name = "generate_standup", RiskLevel = ToolRiskLevel.Write, Description = "Regenerates today's standup after approval.", ParametersSchema = "{}" };
    public async Task<AgentToolResult> ExecuteAsync(JsonObject arguments, CancellationToken ct)
    {
        await _standup.GenerateTodayAsync(ct);
        return new AgentToolResult { Success = true, Content = _standup.GetTodayStandupPath(), DisplaySummary = "Standup generated" };
    }
}