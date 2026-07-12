using System.Text.Json.Nodes;

namespace Curia.Models;

public class AgentChatHistorySession
{
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<AgentChatHistoryEntry> Messages { get; set; } = [];
}

public class AgentChatHistoryEntry
{
    public AgentMessageKind Kind { get; set; }
    public string Text { get; set; } = "";
    public AgentToolCall? ToolCall { get; set; }
    public DateTime Timestamp { get; set; }
}