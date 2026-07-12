using Curia.Models;
using CommunityToolkit.Mvvm.Messaging;

namespace Curia.Services.Agent;

public class AgentCompatibilityService
{
    private const string TestSystemPrompt = """
You are testing a strict JSON agent protocol. Your whole reply must be one JSON object only.
For a request needing a tool, return {"type":"tool_call","tool":"echo","arguments":{"value":"ok"},"reason":"test"}.
For a completed request, return {"type":"final_answer","text":"ok"}.
""";

    private readonly LlmClientService _llm;
    private readonly ConfigService _config;
    public AgentCompatibilityService(LlmClientService llm, ConfigService config) => (_llm, _config) = (llm, config);

    public async Task<bool> TestAsync(CancellationToken ct)
    {
        var settings = _config.LoadSettings();
        if (LlmClientService.SupportsNativeToolCalling(settings.LlmProvider))
            return await TestNativeToolCallingAsync(ct);

        var first = await _llm.ChatCompletionAsync(TestSystemPrompt, "Call the echo tool with the value ok.", ct);
        if (!AgentProtocol.TryParse(first, out var call, out _) || call?.Tool != "echo") return SaveResult(false);
        var messages = new List<(string role, string content)>
        {
            ("user", "Call the echo tool with the value ok."),
            ("assistant", first),
            ("user", "Tool result for echo: ok. Give the completed answer.")
        };
        var second = await _llm.ChatWithHistoryAsync(TestSystemPrompt, messages, ct);
        return SaveResult(AgentProtocol.TryParse(second, out _, out var answer) && answer != null);
    }

    private async Task<bool> TestNativeToolCallingAsync(CancellationToken ct)
    {
        var echo = new AgentToolDescriptor
        {
            Name = "echo",
            Description = "Returns the supplied value.",
            ParametersSchema = "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}},\"required\":[\"value\"]}",
            RiskLevel = ToolRiskLevel.ReadOnly
        };
        var messages = new List<NativeAgentMessage>
        {
            new() { Role = "user", Content = "Call the echo tool with the value ok." }
        };
        var first = await _llm.ChatWithToolsAsync("Use the echo tool when requested.", messages, [echo], ct);
        var call = first.ToolCalls.FirstOrDefault(tool => tool.Name == "echo");
        if (call == null) return SaveResult(false);
        messages.Add(new NativeAgentMessage { Role = "assistant", Content = first.Content, ToolCalls = first.ToolCalls });
        messages.Add(new NativeAgentMessage { Role = "tool", ToolCallId = call.Id, Content = "ok" });
        messages.Add(new NativeAgentMessage { Role = "user", Content = "Give the completed answer." });
        var second = await _llm.ChatWithToolsAsync("Use the echo tool when requested.", messages, [echo], ct, allowTools: false);
        return SaveResult(second.ToolCalls.Count == 0 && !string.IsNullOrWhiteSpace(second.Content));
    }

    private bool SaveResult(bool passed)
    {
        var settings = _config.LoadSettings();
        settings.AgentCompatibilityOk = passed;
        settings.AgentCompatibilityCheckedFor = $"{settings.LlmProvider}|{settings.LlmModel}";
        _config.SaveSettings(settings);
        WeakReferenceMessenger.Default.Send(new AgentCompatibilityChangedMessage(passed));
        return passed;
    }
}