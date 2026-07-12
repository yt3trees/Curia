using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Curia.Helpers;
using Curia.Models;

namespace Curia.Services;

public class StandupGeneratorService : IDisposable
{
    private readonly ConfigService _configService;
    private readonly ProjectDiscoveryService _discoveryService;
    private readonly TodayQueueService _todayQueueService;
    private System.Threading.Timer? _timer;

    public StandupGeneratorService(
        ConfigService configService,
        ProjectDiscoveryService discoveryService,
        TodayQueueService todayQueueService)
    {
        _configService = configService;
        _discoveryService = discoveryService;
        _todayQueueService = todayQueueService;
    }

    public void StartScheduler()
    {
        // アプリ起動直後 + 1時間ごとにチェック
        _timer = new System.Threading.Timer(_ => _ = TryGenerateTodayAsync(),
                                            null, TimeSpan.Zero, TimeSpan.FromHours(1));
    }

    /// <summary>
    /// 今日の standup を生成する。既に存在する場合は何もしない (冪等)。
    /// </summary>
    public async Task TryGenerateTodayAsync()
    {
        try
        {
            if (DateTime.Now.Hour < 6) return;
            var path = GetTodayStandupPath();
            if (string.IsNullOrEmpty(path)) return;
            if (File.Exists(path)) return;
            await GenerateAndSaveAsync(path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Standup] TryGenerateTodayAsync failed: {ex}");
        }
    }

    public string GetTodayStandupPath()
    {
        var obsidian = _configService.LoadSettings().ObsidianVaultRoot;
        if (string.IsNullOrWhiteSpace(obsidian)) return "";
        return Path.Combine(obsidian, "standup", $"{DateTime.Today:yyyy-MM-dd}_standup.md");
    }

