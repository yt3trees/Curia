using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Curia.Models;

namespace Curia.Services;

public enum PomodoroState { Idle, Running, Paused, Break }

public class PomodoroSession
{
    public DateTime StartAt { get; set; }
    public int DurationMinutes { get; set; }
    public string ProjectKey { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string? TaskTitle { get; set; }
    public string? Note { get; set; }
    public bool Completed { get; set; }
    public bool IsBreak { get; set; }
}

public record PomodoroDailySummary(
    DateTime Date,
    int CompletedSessions,
    int InterruptedSessions,
    int TotalFocusMinutes,
    double CompletionRate);

public class PomodoroService : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private const int ArchiveDaysThreshold = 30;

    private readonly ConfigService _configService;
    private readonly ProjectDiscoveryService _discoveryService;

    private System.Threading.Timer? _tickTimer;
    private PomodoroSession? _currentSession;
    private DateTime _sessionEndAt;
    private TimeSpan _pausedRemaining = TimeSpan.Zero;

    public PomodoroState State { get; private set; } = PomodoroState.Idle;
    public PomodoroSession? CurrentSession => _currentSession;
    public TimeSpan Remaining => State switch
    {
        PomodoroState.Running or PomodoroState.Break =>
            _sessionEndAt - DateTime.Now > TimeSpan.Zero ? _sessionEndAt - DateTime.Now : TimeSpan.Zero,
        PomodoroState.Paused => _pausedRemaining,
        _ => TimeSpan.Zero
    };

    public event Action<TimeSpan>? Tick;
    public event Action<PomodoroSession>? SessionCompleted;

    public PomodoroService(ConfigService configService, ProjectDiscoveryService discoveryService)
    {
        _configService = configService;
        _discoveryService = discoveryService;
    }

    // ── タイマー操作 ────────────────────────────────────────────────────────

    public void Start(PomodoroSession session)
    {
        _currentSession = session;
        _currentSession.StartAt = DateTime.Now;
        _sessionEndAt = DateTime.Now.AddMinutes(session.DurationMinutes);
        State = session.IsBreak ? PomodoroState.Break : PomodoroState.Running;

        _tickTimer?.Dispose();
        _tickTimer = new System.Threading.Timer(_ => OnTick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    public void Pause()
    {
        if (State != PomodoroState.Running) return;
        _pausedRemaining = _sessionEndAt - DateTime.Now;
        if (_pausedRemaining < TimeSpan.Zero) _pausedRemaining = TimeSpan.Zero;
        State = PomodoroState.Paused;
        _tickTimer?.Dispose();
        _tickTimer = null;
    }

    public void Resume()
    {
        if (State != PomodoroState.Paused || _currentSession == null) return;
        _sessionEndAt = DateTime.Now + _pausedRemaining;
        State = PomodoroState.Running;
        _tickTimer = new System.Threading.Timer(_ => OnTick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    public void Interrupt()
    {
        if (_currentSession == null) return;
        _currentSession.Completed = false;
        _ = SaveSessionAsync(_currentSession);
        ResetState();
    }

    public void FinishEarly()
    {
        if (_currentSession == null || State is not (PomodoroState.Running or PomodoroState.Paused)) return;

        _tickTimer?.Dispose();
        _tickTimer = null;

        var elapsed = (int)Math.Ceiling((DateTime.Now - _currentSession.StartAt).TotalMinutes);
        _currentSession.DurationMinutes = Math.Max(1, elapsed);
        _currentSession.Completed = true;
        State = PomodoroState.Idle;

        var session = _currentSession;
        _currentSession = null;

        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            SessionCompleted?.Invoke(session));
    }

    public void ResetState()
    {
        _tickTimer?.Dispose();
        _tickTimer = null;
        _currentSession = null;
        State = PomodoroState.Idle;
    }

    private void OnTick()
    {
        var remaining = _sessionEndAt - DateTime.Now;
        if (remaining <= TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
            DispatchTick(remaining);
            OnSessionCompleted();
        }
        else
        {
            DispatchTick(remaining);
        }
    }

    private void DispatchTick(TimeSpan remaining)
    {
        try
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => Tick?.Invoke(remaining));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PomodoroService] DispatchTick failed: {ex.Message}");
        }
    }

