using System.Text.Json.Nodes;
using System.Text.Json;

namespace Curia.Models;

public enum ToolRiskLevel { ReadOnly, Write, Dangerous }

public class AgentToolDescriptor
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string ParametersSchema { get; set; } = "{}";
    public ToolRiskLevel RiskLevel { get; set; }
}

public class AgentToolCall
{
    public string Tool { get; set; } = "";
    public JsonObject Arguments { get; set; } = new();
    public string Reason { get; set; } = "";
}

public class AgentToolResult
{
    public bool Success { get; set; }
    public string Content { get; set; } = "";
    public string? DisplaySummary { get; set; }
}

/// <summary>OpenAI/Azure OpenAI の native function call をプロバイダー非依存で表す。</summary>
public class NativeAgentToolCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public JsonObject Arguments { get; set; } = new();
}

public class NativeAgentMessage
{
    public string Role { get; set; } = "user";
    public string? Content { get; set; }
    public string? ToolCallId { get; set; }
    public List<NativeAgentToolCall> ToolCalls { get; set; } = [];
}

public class NativeAgentCompletion
{
    public string? Content { get; set; }
    public List<NativeAgentToolCall> ToolCalls { get; set; } = [];
}

public enum AgentMessageKind { User, Assistant, ToolCall, ToolResult, Approval, Error }

public class AgentChatMessage
{
    public AgentMessageKind Kind { get; set; }
    public string Text { get; set; } = "";
    public AgentToolCall? ToolCall { get; set; }
    public string ToolResultContent { get; set; } = "";
    public bool AutoApproveForSession { get; set; }
    public bool IsApprovalResolved { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;

    public bool IsUser => Kind == AgentMessageKind.User;
    public bool IsAssistant => Kind == AgentMessageKind.Assistant;
    public bool IsToolActivity => Kind is AgentMessageKind.ToolCall or AgentMessageKind.ToolResult;
    public bool IsToolCall => Kind == AgentMessageKind.ToolCall;
    public bool IsToolResult => Kind == AgentMessageKind.ToolResult;
    public bool IsApproval => Kind == AgentMessageKind.Approval;
    public string DisplayText => ToolCall == null ? Text : $"{ToolCall.Tool}: {Text}";
    public string ToolArgumentsDisplay => ToolCall?.Arguments.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "";
}