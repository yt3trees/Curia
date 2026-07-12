using System.Text.Json.Nodes;
using Curia.Models;

namespace Curia.Services.Agent;

public interface ICuriaAgentTool
{
    AgentToolDescriptor Descriptor { get; }
    Task<AgentToolResult> ExecuteAsync(JsonObject arguments, CancellationToken ct);
}