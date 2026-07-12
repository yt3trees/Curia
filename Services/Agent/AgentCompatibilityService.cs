using Curia.Models;

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

    private bool SaveResult(bool passed)
    {
        var settings = _config.LoadSettings();
        settings.AgentCompatibilityOk = passed;
        settings.AgentCompatibilityCheckedFor = $"{settings.LlmProvider}|{settings.LlmModel}";
        _config.SaveSettings(settings);
        return passed;
    }
}