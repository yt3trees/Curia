using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.Encodings.Web;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Curia.Models;

public enum ToolRiskLevel { ReadOnly, Write, Dangerous }

[Flags]
public enum AgentToolCapability
{
    None = 0,
    Asana = 1,
    UiNavigation = 2,
    UiReview = 4,
    ManagedRoots = 8,
}

public class AgentToolDescriptor
{
    public string Name { get; set; } = "";
    /// <summary>Stable name used by new callers. Defaults to <see cref="Name"/> for existing tools.</summary>
    public string CanonicalName { get; set; } = "";
    /// <summary>Legacy execution names accepted without advertising them to models.</summary>
    public IReadOnlyList<string> Aliases { get; set; } = [];
    /// <summary>Whether this tool is sent to providers and displayed in the Tools panel.</summary>
    public bool IsAdvertised { get; set; } = true;
    /// <summary>Optional version/date note for a legacy tool that is no longer advertised.</summary>
    public string? DeprecatedSince { get; set; }
    /// <summary>Runtime capabilities required before the tool can be advertised.</summary>
    public AgentToolCapability CapabilityRequirements { get; set; }
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
    public string Code { get; set; } = "ok";
    public string Content { get; set; } = "";
    public string? DisplaySummary { get; set; }
    public bool Truncated { get; set; }
    public string? NextCursor { get; set; }
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

public partial class AgentChatMessage : ObservableObject
{
    private static readonly JsonSerializerOptions ToolArgumentsDisplayOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public AgentMessageKind Kind { get; set; }
    [ObservableProperty] private string text = "";
    public AgentToolCall? ToolCall { get; set; }
    public string ToolResultContent { get; set; } = "";
    [ObservableProperty] private bool autoApproveForSession;
    [ObservableProperty] private bool isApprovalResolved;
    public DateTime Timestamp { get; set; } = DateTime.Now;

    public bool IsUser => Kind == AgentMessageKind.User;
    public bool IsAssistant => Kind == AgentMessageKind.Assistant;
    public bool IsToolActivity => Kind is AgentMessageKind.ToolCall or AgentMessageKind.ToolResult;
    public bool IsToolCall => Kind == AgentMessageKind.ToolCall;
    public bool IsToolResult => Kind == AgentMessageKind.ToolResult;
    public bool IsApproval => Kind == AgentMessageKind.Approval;
    public bool IsApprovalPending => IsApproval && !IsApprovalResolved;
    public string DisplayText => ToolCall == null ? Text : $"{ToolCall.Tool}: {Text}";
    public string ToolArgumentsDisplay => ToolCall?.Arguments.ToJsonString(ToolArgumentsDisplayOptions) ?? "";

    partial void OnIsApprovalResolvedChanged(bool value) => OnPropertyChanged(nameof(IsApprovalPending));
}

public class AgentTaskReference
{
    public string TaskId { get; set; } = "";
    public string Source { get; set; } = "";
    public string Project { get; set; } = "";
    public string? Workstream { get; set; }
    public string Status { get; set; } = "";
    public string Title { get; set; } = "";
    public string? ParentTitle { get; set; }
    public string? Due { get; set; }
    public string? DueBucket { get; set; }
    public bool CanComplete { get; set; }
}