    private void OnSessionCompleted()
    {
        _tickTimer?.Dispose();
        _tickTimer = null;

        if (_currentSession == null) return;
        _currentSession.Completed = true;
        State = PomodoroState.Idle;

        var session = _currentSession;
        _currentSession = null;

        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            SessionCompleted?.Invoke(session));
    }

    // ── ログ書き込み ────────────────────────────────────────────────────────

    public async Task SaveSessionAsync(PomodoroSession session, CancellationToken ct = default)
    {
        if (session.IsBreak) return;

        var project = await GetProjectByKeyAsync(session.ProjectKey);
        if (project == null)
        {
            Debug.WriteLine($"[PomodoroService] Project not found: {session.ProjectKey}");
            return;
        }

        var dir = Path.Combine(project.AiContextContentPath, "focus_history", "pomodoro");
        Directory.CreateDirectory(dir);

        var logPath = Path.Combine(dir, $"{session.StartAt:yyyy-MM-dd}.md");
        var line = BuildLogLine(session);

        await AppendToLogAsync(logPath, session.StartAt, line, ct);
    }

    private static string BuildLogLine(PomodoroSession session)
    {
        var state = session.Completed ? "completed" : "interrupted";
        var note = string.IsNullOrWhiteSpace(session.Note) ? "" : $" — {session.Note.Trim()}";
        var task = string.IsNullOrWhiteSpace(session.TaskTitle) ? "" : $" / {session.TaskTitle}";
        return $"- {session.StartAt:HH:mm} [{session.ProjectName}{task}] {session.DurationMinutes}min {state}{note}";
    }

    private async Task AppendToLogAsync(string logPath, DateTime date, string line, CancellationToken ct)
    {
        string existing = "";
        if (File.Exists(logPath))
            existing = await File.ReadAllTextAsync(logPath, ct);

        string header = $"# Pomodoro Log {date:yyyy-MM-dd}";
        string sessionsHeader = "## Sessions";

        if (!existing.Contains(sessionsHeader))
        {
            existing = $"{header}\n\n{sessionsHeader}\n";
        }

        // ## Sessions の後に追記
        var insertIdx = existing.IndexOf(sessionsHeader, StringComparison.Ordinal) + sessionsHeader.Length;
        var after = existing[insertIdx..].TrimStart('\n');

        // Summary セクションが存在する場合はその前に挿入
        var summaryIdx = after.IndexOf("## Summary", StringComparison.Ordinal);
        if (summaryIdx >= 0)
        {
            var beforeSummary = after[..summaryIdx].TrimEnd();
            var fromSummary = after[summaryIdx..];
            existing = $"{existing[..insertIdx]}\n{beforeSummary}\n{line}\n\n{fromSummary}";
        }
        else
        {
            existing = existing.TrimEnd() + "\n" + line + "\n";
        }

        // Summary を更新
        existing = RebuildSummary(existing, date);

        await File.WriteAllTextAsync(logPath, existing, Utf8NoBom, ct);
    }

    private static string RebuildSummary(string content, DateTime date)
    {
        // Sessions 行をパース
        var lines = content.Split('\n');
        var sessionLines = lines
            .Where(l => l.StartsWith("- ", StringComparison.Ordinal) && l.Contains("min "))
            .ToList();

        int completed = sessionLines.Count(l => l.Contains("completed"));
        int interrupted = sessionLines.Count(l => l.Contains("interrupted"));
        int totalFocus = 0;

        foreach (var l in sessionLines.Where(l => l.Contains("completed") || l.Contains("interrupted")))
        {
            var m = System.Text.RegularExpressions.Regex.Match(l, @"(\d+)min");
            if (m.Success) totalFocus += int.Parse(m.Groups[1].Value);
        }

        int total = completed + interrupted;
        double rate = total > 0 ? completed * 100.0 / total : 0;
        var summary =
            $"\n## Summary\n" +
            $"- Total sessions: {completed} completed, {interrupted} interrupted\n" +
            $"- Focus time: {totalFocus} min\n" +
            $"- Completion rate: {rate:F1}%\n";

        // 既存の Summary を置換
        var summaryStart = content.IndexOf("\n## Summary", StringComparison.Ordinal);
        return summaryStart >= 0
            ? content[..summaryStart] + summary
            : content.TrimEnd() + summary;
    }

    // ── サマリー取得 ────────────────────────────────────────────────────────

    public async Task<PomodoroDailySummary?> GetDaySummaryAsync(DateTime date, CancellationToken ct = default)
    {
        var projects = await Task.Run(() => _discoveryService.GetProjectInfoList(), ct);
        int completed = 0, interrupted = 0, totalFocus = 0;

        foreach (var proj in projects)
        {
            var logPath = Path.Combine(proj.AiContextContentPath, "focus_history", "pomodoro", $"{date:yyyy-MM-dd}.md");
            if (!File.Exists(logPath)) continue;

            try
            {
                var text = await File.ReadAllTextAsync(logPath, ct);
                var sessionLines = text.Split('\n')
                    .Where(l => l.StartsWith("- ", StringComparison.Ordinal) && l.Contains("min "));

                foreach (var line in sessionLines)
                {
                    if (line.Contains("completed")) completed++;
                    else if (line.Contains("interrupted")) interrupted++;

                    var m = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)min");
                    if (m.Success) totalFocus += int.Parse(m.Groups[1].Value);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PomodoroService] GetDaySummaryAsync failed: {ex.Message}");
            }
        }

        if (completed == 0 && interrupted == 0) return null;
        int total = completed + interrupted;
        return new PomodoroDailySummary(date, completed, interrupted, totalFocus,
            total > 0 ? completed * 100.0 / total : 0);
    }

    // プロジェクト単位のサマリー (Dashboard インジケーター用)
    public async Task<PomodoroDailySummary?> GetProjectDaySummaryAsync(
        ProjectInfo project, DateTime date, CancellationToken ct = default)
    {
        var logPath = Path.Combine(project.AiContextContentPath, "focus_history", "pomodoro", $"{date:yyyy-MM-dd}.md");
        if (!File.Exists(logPath)) return null;

        try
        {
            var text = await File.ReadAllTextAsync(logPath, ct);
            int completed = 0, interrupted = 0, totalFocus = 0;

            foreach (var line in text.Split('\n')
                         .Where(l => l.StartsWith("- ", StringComparison.Ordinal) && l.Contains("min ")))
            {
                if (line.Contains("completed")) completed++;
                else if (line.Contains("interrupted")) interrupted++;
                var m = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)min");
                if (m.Success) totalFocus += int.Parse(m.Groups[1].Value);
            }

            if (completed == 0 && interrupted == 0) return null;
            int total = completed + interrupted;
            return new PomodoroDailySummary(date, completed, interrupted, totalFocus,
                total > 0 ? completed * 100.0 / total : 0);
        }
        catch
        {
            return null;
        }
    }

    // ── 月次アーカイブ ──────────────────────────────────────────────────────

    public async Task ArchiveOldLogsAsync(CancellationToken ct = default)
    {
        try
        {
            var projects = await Task.Run(() => _discoveryService.GetProjectInfoList(), ct);
            foreach (var proj in projects)
                await ArchiveProjectLogsAsync(proj, ct);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PomodoroService] ArchiveOldLogsAsync failed: {ex.Message}");
        }
    }

    private static async Task ArchiveProjectLogsAsync(ProjectInfo proj, CancellationToken ct)
    {
        var dir = Path.Combine(proj.AiContextContentPath, "focus_history", "pomodoro");
        if (!Directory.Exists(dir)) return;

        var cutoff = DateTime.Today.AddDays(-ArchiveDaysThreshold);
        var files = Directory.EnumerateFiles(dir, "????-??-??.md")
            .Where(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f);
                return DateTime.TryParseExact(name, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d < cutoff;
            })
            .ToList();

        if (files.Count == 0) return;

        // 月ごとにグループ化してアーカイブファイルに追記
        foreach (var group in files.GroupBy(f => f[^10..^6] + "-" + f[^7..^5]))
        {
            var yearMonth = group.Key.Replace("-", "").Length == 6 ? group.Key : group.First()[^10..^4];
            var archivePath = Path.Combine(dir, $"{yearMonth}.md");

            var sb = new StringBuilder();
            if (File.Exists(archivePath))
                sb.AppendLine(await File.ReadAllTextAsync(archivePath, ct));

            foreach (var file in group.OrderBy(f => f))
            {
                var content = await File.ReadAllTextAsync(file, ct);
                sb.AppendLine(content);
                sb.AppendLine("---");
                File.Delete(file);
            }

            await File.WriteAllTextAsync(archivePath, sb.ToString(), Utf8NoBom, ct);
            Debug.WriteLine($"[PomodoroService] Archived to {archivePath}");
        }
    }

    // ── ヘルパー ────────────────────────────────────────────────────────────

    private async Task<ProjectInfo?> GetProjectByKeyAsync(string projectKey)
    {
        if (string.IsNullOrWhiteSpace(projectKey)) return null;
        var all = await Task.Run(() => _discoveryService.GetProjectInfoList());
        return all.FirstOrDefault(p => p.HiddenKey == projectKey || p.Name == projectKey);
    }

    public void Dispose()
    {
        _tickTimer?.Dispose();
        _tickTimer = null;
    }
}
