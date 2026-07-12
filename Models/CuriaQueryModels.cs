namespace Curia.Models;

public enum CuriaSourceType
{
    DecisionLog,
    FocusHistory,
    Tasks,
    Wiki,
    MeetingNotes,
}

public class CuriaCandidateMeta
{
    public string Path { get; set; } = "";
    public CuriaSourceType SourceType { get; set; }
    public string ProjectId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Snippet { get; set; } = "";
    public DateTime LastModified { get; set; }
}

public class CuriaCitation
{
    public string Path { get; set; } = "";
    public CuriaSourceType SourceType { get; set; }
    public string ProjectId { get; set; } = "";
    public int? LineHint { get; set; }
    public string? Excerpt { get; set; }
}

public class CuriaAnswer
{
    public string Question { get; set; } = "";
    public string AnswerText { get; set; } = "";
    public List<CuriaCitation> Citations { get; set; } = [];
    public List<string> SelectedPaths { get; set; } = [];
    public DateTime GeneratedAt { get; set; }
}

public class CuriaQueryOptions
{
    public IEnumerable<CuriaSourceType>? SourceTypes { get; set; }
    public int? RecencyWindowDays { get; set; }
}

public class CuriaKnowledgeMatch
{
    public string Path { get; set; } = "";
    public CuriaSourceType SourceType { get; set; }
    public string ProjectId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Excerpt { get; set; } = "";
    public DateTime LastModified { get; set; }
    public string? Content { get; set; }
}

public class CuriaConversationTurn
{
    public string Question { get; set; } = "";
    public string AnswerText { get; set; } = "";
    public List<CuriaCitation> Citations { get; set; } = [];
}
