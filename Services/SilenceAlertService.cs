using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.Messaging;
using Curia.Models;

namespace Curia.Services;

public class SilenceAlertService : IDisposable
{
    private readonly ConfigService _configService;
    private readonly ProjectDiscoveryService _discoveryService;
    private readonly TodayQueueService _todayQueueService;
    private readonly LlmClientService _llmClient;
    private System.Threading.Timer? _timer;

    public event Action<List<SilenceAlert>>? AlertsUpdated;
    public List<SilenceAlert> CurrentAlerts { get; private set; } = [];

    public SilenceAlertService(
        ConfigService configService,
        ProjectDiscoveryService discoveryService,
        TodayQueueService todayQueueService,
        LlmClientService llmClient)
    {
        _configService = configService;
        _discoveryService = discoveryService;
        _todayQueueService = todayQueueService;
        _llmClient = llmClient;

        var settings = _configService.LoadSettings();
        if (settings.SilenceAlertEnabled)
        {
            var state = LoadState();
            var now = DateTime.Now;
            CurrentAlerts = FilterAlerts(state.Alerts, state, now);
        }

        WeakReferenceMessenger.Default.Register<SilenceAlertEnabledChangedMessage>(this,
            (_, msg) =>
            {
                if (!msg.Enabled)
                {
                    CurrentAlerts = [];
                    AlertsUpdated?.Invoke(CurrentAlerts);
                }
            });
    }

    public void StartScheduler()
    {
        _timer = new System.Threading.Timer(_ => _ = TryRunAsync(),
                                            null, TimeSpan.FromSeconds(30), TimeSpan.FromHours(1));
    }

