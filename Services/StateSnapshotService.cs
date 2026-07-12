using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Curia.Models;

namespace Curia.Services;

public class StateSnapshotService
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ConfigService _configService;
    private readonly TodayQueueService _todayQueueService;

    public StateSnapshotService(ConfigService configService, TodayQueueService todayQueueService)
    {
        _configService = configService;
        _todayQueueService = todayQueueService;
    }

    public Task ExportAsync(List<ProjectInfo> projects, CancellationToken ct = default)
    {
        return Task.Run(() => ExportCore(projects), ct);
    }

    public Task<CuratorStateSnapshot> BuildAsync(List<ProjectInfo> projects, CancellationToken ct = default)
        => Task.Run(() => BuildSnapshot(projects, _configService.LoadSettings()), ct);

    private void ExportCore(List<ProjectInfo> projects)
    {
        try
        {
            var settings = _configService.LoadSettings();
            var snapshot = BuildSnapshot(projects, settings);

            var outputPath = Path.Combine(_configService.ConfigDir, "curator_state.json");
            var tempPath = outputPath + ".tmp";

            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            File.WriteAllText(tempPath, json, Utf8NoBom);
            File.Move(tempPath, outputPath, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StateSnapshotService] Export failed: {ex.Message}");
        }
    }

    private CuratorStateSnapshot BuildSnapshot(List<ProjectInfo> projects, AppSettings settings)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        var allTasks = _todayQueueService.GetAllTasksSorted(projects, 10000);

        return new CuratorStateSnapshot
        {
            ExportedAt = DateTimeOffset.Now.ToString("o"),
            AppVersion = version,
            ConfigDir = NormalizePath(_configService.ConfigDir),
            LocalProjectsRoot = NormalizePath(settings.LocalProjectsRoot),
            CloudSyncRoot = NormalizePath(settings.CloudSyncRoot),
            ObsidianVaultRoot = NormalizePath(settings.ObsidianVaultRoot),
            StandupDir = NormalizePath(Path.Combine(settings.ObsidianVaultRoot, "standup")),
            Projects = projects.Select(BuildProjectEntry).ToList(),
            TodayTasks = allTasks.Select(BuildTodayTask).ToList(),
        };
    }

    private static CuratorProjectEntry BuildProjectEntry(ProjectInfo p)
    {
        var aiContextPath = p.AiContextPath;
        var contentPath = p.AiContextContentPath;

        var decisionLogPath = Path.Combine(contentPath, "decision_log");
        var focusHistoryPath = Path.Combine(contentPath, "focus_history");
        var obsidianNotesPath = Path.Combine(aiContextPath, "obsidian_notes");
        var asanaTasksPath = Path.Combine(obsidianNotesPath, "tasks.md");

        var paths = new CuratorProjectPaths
        {
            Root = NormalizePath(p.Path),
            AiContext = Directory.Exists(aiContextPath) ? NormalizePath(aiContextPath) : null,
            Focus = p.FocusFile != null ? NormalizePath(p.FocusFile) : null,
            Summary = p.SummaryFile != null ? NormalizePath(p.SummaryFile) : null,
            Decisions = Directory.Exists(decisionLogPath) ? NormalizePath(decisionLogPath) : null,
            FocusHistory = Directory.Exists(focusHistoryPath) ? NormalizePath(focusHistoryPath) : null,
            Tasks = File.Exists(asanaTasksPath) ? NormalizePath(asanaTasksPath) : null,
            ObsidianNotes = Directory.Exists(obsidianNotesPath) ? NormalizePath(obsidianNotesPath) : null,
            Agents = p.AgentsFile != null ? NormalizePath(p.AgentsFile) : null,
            Claude = p.ClaudeFile != null ? NormalizePath(p.ClaudeFile) : null,
        };

        var status = new CuratorProjectStatus
        {
            FocusAge = p.FocusAge,
            SummaryAge = p.SummaryAge,
            FocusLines = p.FocusLines,
            SummaryLines = p.SummaryLines,
            DecisionLogCount = p.DecisionLogCount,
            HasUncommittedChanges = p.HasUncommittedChanges,
            UncommittedRepos = p.UncommittedRepoPaths,
            HasWorkstreams = p.HasWorkstreams,
            Workstreams = p.Workstreams.Select(BuildWorkstreamEntry).ToList(),
        };

        status.PomodoroToday = BuildPomodoroTodayStatus(contentPath);

        return new CuratorProjectEntry
        {
            Name = p.Name,
            DisplayName = p.DisplayName,
            Tier = p.Tier,
            Category = p.Category,
            Paths = paths,
            Status = status,
            Junctions = new Dictionary<string, string>
            {
                ["shared"] = p.JunctionShared,
                ["obsidian"] = p.JunctionObsidian,
                ["context"] = p.JunctionContext,
            },
        };
    }

    private static PomodoroTodayStatus? BuildPomodoroTodayStatus(string contentPath)
    {
        var logPath = Path.Combine(contentPath, "focus_history", "pomodoro", $"{DateTime.Today:yyyy-MM-dd}.md");
        if (!File.Exists(logPath)) return null;

        try
        {
            var lines = File.ReadAllLines(logPath);
            int completed = 0, interrupted = 0, totalMin = 0;
            foreach (var line in lines.Where(l => l.StartsWith("- ", StringComparison.Ordinal) && l.Contains("min ")))
            {
                if (line.Contains("completed")) completed++;
                else if (line.Contains("interrupted")) interrupted++;
                var m = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)min");
                if (m.Success) totalMin += int.Parse(m.Groups[1].Value);
            }
            if (completed == 0 && interrupted == 0) return null;
            int total = completed + interrupted;
            return new PomodoroTodayStatus
            {
                CompletedSessions = completed,
                TotalFocusMinutes = totalMin,
                CompletionRate = total > 0 ? Math.Round(completed * 100.0 / total, 1) : 0
            };
        }
        catch
        {
            return null;
        }
    }

    private static CuratorWorkstreamEntry BuildWorkstreamEntry(WorkstreamInfo ws)
    {
        var decisionLogPath = Path.Combine(ws.Path, "decision_log");
        var focusHistoryPath = Path.Combine(ws.Path, "focus_history");
        var tasksPath = Path.Combine(ws.Path, "tasks.md");

        return new CuratorWorkstreamEntry
        {
            Id = ws.Id,
            Label = ws.Label,
            IsClosed = ws.IsClosed,
            FocusAge = ws.FocusAge,
            FocusPath = ws.FocusFile != null ? NormalizePath(ws.FocusFile) : null,
            TasksPath = File.Exists(tasksPath) ? NormalizePath(tasksPath) : null,
            DecisionsPath = Directory.Exists(decisionLogPath) ? NormalizePath(decisionLogPath) : null,
            FocusHistoryPath = Directory.Exists(focusHistoryPath) ? NormalizePath(focusHistoryPath) : null,
        };
    }

    private static CuratorTodayTask BuildTodayTask(TodayQueueTask t) => new()
    {
        ProjectName = t.ProjectDisplayName,
        WorkstreamId = t.WorkstreamId,
        Title = t.Title,
        ParentTitle = t.ParentTitle,
        DueDate = t.DueDate?.ToString("yyyy-MM-dd"),
        DueLabel = t.DueLabel,
        Bucket = BucketName(t.SortBucket),
        AsanaUrl = t.AsanaUrl,
    };

    private static string BucketName(int bucket) => bucket switch
    {
        0 => "overdue",
        1 => "today",
        2 => "soon",
        3 => "thisweek",
        4 => "later",
        _ => "nodue",
    };

    private static string NormalizePath(string path) => path.Replace('\\', '/');
}
