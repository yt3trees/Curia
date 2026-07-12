using System.Text;
using Curia.Models;

namespace Curia.Services.Agent;

public class AgentToolRegistry
{
    private readonly Dictionary<string, ICuriaAgentTool> _tools;

    public AgentToolRegistry(IEnumerable<ICuriaAgentTool> tools)
    {
        _tools = new Dictionary<string, ICuriaAgentTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            if (!_tools.TryAdd(tool.Descriptor.Name, tool))
                throw new InvalidOperationException($"Duplicate Curia agent tool: {tool.Descriptor.Name}");
        }
    }

    public bool TryGet(string name, out ICuriaAgentTool? tool) => _tools.TryGetValue(name, out tool);

    public IReadOnlyList<AgentToolDescriptor> GetDescriptors() => _tools.Values
        .Select(tool => tool.Descriptor)
        .OrderBy(descriptor => descriptor.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public string BuildToolsPrompt()
    {
        var sb = new StringBuilder();
        foreach (var tool in _tools.Values.OrderBy(t => t.Descriptor.Name, StringComparer.OrdinalIgnoreCase))
        {
            var d = tool.Descriptor;
            sb.AppendLine($"- {d.Name} ({d.RiskLevel}): {d.Description}");
            sb.AppendLine($"  arguments: {d.ParametersSchema}");
        }
        return sb.ToString().TrimEnd();
    }
}