    public async Task TryRunAsync()
    {
        try
        {
            if (DateTime.Now.Hour < 6) return;
            var state = LoadState();
            if ((DateTime.Now - state.LastRunAt).TotalHours < 20) return;
            await RunDetectionAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SilenceAlert] TryRunAsync failed: {ex}");
        }
    }

    public async Task ForceRefreshAsync()
    {
        try
        {
            await RunDetectionAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SilenceAlert] ForceRefreshAsync failed: {ex}");
        }
    }

    private async Task RunDetectionAsync()
    {
        var settings = _configService.LoadSettings();
        if (!settings.AiEnabled || !settings.SilenceAlertEnabled)
        {
            Debug.WriteLine("[SilenceAlert] AI or SilenceAlert disabled, skipping");
            return;
        }

        var allProjects = await _discoveryService.GetProjectInfoListAsync(force: false);
        var hiddenKeys = _configService.LoadHiddenProjects();
        var candidates = allProjects
            .Where(p => !hiddenKeys.Contains(p.HiddenKey))
            .ToList();

        if (candidates.Count == 0 || candidates.Count > 50)
        {
            Debug.WriteLine($"[SilenceAlert] Skipping: {candidates.Count} candidates (0 or >50)");
            return;
        }

        var pinnedProjects = _configService.LoadPinnedFolders()
            .Select(pf => pf.Project)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var allTasks = await Task.Run(() => _todayQueueService.GetAllTasksSorted(candidates, 10000));
        var tasksByProject = allTasks
            .GroupBy(t => t.ProjectShortName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var today = DateTime.Today;
        var tableLines = new StringBuilder();
        tableLines.AppendLine("ProjectId|DisplayName|LastEditDays|LastFocusUpdateDays|OpenTaskCount|OverdueCount|NextDueDays|UncommittedFiles|LastDecisionDays|RecentMentionCount|IsPinned|Tier|FocusSummary");

        foreach (var p in candidates)
        {
            var tasks = tasksByProject.TryGetValue(p.Name, out var ts) ? ts : [];
            var openCount = tasks.Count;
            var overdueCount = tasks.Count(t => t.SortBucket == 0);
            int? nextDueDays = tasks
                .Where(t => t.DueDate.HasValue && t.DueDate.Value > today)
                .Select(t => (int)(t.DueDate!.Value - today).TotalDays)
                .OrderBy(d => d)
                .Cast<int?>()
                .FirstOrDefault();
            int? lastDecisionDays = p.DecisionLogDates.Count > 0
                ? (int)(today - p.DecisionLogDates.Max()).TotalDays
                : null;
            int recentMentions = CountRecentMentions(p.Name, settings.ObsidianVaultRoot);
            bool isPinned = pinnedProjects.Contains(p.Name);
            string focusSummary = ReadFocusSummary(p.FocusFile);

            tableLines.AppendLine(
                $"{p.Name}|{p.Name}|{p.FocusAge?.ToString() ?? "null"}|{p.SummaryAge?.ToString() ?? "null"}" +
                $"|{openCount}|{overdueCount}|{nextDueDays?.ToString() ?? "null"}" +
                $"|{p.UncommittedRepoPaths.Count}|{lastDecisionDays?.ToString() ?? "null"}" +
                $"|{recentMentions}|{isPinned}|{p.Tier}|{focusSummary}");
        }

        var systemPrompt =
            "あなたはプロジェクト忘却検知 AI です。以下のテーブルから「ユーザーが忘れかけている可能性が高い」案件を最大 3 件選び、JSON で返してください。\n\n" +
            "判定の重み:\n" +
            "- 単に古いだけの案件は選ばない\n" +
            "- OverdueCount > 0 または NextDueDays <= 3 は最優先\n" +
            "- LastEditDays が大きいのに RecentMentionCount > 0 は要注意 (周りは動いてる)\n" +
            "- UncommittedFiles > 0 かつ LastEditDays > 7 は中断された作業の疑い\n" +
            "- IsPinned=true の案件は見逃しが致命的、閾値を緩めに\n" +
            "- FocusSummary に期限・ブロッカー・未完了タスクの言及があれば重要シグナル\n\n" +
            "出力は次の JSON スキーマに厳密に従う:\n" +
            "[{ \"project_id\": \"...\", \"severity\": \"high|medium|low\", \"reason\": \"...\" }]\n" +
            "reason は 60 文字以内の日本語 1 行。複数信号を根拠として言及すること。";

        var userPrompt = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(settings.LlmUserProfile))
        {
            userPrompt.AppendLine("# User Profile");
            userPrompt.AppendLine(settings.LlmUserProfile);
            userPrompt.AppendLine();
        }
        userPrompt.AppendLine("# Projects");
        userPrompt.Append(tableLines);

        string raw;
        try
        {
            raw = await _llmClient.ChatCompletionAsync(systemPrompt, userPrompt.ToString());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SilenceAlert] LLM call failed: {ex.Message}");
            return;
        }

        var jsonText = ExtractJsonArray(raw);
        List<LlmAlertItem> items;
        try
        {
            items = JsonSerializer.Deserialize<List<LlmAlertItem>>(jsonText,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SilenceAlert] JSON parse failed: {ex.Message}\nRaw: {raw}");
            return;
        }

        var projectLookup = candidates.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var detectedAt = DateTime.Now;
        var newAlerts = items
            .Where(i => !string.IsNullOrWhiteSpace(i.ProjectId) && projectLookup.ContainsKey(i.ProjectId!))
            .Select(i => new SilenceAlert
            {
                ProjectId = i.ProjectId!,
                ProjectDisplayName = projectLookup[i.ProjectId!].DisplayName,
                Severity = ParseSeverity(i.Severity),
                Reason = i.Reason ?? "",
                DetectedAt = detectedAt,
            })
            .ToList();

        var state = LoadState();
        state.LastRunAt = detectedAt;
        state.Alerts = newAlerts;
        SaveState(state);

        CurrentAlerts = FilterAlerts(newAlerts, state, detectedAt);
        AlertsUpdated?.Invoke(CurrentAlerts);
        Debug.WriteLine($"[SilenceAlert] Detection complete: {CurrentAlerts.Count} alerts");
    }

    public void Dismiss(string projectId)
    {
        var state = LoadState();
        state.DismissedUntil[projectId] = DateTime.Now.AddDays(7);
        SaveState(state);
        CurrentAlerts = CurrentAlerts.Where(a => a.ProjectId != projectId).ToList();
        AlertsUpdated?.Invoke(CurrentAlerts);
    }

    public void Snooze(string projectId)
    {
        var state = LoadState();
        state.SnoozedUntil[projectId] = DateTime.Today.AddDays(1).AddHours(6);
        SaveState(state);
        CurrentAlerts = CurrentAlerts.Where(a => a.ProjectId != projectId).ToList();
        AlertsUpdated?.Invoke(CurrentAlerts);
    }

    private static List<SilenceAlert> FilterAlerts(
        List<SilenceAlert> alerts, SilenceAlertState state, DateTime now)
        => alerts
            .Where(a => !state.DismissedUntil.TryGetValue(a.ProjectId, out var du) || now < du)
            .Where(a => !state.SnoozedUntil.TryGetValue(a.ProjectId, out var su) || now < su)
            .ToList();

    private static string ReadFocusSummary(string? focusFile)
    {
        if (string.IsNullOrWhiteSpace(focusFile) || !File.Exists(focusFile)) return "(no focus)";
        try
        {
            var lines = File.ReadLines(focusFile)
                .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#'))
                .Take(5)
                .ToList();
            var summary = string.Join(" / ", lines);
            // パイプ区切りテーブルを壊さないよう | を除去、150 字に切り詰める
            summary = summary.Replace('|', '｜');
            return summary.Length > 150 ? summary[..150] + "…" : summary;
        }
        catch { return "(read error)"; }
    }

    private static int CountRecentMentions(string projectName, string obsidianVaultRoot)
    {
        if (string.IsNullOrWhiteSpace(obsidianVaultRoot)) return 0;
        var standupDir = Path.Combine(obsidianVaultRoot, "standup");
        if (!Directory.Exists(standupDir)) return 0;

        var count = 0;
        var escaped = Regex.Escape(projectName);
        for (int i = 0; i < 7; i++)
        {
            var file = Path.Combine(standupDir, $"{DateTime.Today.AddDays(-i):yyyy-MM-dd}_standup.md");
            if (!File.Exists(file)) continue;
            try
            {
                var content = File.ReadAllText(file);
                count += Regex.Matches(content, escaped, RegexOptions.IgnoreCase).Count;
            }
            catch { }
        }
        return count;
    }

    private static string ExtractJsonArray(string raw)
    {
        var start = raw.IndexOf('[');
        var end = raw.LastIndexOf(']');
        if (start < 0 || end < 0 || end <= start) return "[]";
        return raw[start..(end + 1)];
    }

    private static SilenceSeverity ParseSeverity(string? s) => s?.ToLowerInvariant() switch
    {
        "high" => SilenceSeverity.High,
        "medium" => SilenceSeverity.Medium,
        _ => SilenceSeverity.Low,
    };

    private string StatePath => Path.Combine(_configService.ConfigDir, "silence_alerts.json");

    private SilenceAlertState LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
                return JsonSerializer.Deserialize<SilenceAlertState>(File.ReadAllText(StatePath))
                    ?? new SilenceAlertState();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SilenceAlert] LoadState error: {ex.Message}");
        }
        return new SilenceAlertState();
    }

    private void SaveState(SilenceAlertState state)
    {
        try
        {
            File.WriteAllText(StatePath,
                JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SilenceAlert] SaveState error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
        _timer?.Dispose();
        _timer = null;
    }

    private sealed class LlmAlertItem
    {
        [JsonPropertyName("project_id")]
        public string? ProjectId { get; set; }
        public string? Severity { get; set; }
        public string? Reason { get; set; }
    }
}
