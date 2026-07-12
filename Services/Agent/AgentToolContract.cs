using System.Text.Json;
using System.Text.Json.Nodes;
using Curia.Models;

namespace Curia.Services.Agent;

/// <summary>Normalizes legacy descriptors and validates provider-independent tool arguments.</summary>
public static class AgentToolContract
{
    public static string NormalizeSchema(string schema)
    {
        var parsed = JsonNode.Parse(schema) as JsonObject
            ?? throw new InvalidOperationException("Agent tool parameters must be a JSON object.");
        if (parsed.ContainsKey("type"))
        {
            if (!string.Equals(parsed["type"]?.GetValue<string>(), "object", StringComparison.Ordinal)
                || parsed["properties"] is not JsonObject)
                throw new InvalidOperationException("Agent tool parameters must be an object JSON Schema.");
            parsed["additionalProperties"] ??= false;
            return parsed.ToJsonString();
        }

        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, value) in parsed)
        {
            var description = value?.GetValue<string>()
                ?? throw new InvalidOperationException($"Invalid description for agent tool parameter '{name}'.");
            var lower = description.ToLowerInvariant();
            var property = new JsonObject
            {
                ["type"] = name.Equals("sections", StringComparison.OrdinalIgnoreCase) || lower.Contains("array") ? "array"
                    : lower.Contains("true|false") ? "boolean"
                    : name.Equals("minutes", StringComparison.OrdinalIgnoreCase) || name.Equals("limit", StringComparison.OrdinalIgnoreCase) || name.Equals("offset", StringComparison.OrdinalIgnoreCase) || name.Equals("max_chars", StringComparison.OrdinalIgnoreCase) || lower.Contains("number") ? "integer"
                    : "string",
                ["description"] = description
            };
            if (property["type"]?.GetValue<string>() == "array") property["items"] = new JsonObject { ["type"] = "string" };
            var enumValues = ExtractEnumValues(description);
            if (enumValues.Count > 0) property["enum"] = new JsonArray(enumValues.Select(value => JsonValue.Create(value)!).ToArray());
            properties[name] = property;
            if (lower.Contains("required")) required.Add(name);
        }
        var normalized = new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };
        if (required.Count > 0) normalized["required"] = required;
        return normalized.ToJsonString();
    }

    public static bool TryValidate(string normalizedSchema, JsonObject arguments, out string error)
    {
        error = "";
        var schema = JsonNode.Parse(normalizedSchema) as JsonObject;
        var properties = schema?["properties"] as JsonObject;
        if (schema == null || properties == null) { error = "Tool schema is invalid."; return false; }
        foreach (var required in schema["required"]?.AsArray().Select(value => value?.GetValue<string>()) ?? [])
            if (string.IsNullOrWhiteSpace(required) || arguments[required] == null) { error = $"{required} is required."; return false; }
        foreach (var (name, value) in arguments)
        {
            if (properties[name] is not JsonObject property) { error = $"Unsupported argument: {name}."; return false; }
            var type = property["type"]?.GetValue<string>();
            if (!MatchesType(value, type)) { error = $"{name} must be a {type}."; return false; }
            if (property["enum"] is JsonArray allowed && value is JsonValue)
            {
                var supplied = value.GetValue<string>();
                if (!allowed.Any(item => string.Equals(item?.GetValue<string>(), supplied, StringComparison.OrdinalIgnoreCase)))
                { error = $"{name} has an unsupported value."; return false; }
            }
        }
        return true;
    }

    public static string ToEnvelope(AgentToolResult result, int maxChars)
    {
        var content = result.Content ?? "";
        var truncated = content.Length > maxChars;
        if (truncated) content = content[..maxChars];
        JsonNode? data;
        try { data = JsonNode.Parse(content); }
        catch (JsonException) { data = JsonValue.Create(content); }
        return new JsonObject
        {
            ["success"] = result.Success,
            ["code"] = result.Code,
            ["summary"] = result.DisplaySummary ?? (result.Success ? "Completed" : "Failed"),
            ["data"] = data,
            ["truncated"] = result.Truncated || truncated,
            ["next_cursor"] = result.NextCursor
        }.ToJsonString();
    }

    private static bool MatchesType(JsonNode? value, string? type) => type switch
    {
        "string" => value is JsonValue stringValue && stringValue.TryGetValue<string>(out _),
        "integer" => value is JsonValue numberValue && (numberValue.TryGetValue<int>(out _) || numberValue.TryGetValue<long>(out _)),
        "boolean" => value is JsonValue boolValue && boolValue.TryGetValue<bool>(out _),
        "array" => value is JsonArray,
        _ => true
    };

    private static List<string> ExtractEnumValues(string description)
    {
        var separator = description.IndexOf(':');
        if (separator < 0 || !description.Contains('|')) return [];
        return description[(separator + 1)..].Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(value => value.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-')).ToList();
    }
}