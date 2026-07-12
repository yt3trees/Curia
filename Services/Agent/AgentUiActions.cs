using Curia.Models;

namespace Curia.Services.Agent;

public class AgentUiActions
{
    public Func<ProjectInfo, string, Task>? OpenInEditorAsync { get; set; }
    public Func<ProjectInfo, Task>? OpenInTimelineAsync { get; set; }
    public Func<string, Task>? NavigateAsync { get; set; }
    public Func<FocusUpdateResult, Func<string, string, Task<string>>, Task<(bool apply, string? content)>>? ReviewFocusUpdateAsync { get; set; }
    public Func<FileUpdateProposal, Func<string, string, Task<string>>, Task<(bool apply, string? content)>>? ReviewDecisionLogAsync { get; set; }
}