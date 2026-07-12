using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using System.IO;
using Curia.Models;
using Curia.Services;

namespace Curia.Services.Agent.Tools;

/// <summary>
/// Generates a focus update only after the standard Agent approval, then requires
/// the existing diff review dialog to apply the proposed content.
/// </summary>
public class UpdateCurrentFocusTool : ICuriaAgentTool
{
    private readonly ProjectDiscoveryService _discovery;
    private readonly FocusUpdateService _focusUpdate;
    private readonly AgentUiActions _uiActions;

    public UpdateCurrentFocusTool(ProjectDiscoveryService discovery, FocusUpdateService focusUpdate, AgentUiActions uiActions)
        => (_discovery, _focusUpdate, _uiActions) = (discovery, focusUpdate, uiActions);

    public AgentToolDescriptor Descriptor { get; } = new()
    {
        Name = "update_current_focus",
        RiskLevel = ToolRiskLevel.Write,
        Description = "Generates a current_focus.md update from Asana data. After approval, a diff review must be applied before any file is changed.",
        ParametersSchema = "{\"project\":\"required project name\",\"workstream\":\"optional workstream id\",\"context\":\"optional user context to prioritize\"}"
    };

    public async Task<AgentToolResult> ExecuteAsync(JsonObject arguments, CancellationToken ct)
    {
        var projects = await _discovery.GetProjectInfoListAsync(ct: ct);
        var project = AgentToolArguments.ResolveProject(projects, AgentToolArguments.String(arguments, "project"), out var error);
        if (project == null) return Failure(error!);
        if (_uiActions.ReviewFocusUpdateAsync == null)
            return Failure("Focus update review is unavailable. Open Agent Chat from the Curia window and try again.");

        var workstream = AgentToolArguments.String(arguments, "workstream");
        if (!AgentWorkstreamValidator.TryValidateWorkstream(project, workstream, out var validatedWorkstream, out error)) return Failure(error);
        var result = await _focusUpdate.GenerateProposalAsync(
            project,
            validatedWorkstream,
            ct,
            AgentToolArguments.String(arguments, "context"));

        var refinementHistory = new List<(string instruction, string result)>();
        var (apply, content) = await _uiActions.ReviewFocusUpdateAsync(result, async (_, instructions) =>
        {
            var refined = await _focusUpdate.RefineAsync(
                result.DebugUserPrompt,
                result.ProposedContent,
                instructions,
                refinementHistory,
                ct);
            refinementHistory.Add((instructions, refined));
            return refined;
        });
        if (!apply) return new AgentToolResult { Success = false, Content = "The focus update was not applied during diff review.", DisplaySummary = "Focus update skipped" };

        await _focusUpdate.ApplyProposalAsync(result, content ?? result.ProposedContent, ct);
        return new AgentToolResult
        {
            Success = true,
            Content = $"Updated {result.TargetFocusPath}.\n{result.Summary}",
            DisplaySummary = "Focus update applied"
        };
    }

    private static AgentToolResult Failure(string content) => new() { Success = false, Content = content, DisplaySummary = "Focus update unavailable" };
}

public class AppendDecisionLogTool : ICuriaAgentTool
{
    private readonly ProjectDiscoveryService _discovery;
    private readonly DecisionLogGeneratorService _decisionLogs;
    private readonly AgentUiActions _uiActions;

    public AppendDecisionLogTool(ProjectDiscoveryService discovery, DecisionLogGeneratorService decisionLogs, AgentUiActions uiActions)
        => (_discovery, _decisionLogs, _uiActions) = (discovery, decisionLogs, uiActions);

    public AgentToolDescriptor Descriptor { get; } = new()
    {
        Name = "append_decision_log",
        RiskLevel = ToolRiskLevel.Write,
        Description = "Generates a structured decision log draft and saves it only after the user accepts the review dialog.",
        ParametersSchema = "{\"project\":\"required project name\",\"decision\":\"required decision details\",\"workstream\":\"optional workstream id\",\"status\":\"optional Confirmed or Tentative\",\"trigger\":\"optional decision trigger\"}"
    };

