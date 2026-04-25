using System.Text.Json.Serialization;

namespace Curia.Models;

public class CuratorStateSnapshot
{
    public string ExportedAt { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public string ConfigDir { get; set; } = "";
    public string LocalProjectsRoot { get; set; } = "";
    public string CloudSyncRoot { get; set; } = "";
    public string ObsidianVaultRoot { get; set; } = "";
    public string StandupDir { get; set; } = "";
    public List<CuratorProjectEntry> Projects { get; set; } = [];
    public List<CuratorTodayTask> TodayTasks { get; set; } = [];
}

public class CuratorProjectEntry
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Tier { get; set; } = "";
    public string Category { get; set; } = "";
    public CuratorProjectPaths Paths { get; set; } = new();
    public CuratorProjectStatus Status { get; set; } = new();
    public Dictionary<string, string> Junctions { get; set; } = [];
}

public class CuratorProjectPaths
{
    public string Root { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AiContext { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Focus { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Decisions { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FocusHistory { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tasks { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ObsidianNotes { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Agents { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Claude { get; set; }
}

public class CuratorProjectStatus
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FocusAge { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SummaryAge { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FocusLines { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SummaryLines { get; set; }

    public int DecisionLogCount { get; set; }
    public bool HasUncommittedChanges { get; set; }
    public List<string> UncommittedRepos { get; set; } = [];
    public bool HasWorkstreams { get; set; }
    public List<CuratorWorkstreamEntry> Workstreams { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PomodoroTodayStatus? PomodoroToday { get; set; }
}

public class PomodoroTodayStatus
{
    public int CompletedSessions { get; set; }
    public int TotalFocusMinutes { get; set; }
    public double CompletionRate { get; set; }
}

public class CuratorWorkstreamEntry
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public bool IsClosed { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FocusAge { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FocusPath { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TasksPath { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DecisionsPath { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FocusHistoryPath { get; set; }
}

public class CuratorTodayTask
{
    public string ProjectName { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkstreamId { get; set; }

    public string Title { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentTitle { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DueDate { get; set; }

    public string DueLabel { get; set; } = "";
    public string Bucket { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AsanaUrl { get; set; }
}
