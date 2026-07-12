using System.Text;
using System.Text.Json;
using Curia.Models;

namespace Curia.Services.Agent;

public class AgentOrchestratorService
{
    private readonly LlmClientService _llm;
    private readonly ConfigService _configService;
    private readonly ProjectDiscoveryService _discovery;
    private readonly AgentToolRegistry _registry;

    public AgentOrchestratorService(LlmClientService llm, ConfigService configService,
        ProjectDiscoveryService discovery, AgentToolRegistry registry)
    {
        _llm = llm;
        _configService = configService;
        _discovery = discovery;
        _registry = registry;
    }

    public async Task<AgentChatMessage> RunTurnAsync(List<AgentChatMessage> history, string userInput,
        Func<AgentToolCall, Task<bool>> approvalCallback, Action<AgentChatMessage> progressCallback,
        CancellationToken ct)
    {
        var settings = _configService.LoadSettings();
        if (!settings.AiEnabled) throw new InvalidOperationException("AI features are not enabled.");
        if (!IsCompatibilityCurrent(settings))
            throw new InvalidOperationException("This provider/model did not pass the agent compatibility check.");

        var projects = await _discovery.GetProjectInfoListAsync(ct: ct);
        if (LlmClientService.SupportsNativeToolCalling(settings.LlmProvider))
            return await RunNativeTurnAsync(settings, projects, history, userInput, approvalCallback, progressCallback, ct);

        var systemPrompt = BuildSystemPrompt(settings, projects, nativeToolCalling: false);
        var messages = ToLlmHistory(history);
        messages.Add(("user", $"{userInput}\n\nReply with one JSON object only."));
        var maxIterations = Math.Clamp(settings.AgentMaxIterations, 1, 20);

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();
            EnsureAgentAvailable();
            var response = await _llm.ChatWithHistoryAsync(systemPrompt, messages, ct);
            if (!AgentProtocol.TryParse(response, out var call, out var finalAnswer))
            {
                if (iteration == 0)
                {
                    messages.Add(("assistant", response));
                    messages.Add(("user", "Return exactly one JSON object of type tool_call or final_answer."));
                    continue;
                }
                return Assistant(response);
            }
            if (finalAnswer != null) return Assistant(finalAnswer);

            var toolCall = call!;
            progressCallback(new AgentChatMessage { Kind = AgentMessageKind.ToolCall, ToolCall = toolCall, Text = toolCall.Reason });
            AgentToolResult result;
            if (!_registry.TryGet(toolCall.Tool, out var tool) || tool == null)
            {
                result = new AgentToolResult { Success = false, Content = $"Unknown tool: {toolCall.Tool}", DisplaySummary = "Unknown tool" };
            }
            else if (tool.Descriptor.RiskLevel != ToolRiskLevel.ReadOnly && !await approvalCallback(toolCall))
            {
                result = new AgentToolResult { Success = false, Content = "User rejected this action.", DisplaySummary = "Rejected" };
            }
            else
            {
                try
                {
                    EnsureAgentAvailable();
                    result = await tool.ExecuteAsync(toolCall.Arguments, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { result = new AgentToolResult { Success = false, Content = $"Tool error: {ex.Message}", DisplaySummary = "Error" }; }
            }

            result.Content = Truncate(result.Content, settings.AgentToolResultMaxChars);
            progressCallback(new AgentChatMessage
            {
                Kind = AgentMessageKind.ToolResult,
                ToolCall = toolCall,
                Text = result.DisplaySummary ?? (result.Success ? "Completed" : "Failed"),
                ToolResultContent = result.Content
            });
            messages.Add(("assistant", JsonSerializer.Serialize(new { type = "tool_call", tool = toolCall.Tool, arguments = toolCall.Arguments, reason = toolCall.Reason })));
            messages.Add(("user", $"Tool result for {toolCall.Tool} ({(result.Success ? "success" : "failure")}):\n{result.Content}\n\nReply with one JSON object only."));
        }

        messages.Add(("user", "Do not call any more tools. Give the best final answer using the information already available. Reply with one JSON object only."));
        var forcedResponse = await _llm.ChatWithHistoryAsync(systemPrompt, messages, ct);
        return AgentProtocol.TryParse(forcedResponse, out _, out var forcedAnswer) && forcedAnswer != null
            ? Assistant(forcedAnswer) : Assistant(forcedResponse);
    }

    private static bool IsCompatibilityCurrent(AppSettings settings) => settings.AgentCompatibilityOk
        && string.Equals(settings.AgentCompatibilityCheckedFor, $"{settings.LlmProvider}|{settings.LlmModel}", StringComparison.OrdinalIgnoreCase);

    private void EnsureAgentAvailable()
    {
        var settings = _configService.LoadSettings();
        if (!settings.AiEnabled) throw new OperationCanceledException("AI features were disabled.");
        if (!IsCompatibilityCurrent(settings))
            throw new InvalidOperationException("This provider/model did not pass the agent compatibility check.");
    }

    private async Task<AgentChatMessage> RunNativeTurnAsync(
        AppSettings settings,
        IReadOnlyList<ProjectInfo> projects,
        List<AgentChatMessage> history,
        string userInput,
        Func<AgentToolCall, Task<bool>> approvalCallback,
        Action<AgentChatMessage> progressCallback,
        CancellationToken ct)
    {
        var systemPrompt = BuildSystemPrompt(settings, projects, nativeToolCalling: true);
        var messages = ToNativeHistory(history);
        messages.Add(new NativeAgentMessage { Role = "user", Content = userInput });
        var descriptors = _registry.GetDescriptors();
        var maxIterations = Math.Clamp(settings.AgentMaxIterations, 1, 20);

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();
            EnsureAgentAvailable();
            var response = await _llm.ChatWithToolsAsync(systemPrompt, messages, descriptors, ct);
            if (response.ToolCalls.Count == 0)
                return Assistant(response.Content ?? "No response was returned.");

            messages.Add(new NativeAgentMessage { Role = "assistant", Content = response.Content, ToolCalls = response.ToolCalls });
            foreach (var nativeCall in response.ToolCalls)
            {
                var toolCall = new AgentToolCall { Tool = nativeCall.Name, Arguments = nativeCall.Arguments, Reason = "Requested by the model." };
                progressCallback(new AgentChatMessage { Kind = AgentMessageKind.ToolCall, ToolCall = toolCall, Text = toolCall.Reason });
                var result = await ExecuteToolAsync(toolCall, approvalCallback, ct);
                result.Content = Truncate(result.Content, settings.AgentToolResultMaxChars);
                progressCallback(new AgentChatMessage
                {
                    Kind = AgentMessageKind.ToolResult,
                    ToolCall = toolCall,
                    Text = result.DisplaySummary ?? (result.Success ? "Completed" : "Failed"),
                    ToolResultContent = result.Content
                });
                messages.Add(new NativeAgentMessage { Role = "tool", ToolCallId = nativeCall.Id, Content = result.Content });
            }
        }

        messages.Add(new NativeAgentMessage { Role = "user", Content = "Do not call any more tools. Give the best final answer using the information already available." });
        var finalResponse = await _llm.ChatWithToolsAsync(systemPrompt, messages, descriptors, ct, allowTools: false);
        return Assistant(finalResponse.Content ?? "The maximum tool-call limit was reached.");
    }

