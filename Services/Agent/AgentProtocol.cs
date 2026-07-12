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
            if (node == null) return false;
            if (!TryGetString(node, "type", out var type)) return false;
            if (string.Equals(type, "final_answer", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryGetString(node, "text", out var text)) return false;
                finalAnswer = text;
                return true;
            }

            if (!string.Equals(type, "tool_call", StringComparison.OrdinalIgnoreCase)) return false;
            if (!TryGetString(node, "tool", out var tool)) return false;
            if (string.IsNullOrWhiteSpace(tool)) return false;
            if (node["arguments"] is not null and not JsonObject) return false;
            toolCall = new AgentToolCall
            {
                Tool = tool,
                Arguments = node?["arguments"] as JsonObject ?? new JsonObject(),
                Reason = TryGetString(node, "reason", out var reason) ? reason : ""
            };
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static bool TryGetString(JsonObject node, string name, out string value)
    {
        value = "";
        if (node[name] is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var parsed)) return false;
        value = parsed ?? "";
        return true;
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