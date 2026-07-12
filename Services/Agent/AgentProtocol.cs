using System.Text.Json;
using System.Text.Json.Nodes;
using Curia.Models;

namespace Curia.Services.Agent;

public static class AgentProtocol
{
    public static bool TryParse(string response, out AgentToolCall? toolCall, out string? finalAnswer)
    {
        toolCall = null;
        finalAnswer = null;
        if (!TryExtractJson(response, out var json)) return false;

        try
        {
            var node = JsonNode.Parse(json) as JsonObject;
            var type = node?["type"]?.GetValue<string>();
            if (string.Equals(type, "final_answer", StringComparison.OrdinalIgnoreCase))
            {
                finalAnswer = node?["text"]?.GetValue<string>() ?? "";
                return true;
            }

            if (!string.Equals(type, "tool_call", StringComparison.OrdinalIgnoreCase)) return false;
            var tool = node?["tool"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(tool)) return false;
            toolCall = new AgentToolCall
            {
                Tool = tool,
                Arguments = node?["arguments"] as JsonObject ?? new JsonObject(),
                Reason = node?["reason"]?.GetValue<string>() ?? ""
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryExtractJson(string response, out string json)
    {
        json = "";
        if (string.IsNullOrWhiteSpace(response)) return false;
        var text = response.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            var endFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && endFence > firstNewline)
                text = text[(firstNewline + 1)..endFence].Trim();
        }

        var start = text.IndexOf('{');
        while (start >= 0)
        {
            var depth = 0;
            var quoted = false;
            var escaped = false;
            for (var i = start; i < text.Length; i++)
            {
                var ch = text[i];
                if (quoted)
                {
                    if (escaped) escaped = false;
                    else if (ch == '\\') escaped = true;
                    else if (ch == '"') quoted = false;
                    continue;
                }
                if (ch == '"') quoted = true;
                else if (ch == '{') depth++;
                else if (ch == '}' && --depth == 0)
                {
                    json = text[start..(i + 1)];
                    return true;
                }
            }
            start = text.IndexOf('{', start + 1);
        }
        return false;
    }
}