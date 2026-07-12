using Curia.Models;

namespace Curia.Services.Agent;

public class AgentUiActions
{
    public Func<ProjectInfo, string, Task>? OpenInEditorAsync { get; set; }
    public Func<ProjectInfo, Task>? OpenInTimelineAsync { get; set; }
    public Func<string, Task>? NavigateAsync { get; set; }
}