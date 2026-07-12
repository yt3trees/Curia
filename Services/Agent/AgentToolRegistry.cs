using System.Text;
using Curia.Models;

namespace Curia.Services.Agent;

public class AgentToolRegistry
{
    private readonly Dictionary<string, ICuriaAgentTool> _tools;
    private readonly IReadOnlyList<ICuriaAgentTool> _registeredTools;
    private readonly ConfigService _config;
    private readonly AgentUiActions _uiActions;

    public AgentToolRegistry(IEnumerable<ICuriaAgentTool> tools, ConfigService config, AgentUiActions uiActions)
    {
        _config = config;
        _uiActions = uiActions;
        _tools = new Dictionary<string, ICuriaAgentTool>(StringComparer.OrdinalIgnoreCase);
        _registeredTools = tools.ToList();
        foreach (var tool in _registeredTools)
        {
            var descriptor = tool.Descriptor;
            if (string.IsNullOrWhiteSpace(descriptor.CanonicalName))
                descriptor.CanonicalName = descriptor.Name;
            descriptor.ParametersSchema = AgentToolContract.NormalizeSchema(descriptor.ParametersSchema);
            AddExecutionName(descriptor.CanonicalName, tool);
            foreach (var alias in descriptor.Aliases)
                AddExecutionName(alias, tool);
        }
    }

    public bool TryGet(string name, out ICuriaAgentTool? tool) => _tools.TryGetValue(name, out tool);

    public IReadOnlyList<AgentToolDescriptor> GetDescriptors() => _registeredTools
        .Select(tool => tool.Descriptor)
        .Where(IsAvailable)
        .OrderBy(descriptor => descriptor.CanonicalName, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public string BuildToolsPrompt()
    {
        var sb = new StringBuilder();
        foreach (var tool in _registeredTools.Where(t => IsAvailable(t.Descriptor))
                     .OrderBy(t => t.Descriptor.CanonicalName, StringComparer.OrdinalIgnoreCase))
        {
            var d = tool.Descriptor;
            sb.AppendLine($"- {d.CanonicalName} ({d.RiskLevel}): {d.Description}");
            sb.AppendLine($"  arguments: {d.ParametersSchema}");
        }
        return sb.ToString().TrimEnd();
    }

    private void AddExecutionName(string name, ICuriaAgentTool tool)
    {
        if (string.IsNullOrWhiteSpace(name) || !_tools.TryAdd(name, tool))
            throw new InvalidOperationException($"Duplicate or empty Curia agent tool name: {name}");
    }

    private bool IsAvailable(AgentToolDescriptor descriptor)
    {
        if (!descriptor.IsAdvertised) return false;
        var requirements = descriptor.CapabilityRequirements;
        if (requirements.HasFlag(AgentToolCapability.Asana) && !_config.IsAsanaConfigured()) return false;
        if (requirements.HasFlag(AgentToolCapability.ManagedRoots))
        {
            var settings = _config.LoadSettings();
            if (string.IsNullOrWhiteSpace(settings.LocalProjectsRoot)
                && string.IsNullOrWhiteSpace(settings.CloudSyncRoot)
                && string.IsNullOrWhiteSpace(settings.ObsidianVaultRoot)) return false;
        }
        if (requirements.HasFlag(AgentToolCapability.UiNavigation)
            && _uiActions.NavigateAsync == null && _uiActions.OpenInEditorAsync == null) return false;
        if (requirements.HasFlag(AgentToolCapability.UiReview)
            && _uiActions.ReviewFocusUpdateAsync == null && _uiActions.ReviewDecisionLogAsync == null) return false;
        return true;
    }
}