    private async Task<AgentToolResult> ExecuteToolAsync(
        AgentToolCall toolCall,
        Func<AgentToolCall, Task<bool>> approvalCallback,
        CancellationToken ct)
    {
        if (!_registry.TryGet(toolCall.Tool, out var tool) || tool == null)
            return new AgentToolResult { Success = false, Content = $"Unknown tool: {toolCall.Tool}", DisplaySummary = "Unknown tool" };
        EnsureAgentAvailable();
        if (tool.Descriptor.RiskLevel != ToolRiskLevel.ReadOnly && !await approvalCallback(toolCall))
            return new AgentToolResult { Success = false, Content = "User rejected this action.", DisplaySummary = "Rejected" };
        EnsureAgentAvailable();
        try { return await tool.ExecuteAsync(toolCall.Arguments, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new AgentToolResult { Success = false, Content = $"Tool error: {ex.Message}", DisplaySummary = "Error" }; }
    }

    private string BuildSystemPrompt(AppSettings settings, IEnumerable<ProjectInfo> projects, bool nativeToolCalling)
    {
        var projectLines = projects.Take(50).Select(p => $"{p.Name} ({p.Tier}/{p.Category})").ToList();
        var remaining = projects.Skip(50).Count();
        if (remaining > 0) projectLines.Add($"...and {remaining} more (use list_projects)");
        return string.Join(Environment.NewLine, new[]
        {
            "You are Curia Agent, an assistant embedded in a personal project management app.",
            "You can call tools to read project data and perform actions on behalf of the user.",
            "",
            "Rules:",
            nativeToolCalling
                ? "- Use the provided native tools to gather facts before answering. When finished, respond with a concise Markdown answer."
                : "- Your entire reply must be exactly one JSON object starting with an opening brace. No prose or code fences.",
            nativeToolCalling ? "- Never invent tool results or project facts." : "- To call a tool: {\"type\":\"tool_call\",\"tool\":\"<name>\",\"arguments\":{},\"reason\":\"<short>\"}",
            nativeToolCalling ? "- Write tools require user approval before they run." : "- To answer: {\"type\":\"final_answer\",\"text\":\"<markdown>\"}",
            "- Gather facts with tools before answering. Do not guess project data.",
            "- Use ask_knowledge_base for open-ended decision or knowledge questions.",
            $"- Respond in {settings.LlmLanguage}.",
            "",
            nativeToolCalling ? "Use native tool calls rather than embedding JSON tool requests in text." : "Example tool call: {\"type\":\"tool_call\",\"tool\":\"get_today_tasks\",\"arguments\":{\"bucket\":\"today\"},\"reason\":\"Check today's tasks\"}",
            nativeToolCalling ? "Return Markdown only after you have enough information." : "Example answer: {\"type\":\"final_answer\",\"text\":\"Here are your priorities.\"}",
            "",
            $"Today: {DateTime.Today:yyyy-MM-dd}",
            "Projects:",
            string.Join(Environment.NewLine, projectLines),
            "",
            "Available tools:",
            nativeToolCalling ? "The available native tool definitions are supplied separately." : _registry.BuildToolsPrompt()
        });
    }

    private static List<(string role, string content)> ToLlmHistory(IEnumerable<AgentChatMessage> history) => history
        .Where(m => m.Kind is AgentMessageKind.User or AgentMessageKind.Assistant)
        .Select(m => (m.IsUser ? "user" : "assistant", m.Text)).ToList();

    private static List<NativeAgentMessage> ToNativeHistory(IEnumerable<AgentChatMessage> history) => history
        .Where(message => message.Kind is AgentMessageKind.User or AgentMessageKind.Assistant)
        .Select(message => new NativeAgentMessage { Role = message.IsUser ? "user" : "assistant", Content = message.Text })
        .ToList();

    private static AgentChatMessage Assistant(string text) => new() { Kind = AgentMessageKind.Assistant, Text = text, Timestamp = DateTime.Now };
    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "\n...truncated";
}