    public async Task<AgentToolResult> ExecuteAsync(JsonObject arguments, CancellationToken ct)
    {
        var projects = await _discovery.GetProjectInfoListAsync(ct: ct);
        var project = AgentToolArguments.ResolveProject(projects, AgentToolArguments.String(arguments, "project"), out var error);
        if (project == null) return Failure(error!);
        var decision = AgentToolArguments.String(arguments, "decision");
        if (string.IsNullOrWhiteSpace(decision)) return Failure("decision is required.");
        if (_uiActions.ReviewDecisionLogAsync == null)
            return Failure("Decision log review is unavailable. Open Agent Chat from the Curia window and try again.");

        var workstream = AgentToolArguments.String(arguments, "workstream");
        if (!AgentWorkstreamValidator.TryValidateWorkstream(project, workstream, out var validatedWorkstream, out error)) return Failure(error);
        var draft = await _decisionLogs.GenerateDraftAsync(
            decision,
            [],
            string.IsNullOrWhiteSpace(AgentToolArguments.String(arguments, "status")) ? "Confirmed" : AgentToolArguments.String(arguments, "status"),
            string.IsNullOrWhiteSpace(AgentToolArguments.String(arguments, "trigger")) ? "Agent chat" : AgentToolArguments.String(arguments, "trigger"),
            project,
            validatedWorkstream,
            ct: ct);
        var proposal = new FileUpdateProposal
        {
            CurrentContent = "",
            ProposedContent = draft.DraftContent,
            Summary = $"Decision log: {draft.SuggestedFileName}",
            DebugSystemPrompt = draft.DebugSystemPrompt,
            DebugUserPrompt = draft.DebugUserPrompt,
            DebugResponse = draft.DebugResponse
        };
        var refinements = new List<(string instruction, string result)>();
        var (apply, content) = await _uiActions.ReviewDecisionLogAsync(proposal, async (_, instructions) =>
        {
            var refined = await _decisionLogs.RefineAsync(draft.DebugUserPrompt, draft.DraftContent, instructions, refinements, ct);
            refinements.Add((instructions, refined));
            return refined;
        });
        if (!apply) return new AgentToolResult { Success = false, Content = "The decision log was not saved during review.", DisplaySummary = "Decision log skipped" };

        var path = await _decisionLogs.SaveDraftAsync(project, validatedWorkstream, draft, content, ct: ct);
        return new AgentToolResult { Success = true, Content = $"Saved decision log: {path}", DisplaySummary = "Decision log saved" };
    }

    private static AgentToolResult Failure(string content) => new() { Success = false, Content = content, DisplaySummary = "Decision log unavailable" };
}

public class CompleteTaskTool : ICuriaAgentTool
{
    private sealed record CompletionUndo(string TaskGid, DateTime ExpiresAt, bool InProgress = false);

    private static readonly ConcurrentDictionary<string, CompletionUndo> UndoRecords = new(StringComparer.Ordinal);
    private readonly TodayQueueService _todayQueue;

    public CompleteTaskTool(TodayQueueService todayQueue) => _todayQueue = todayQueue;

    public AgentToolDescriptor Descriptor { get; } = new()
    {
        Name = "complete_task",
        RiskLevel = ToolRiskLevel.Write,
        Description = "Completes an Asana task after approval. Pass a returned undo_token within 15 minutes to restore the task to incomplete.",
        ParametersSchema = "{\"task_gid\":\"required Asana task GID\",\"undo_token\":\"optional token returned by a prior completion\"}"
    };

    public async Task<AgentToolResult> ExecuteAsync(JsonObject arguments, CancellationToken ct)
    {
        var undoToken = AgentToolArguments.String(arguments, "undo_token");
        if (!string.IsNullOrWhiteSpace(undoToken))
            return await UndoAsync(undoToken, ct);

        var taskGid = AgentToolArguments.String(arguments, "task_gid");
        if (string.IsNullOrWhiteSpace(taskGid)) return Failure("task_gid is required.");
        var (success, message) = await _todayQueue.SetAsanaTaskCompletedAsync(taskGid, true, ct);
        if (!success) return Failure(message);

        var token = Guid.NewGuid().ToString("N");
        var expiresAt = DateTime.Now.AddMinutes(15);
        UndoRecords[token] = new CompletionUndo(taskGid, expiresAt);
        return new AgentToolResult
        {
            Success = true,
            Content = $"{message}\nUndo token: {token}\nThis token expires at {expiresAt:t}. Use complete_task with undo_token to restore the task.",
            DisplaySummary = "Task completed (Undo available)"
        };
    }

    private async Task<AgentToolResult> UndoAsync(string token, CancellationToken ct)
    {
        if (!UndoRecords.TryGetValue(token, out var record)) return Failure("The undo token was not found or has already been used.");
        if (record.ExpiresAt < DateTime.Now) return Failure("The undo token has expired.");
        var inProgress = record with { InProgress = true };
        if (record.InProgress || !UndoRecords.TryUpdate(token, inProgress, record))
            return Failure("The undo token is already being used. Try again shortly.");
        try
        {
            var (success, message) = await _todayQueue.SetAsanaTaskCompletedAsync(record.TaskGid, false, ct);
            if (success)
            {
                UndoRecords.TryRemove(new KeyValuePair<string, CompletionUndo>(token, inProgress));
                return new AgentToolResult { Success = true, Content = message, DisplaySummary = "Task restored" };
            }
            UndoRecords.TryUpdate(token, record, inProgress);
            return Failure(message);
        }
        catch (OperationCanceledException)
        {
            UndoRecords.TryUpdate(token, record, inProgress);
            throw;
        }
    }

    private static AgentToolResult Failure(string content) => new() { Success = false, Content = content, DisplaySummary = "Task update failed" };
}

internal static class AgentWorkstreamValidator
{
    public static bool TryValidateWorkstream(ProjectInfo project, string? suppliedId, out string? workstreamId, out string error)
    {
        workstreamId = null;
        error = "";
        if (string.IsNullOrWhiteSpace(suppliedId)) return true;
        if (Path.IsPathRooted(suppliedId) || suppliedId.Contains("..", StringComparison.Ordinal)
            || suppliedId.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
            || suppliedId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "Invalid workstream id.";
            return false;
        }
        if (!project.Workstreams.Any(workstream => string.Equals(workstream.Id, suppliedId, StringComparison.Ordinal)))
        {
            error = $"Unknown workstream id for project {project.DisplayName}.";
            return false;
        }
        workstreamId = suppliedId;
        return true;
    }
}