    public async Task GenerateTodayAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var path = GetTodayStandupPath();
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Obsidian vault root is not configured.");
        await GenerateAndSaveAsync(path);
    }

    private async Task GenerateAndSaveAsync(string path)
    {
        var projects = await Task.Run(() => _discoveryService.GetProjectInfoList());
        var yesterday = DateTime.Today.AddDays(-1);

        var sb = new StringBuilder();
        sb.AppendLine($"# Daily Standup: {DateTime.Today:yyyy-MM-dd}");
        sb.AppendLine();

        // --- Yesterday ---
        sb.AppendLine("## Yesterday");
        var yesterdayLines = BuildYesterdayLines(projects, yesterday);
        if (yesterdayLines.Count > 0)
        {
            foreach (var line in yesterdayLines)
                sb.AppendLine(line);
        }
        else
        {
            sb.AppendLine("- (none)");
        }
        sb.AppendLine();

        // --- Today / This Week ---
        var (todayLines, thisWeekLines) = BuildTodayAndWeekLines(projects);

        sb.AppendLine("## Today");
        if (todayLines.Count > 0)
        {
            foreach (var line in todayLines)
                sb.AppendLine(line);
        }
        else
        {
            sb.AppendLine("- (none)");
        }
        sb.AppendLine();

        sb.AppendLine("## This Week");
        if (thisWeekLines.Count > 0)
        {
            foreach (var line in thisWeekLines)
                sb.AppendLine(line);
        }
        else
        {
            sb.AppendLine("- (none)");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(false));
        Debug.WriteLine($"[Standup] Generated: {path}");
    }

    private static List<string> BuildYesterdayLines(IEnumerable<ProjectInfo> projects, DateTime yesterday)
    {
        var result = new List<string>();

        foreach (var proj in projects)
        {
            // focus_history/{yesterday}.md
            var focusPath = Path.Combine(proj.AiContextContentPath, "focus_history", $"{yesterday:yyyy-MM-dd}.md");
            if (File.Exists(focusPath))
            {
                var preview = GetFocusPreview(focusPath);
                result.Add($"- [{proj.Name}] {preview} (Focus)");
            }

            // decision_log/{yesterday}_*.md
            var logDir = Path.Combine(proj.AiContextContentPath, "decision_log");
            if (Directory.Exists(logDir))
            {
                var pattern = $"{yesterday:yyyy-MM-dd}_*.md";
                foreach (var file in Directory.EnumerateFiles(logDir, pattern))
                {
                    var baseName = Path.GetFileNameWithoutExtension(file);
                    var match = Regex.Match(baseName, @"^(\d{4}-\d{2}-\d{2})_(.+)$");
                    var topic = match.Success ? match.Groups[2].Value : baseName;
                    result.Add($"- [{proj.Name}] {topic} (Decision)");
                }
            }

            // 完了済み Asana タスク
            var asanaTasks = GetCompletedAsanaTasks(proj, yesterday);
            foreach (var taskName in asanaTasks)
                result.Add($"- [{proj.Name}] Asana: {taskName}");

            // ポモドーロ集計
            var pomodoroSummary = GetPomodoroSummary(proj, yesterday);
            if (pomodoroSummary != null)
                result.Add(pomodoroSummary);
        }

        return result;
    }

    private static string? GetPomodoroSummary(ProjectInfo proj, DateTime date)
    {
        var logPath = Path.Combine(proj.AiContextContentPath, "focus_history", "pomodoro", $"{date:yyyy-MM-dd}.md");
        if (!File.Exists(logPath)) return null;

        try
        {
            var lines = File.ReadAllLines(logPath);
            int completed = lines.Count(l => l.StartsWith("- ", StringComparison.Ordinal) && l.Contains("completed"));
            int totalMin = 0;
            foreach (var line in lines.Where(l => l.StartsWith("- ", StringComparison.Ordinal) && l.Contains("min ")))
            {
                var m = Regex.Match(line, @"(\d+)min");
                if (m.Success) totalMin += int.Parse(m.Groups[1].Value);
            }
            if (completed == 0 && totalMin == 0) return null;
            return $"- [{proj.Name}] Pomodoro: {completed} sessions, {totalMin} min focus";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Standup] GetPomodoroSummary failed: {ex.Message}");
            return null;
        }
    }

    private static List<string> GetCompletedAsanaTasks(ProjectInfo proj, DateTime targetDate)
    {
        var result = new List<string>();

        // obsidian_notes/tasks.md から completed: YYYY-MM-DD コメントを持つ行を抽出
        var asanaPath = Path.Combine(proj.AiContextPath, "obsidian_notes", "tasks.md");
        if (!File.Exists(asanaPath)) return result;

        try
        {
            var dateStr = targetDate.ToString("yyyy-MM-dd");
            var lines = File.ReadAllLines(asanaPath);
            bool inCompleted = false;

            foreach (var line in lines)
            {
                if (Regex.IsMatch(line, @"^#{2,3}\s*完了"))
                {
                    inCompleted = true;
                    continue;
                }
                if (inCompleted && Regex.IsMatch(line, @"^## "))
                {
                    inCompleted = false;
                    continue;
                }
                if (inCompleted && line.Contains($"<!-- completed: {dateStr} -->"))
                {
                    // タスク名を抽出: "- [x] [役割] タスク名 (Due: ...) [[Asana](...)] <!-- completed: ... -->"
                    var nameMatch = Regex.Match(line, @"^\s*-\s+\[x\]\s+(?:\[.+?\]\s+)*(.+?)(?:\s+\(Due:|$)");
                    if (nameMatch.Success)
                        result.Add(nameMatch.Groups[1].Value.Trim());
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Standup] GetCompletedAsanaTasks failed for {proj.Name}: {ex.Message}");
        }

        return result;
    }

    private (List<string> today, List<string> thisWeek) BuildTodayAndWeekLines(IEnumerable<ProjectInfo> projects)
    {
        var today = new List<string>();
        var thisWeek = new List<string>();

        try
        {
            var tasks = _todayQueueService.GetAllTasksSorted(projects, limit: 50);
            foreach (var task in tasks)
            {
                if (task.SortBucket <= 1)
                    today.Add($"- [{task.ProjectShortName}] {task.DisplayTitle} ({task.DueLabel})");
                else if (task.SortBucket is 2 or 3)
                    thisWeek.Add($"- [{task.ProjectShortName}] {task.DisplayTitle} ({task.DueLabel})");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Standup] BuildTodayAndWeekLines failed: {ex.Message}");
        }

        return (today, thisWeek);
    }

    private static string GetFocusPreview(string filePath)
    {
        try
        {
            var (content, _) = EncodingDetector.ReadFile(filePath);
            var lines = content.Split('\n');
            bool inSection = false;

            foreach (var raw in lines)
            {
                var line = raw.TrimEnd('\r');
                if (Regex.IsMatch(line, @"^## (今やってること|Now doing)"))
                {
                    inSection = true;
                    continue;
                }
                if (inSection && Regex.IsMatch(line, @"^## "))
                    break;
                if (inSection && Regex.IsMatch(line, @"^\s*-\s+\S"))
                {
                    var preview = Regex.Replace(line, @"^\s*-\s+", "").Trim();
                    return preview.Length > 80 ? preview[..77] + "..." : preview;
                }
            }

            // fallback: 最初の意味のある行
            foreach (var raw in lines)
            {
                var trimmed = raw.TrimEnd('\r').Trim();
                if (trimmed.Length > 0
                    && !trimmed.StartsWith('#')
                    && !trimmed.StartsWith("<!--", StringComparison.Ordinal)
                    && !trimmed.StartsWith("---", StringComparison.Ordinal)
                    && trimmed != "-")
                {
                    return trimmed.Length > 80 ? trimmed[..77] + "..." : trimmed;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Standup] GetFocusPreview failed: {filePath}: {ex.Message}");
        }

        return "(No preview)";
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
