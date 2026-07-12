using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;
using Curia.Models;

namespace Curia.Services;

public class TodayQueueTask
{
    private static readonly Regex DisplayLeadingTagRx =
        new(@"^\[[^\]]+\]\s*", RegexOptions.Compiled);

    public string ProjectDisplayName { get; set; } = "";
    public string? WorkstreamId { get; set; }
    public string? WorkstreamLabel { get; set; }
    public string Title { get; set; } = "";
    public string? ParentTitle { get; set; }
    public DateTime? DueDate { get; set; }
    public string DueLabel { get; set; } = "";
    public string? AsanaUrl { get; set; }
    public string? AsanaTaskGid { get; set; }
    public bool IsSubtask { get; set; }
    public int SortBucket { get; set; }
    public int SortRank { get; set; }

    // [Mini][Domain] タグを除いた短いプロジェクト名 (Dashboard 表示用)
    public string ProjectShortName => ProjectDisplayName
        .Replace(" [Domain][Mini]", "")
        .Replace(" [Domain]", "")
        .Replace(" [Mini]", "");

    // Dashboard 表示用: 先頭タグ [XXX] を隠し、サブタスクは親タイトルを補助表示
    public string DisplayMainTitle
    {
        get
        {
            var title = NormalizeDisplayText(Title);
            return string.IsNullOrWhiteSpace(title) ? Title?.Trim() ?? "" : title;
        }
    }

    public string DisplayParentLabel
    {
        get
        {
            if (!IsSubtask || string.IsNullOrWhiteSpace(ParentTitle))
                return "";

            var parent = NormalizeDisplayText(ParentTitle);
            return string.IsNullOrWhiteSpace(parent) ? "" : $"  < {parent}";
        }
    }

    public string DisplayTitle => $"{DisplayMainTitle}{DisplayParentLabel}";

    public string DisplayText => $"[{ProjectShortName}] {DisplayTitle}";
    public bool HasWorkstream => !string.IsNullOrWhiteSpace(WorkstreamLabel);
    public string ProjectFilterLabel => HasWorkstream
        ? $"{ProjectShortName} / {WorkstreamLabel}"
        : ProjectShortName;
    public string? AsanaFilePath { get; set; }
    // ローカルタスク (GID なし) もファイルパスがあれば完了可能
    public bool CanComplete => !string.IsNullOrWhiteSpace(AsanaTaskGid)
                            || !string.IsNullOrWhiteSpace(AsanaFilePath);
    // ローカル専用かどうか (API 不要で完了可能)
    public bool IsLocalOnly => string.IsNullOrWhiteSpace(AsanaTaskGid);
    public bool HasAsanaUrl => !string.IsNullOrWhiteSpace(AsanaUrl);

    // Snooze の識別キー: Asana GID があればそれを使い、なければ "ProjectShortName|Title"
    public string SnoozeKey => !string.IsNullOrEmpty(AsanaTaskGid)
        ? AsanaTaskGid
        : $"{ProjectShortName}|{Title}";

    // "overdue" | "today" | "soon" | "normal"
    public string DueBucket => SortBucket switch
    {
        0 => "overdue",
        1 => "today",
        2 => "soon",
        _ => "normal"
    };

    private static string NormalizeDisplayText(string? s)
        => string.IsNullOrWhiteSpace(s)
            ? ""
            : DisplayLeadingTagRx.Replace(s, "").Trim();
}

public class TodayQueueService
{
    private readonly ConfigService _configService;
    private readonly FileEncodingService _fileEncodingService;
    private static readonly HttpClient _http = new();

    // Snooze 状態: key → snooze解除日時
    private Dictionary<string, DateTime> _snooze = [];
    private bool _snoozeLoaded;

    // PS1 の TabTodayQueue.ps1 から移植したパターン
    private static readonly Regex TopLevelAnyRx =
        new(@"^\s{0,2}-\s+\[[ x]\]\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex TopLevelUncheckedRx =
        new(@"^\s{0,2}-\s+\[ \]\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex SubtaskUncheckedRx =
        new(@"^\s{4}-\s+\[ \]\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex DueDateRx =
        new(@"\(Due:\s*(\d{4}-\d{2}-\d{2})\)", RegexOptions.Compiled);
    private static readonly Regex AsanaUrlRx =
        new(@"\[\[Asana\]\((https?://[^)]+)\)\]", RegexOptions.Compiled);
    private static readonly Regex AsanaGidRx =
        new(@"/(\d+)$", RegexOptions.Compiled);
    private static readonly Regex RoleTagRx =
        new(@"^\[(?:Owner|Collab|Other)\]\s*", RegexOptions.Compiled);
    private static readonly Regex ColaboTagRx =
        new(@"^\[Collab\]", RegexOptions.Compiled);
    private static readonly Regex OtherTagRx =
        new(@"^\[Other\]", RegexOptions.Compiled);
    private static readonly Regex InProgressHeadingRx =
        new(@"^\s*#{2,3}\s*In\s+Progress\b", RegexOptions.Compiled);
    private static readonly Regex DoneHeadingRx =
        new(@"^\s*#{2,3}\s*Completed\b", RegexOptions.Compiled);

    public TodayQueueService(ConfigService configService, FileEncodingService fileEncodingService)
    {
        _configService = configService;
        _fileEncodingService = fileEncodingService;
    }

    /// <summary>
    /// 指定プロジェクトの tasks.md から未完了タスクを抽出する。
    /// PS1: Get-TodayQueueTasksFromProject 相当。
    /// </summary>
    public List<TodayQueueTask> ParseTasksFromProject(ProjectInfo info)
    {
        var tasks = new List<TodayQueueTask>();

        var rootAsanaPath = Path.Combine(info.AiContextPath, "obsidian_notes", "tasks.md");
        tasks.AddRange(ParseTasksFromAsanaFile(rootAsanaPath, info.DisplayName));

        var workstreamsAsanaRoot = Path.Combine(info.AiContextPath, "obsidian_notes", "workstreams");
        if (Directory.Exists(workstreamsAsanaRoot))
        {
            // context 側で管理されている状態を優先して参照するが、
            // 実ディレクトリにだけ存在する workstream も拾う。
            var closedIds = info.Workstreams
                .Where(w => w.IsClosed)
                .Select(w => w.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var labelById = info.Workstreams
                .GroupBy(w => w.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => string.IsNullOrWhiteSpace(g.First().Label) ? g.Key : g.First().Label,
                    StringComparer.OrdinalIgnoreCase);

            foreach (var wsDir in Directory.EnumerateDirectories(workstreamsAsanaRoot))
            {
                var wsId = Path.GetFileName(wsDir);
                if (string.IsNullOrWhiteSpace(wsId)) continue;
                if (closedIds.Contains(wsId)) continue;

                var wsLabel = labelById.TryGetValue(wsId, out var mappedLabel) ? mappedLabel : wsId;
                var wsAsanaPath = Path.Combine(wsDir, "tasks.md");
                tasks.AddRange(ParseTasksFromAsanaFile(wsAsanaPath, info.DisplayName, wsId, wsLabel));
            }
        }

        return tasks;
    }

    private static List<TodayQueueTask> ParseTasksFromAsanaFile(
        string asanaPath,
        string projectDisplayName,
        string? workstreamId = null,
        string? workstreamLabel = null)
    {
        if (!File.Exists(asanaPath)) return [];

        string[] lines;
        try
        {
            lines = File.ReadAllLines(asanaPath, Encoding.UTF8);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TodayQueue] Failed to read '{asanaPath}': {ex.Message}");
            return [];
        }

        var normalizedWorkstreamLabel = string.IsNullOrWhiteSpace(workstreamLabel)
            ? workstreamId
            : workstreamLabel.Trim();

        var tasks = new List<TodayQueueTask>();
        bool inProgress = false;
        TodayQueueTask? currentParent = null;

        foreach (var line in lines)
        {
            if (InProgressHeadingRx.IsMatch(line)) { inProgress = true; currentParent = null; continue; }
            if (DoneHeadingRx.IsMatch(line)) { inProgress = false; currentParent = null; continue; }
            if (!inProgress) continue;

            // --- トップレベルタスク (チェック済み/未チェック両方でパース) ---
            var topAny = TopLevelAnyRx.Match(line);
            if (topAny.Success)
            {
                var body = topAny.Groups[1].Value.Trim();
                if (string.IsNullOrWhiteSpace(body) || body.StartsWith("<!-- Memo area"))
                {
                    currentParent = null;
                    continue;
                }

                var dueDate = ParseDueDate(body);
                var (asanaUrl, asanaGid) = ParseAsanaLink(body);
                var title = StripExtras(body);

                var task = new TodayQueueTask
                {
                    ProjectDisplayName = projectDisplayName,
                    WorkstreamId = workstreamId,
                    WorkstreamLabel = normalizedWorkstreamLabel,
                    Title = string.IsNullOrWhiteSpace(title) ? "(untitled task)" : title,
                    DueDate = dueDate,
                    AsanaUrl = asanaUrl,
                    AsanaTaskGid = asanaGid,
                    AsanaFilePath = asanaPath,
                    IsSubtask = false,
                };
                // [コラボ]/[他] の親でも currentParent に設定する。
                // これにより配下に [担当] サブタスクがある場合にそれを拾える。
                currentParent = task;

                // [コラボ]/[他] は親タスク自体はリストに追加しない。未チェックの [担当] のみ追加。
                if (!ColaboTagRx.IsMatch(body) && !OtherTagRx.IsMatch(body) && TopLevelUncheckedRx.IsMatch(line))
                    tasks.Add(task);
                continue;
            }

            // --- サブタスク (4スペースインデント, 未チェックのみ) ---
            if (currentParent != null)
            {
                var sub = SubtaskUncheckedRx.Match(line);
                if (sub.Success)
                {
                    var body = sub.Groups[1].Value.Trim();
                    if (string.IsNullOrWhiteSpace(body) || body.StartsWith("<!--")) continue;
                    if (ColaboTagRx.IsMatch(body) || OtherTagRx.IsMatch(body)) continue;

                    var dueDate = ParseDueDate(body) ?? currentParent.DueDate;
                    var (asanaUrl, asanaGid) = ParseAsanaLink(body);
                    var title = StripExtras(body);

                    tasks.Add(new TodayQueueTask
                    {
                        ProjectDisplayName = currentParent.ProjectDisplayName,
                        WorkstreamId = currentParent.WorkstreamId,
                        WorkstreamLabel = currentParent.WorkstreamLabel,
                        Title = string.IsNullOrWhiteSpace(title) ? "(untitled subtask)" : title,
                        ParentTitle = currentParent.Title,
                        DueDate = dueDate,
                        AsanaUrl = asanaUrl,
                        AsanaTaskGid = asanaGid,
                        AsanaFilePath = asanaPath,
                        IsSubtask = true,
                    });
                }
            }
        }

        return tasks;
    }

    // ---- Snooze 管理 ----

    private string SnoozeFilePath =>
        Path.Combine(_configService.ConfigDir, "today_queue_snooze.json");

    public void EnsureSnoozeLoaded()
    {
        if (_snoozeLoaded) return;
        _snoozeLoaded = true;
        try
        {
            if (!File.Exists(SnoozeFilePath)) return;
            var raw = File.ReadAllText(SnoozeFilePath, Encoding.UTF8);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(raw);
            if (dict == null) return;
            var now = DateTime.Now;
            foreach (var kv in dict)
            {
                if (DateTime.TryParse(kv.Value, out var dt) && dt > now)
                    _snooze[kv.Key] = dt;
            }
        }
        catch { }
    }

    private void SaveSnooze()
    {
        try
        {
            var now = DateTime.Now;
            var obj = _snooze
                .Where(kv => kv.Value > now)
                .ToDictionary(kv => kv.Key, kv => kv.Value.ToString("o"));
            var json = JsonSerializer.Serialize(obj);
            Directory.CreateDirectory(Path.GetDirectoryName(SnoozeFilePath)!);
            File.WriteAllText(SnoozeFilePath, json, Encoding.UTF8);
        }
        catch { }
    }

    public void SnoozeTask(string key)
    {
        EnsureSnoozeLoaded();
        _snooze[key] = DateTime.Today.AddDays(1); // 明日まで snooze
        SaveSnooze();
    }

    public void UnsnoozeAll()
    {
        _snooze.Clear();
        SaveSnooze();
    }

    public bool IsSnoozed(string key)
    {
        EnsureSnoozeLoaded();
        if (!_snooze.TryGetValue(key, out var until)) return false;
        if (until > DateTime.Now) return true;
        _snooze.Remove(key); // 期限切れを削除
        return false;
    }

    public int GetSnoozeCount(IEnumerable<TodayQueueTask> tasks)
        => tasks.Count(t => IsSnoozed(t.SnoozeKey));

    // ---- タスク取得 ----

    /// <summary>
    /// 全プロジェクトのタスクを収集し、優先度順にソートして上位 limit 件を返す。
    /// </summary>
    public List<TodayQueueTask> GetAllTasksSorted(IEnumerable<ProjectInfo> projects, int limit)
    {
        var all = new List<TodayQueueTask>();
        foreach (var p in projects)
            all.AddRange(ParseTasksFromProject(p));

        var today = DateTime.Today;
        foreach (var t in all)
        {
            var (bucket, rank, label) = GetPriority(t.DueDate, today);
            t.SortBucket = bucket;
            t.SortRank = rank;
            t.DueLabel = label;
        }

        return [.. all
            .OrderBy(t => t.SortBucket)
            .ThenBy(t => t.SortRank)
            .ThenBy(t => t.ProjectDisplayName)
            .ThenBy(t => t.Title)
            .Take(limit)];
    }

    /// <summary>
    /// Asana API で指定タスクを完了にする。
    /// PS1: Invoke-TodayQueueCompleteAsanaTask 相当。
    /// </summary>
    public async Task<(bool Success, string Message)> CompleteAsanaTaskAsync(string taskGid)
        => await SetAsanaTaskCompletedAsync(taskGid, true);

    /// <summary>Asana タスクの完了状態を更新する。Agent の Undo 操作でも使用する。</summary>
    public async Task<(bool Success, string Message)> SetAsanaTaskCompletedAsync(string taskGid, bool completed, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskGid))
            return (false, "Asana task GID が見つかりません。");

        var token = GetAsanaToken();
        if (string.IsNullOrWhiteSpace(token))
            return (false, "ASANA_TOKEN 環境変数または _config/asana_global.json の asana_token が未設定です。");

        try
        {
            var uri = $"https://app.asana.com/api/1.0/tasks/{taskGid}";
            var body = JsonSerializer.Serialize(new { data = new { completed } });
            var req = new HttpRequestMessage(HttpMethod.Put, uri)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
                return (true, completed
                    ? $"Asana タスクを完了にしました ({taskGid})"
                    : $"Asana タスクを未完了に戻しました ({taskGid})");

            var err = await resp.Content.ReadAsStringAsync(ct);
            var snippet = err.Length > 200 ? err[..200] : err;
            return (false, $"Asana API エラー: {(int)resp.StatusCode} {snippet}");
        }
        catch (Exception ex)
        {
            return (false, $"Asana 通信エラー: {ex.Message}");
        }
    }

    /// <summary>
    /// Asana API でタスクの期日を更新する。dueAt が空でない場合は due_at、そうでなければ due_on を使用。
    /// </summary>
    public async Task<(bool Success, string Message)> UpdateDueDateAsync(
        string taskGid, string dueOn, string dueAt = "")
    {
        if (string.IsNullOrWhiteSpace(taskGid))
            return (false, "Asana task GID not found.");

        var token = GetAsanaToken();
        if (string.IsNullOrWhiteSpace(token))
            return (false, "Asana token not configured.");

        try
        {
            var uri  = $"https://app.asana.com/api/1.0/tasks/{taskGid}";
            object payload;
            if (!string.IsNullOrWhiteSpace(dueAt))
                payload = new { data = new { due_at = dueAt } };
            else if (!string.IsNullOrWhiteSpace(dueOn))
                payload = new { data = new { due_on = dueOn } };
            else
                payload = new { data = new { due_on = (string?)null } };

            var body = JsonSerializer.Serialize(payload);
            var req  = new HttpRequestMessage(HttpMethod.Put, uri)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var resp = await _http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
                return (true, "Due date updated.");

            var err     = await resp.Content.ReadAsStringAsync();
            var snippet = err.Length > 200 ? err[..200] : err;
            return (false, $"Asana API error: {(int)resp.StatusCode} {snippet}");
        }
        catch (Exception ex)
        {
            return (false, $"Asana API error: {ex.Message}");
        }
    }

    /// <summary>
    /// tasks.md 内の該当タスク行の (Due: ...) を更新・挿入・削除する。
    /// dueOn が空なら既存の Due 記述を除去する。
    /// </summary>
    public void UpdateDueDateInFile(TodayQueueTask task, string dueOn)
    {
        if (string.IsNullOrWhiteSpace(task.AsanaFilePath) || !File.Exists(task.AsanaFilePath)) return;
        if (string.IsNullOrWhiteSpace(task.AsanaTaskGid)) return;

        try
        {
            var (content, encoding) = _fileEncodingService.ReadFile(task.AsanaFilePath);
            var gid = Regex.Escape(task.AsanaTaskGid);
            string updated;

            if (string.IsNullOrWhiteSpace(dueOn))
            {
                // Due 削除: GID を含む行から (Due: ...) を取り除く
                var removePattern = @"(?m)^([ \t]*-\s+\[[ x]\].*" + gid + @".*?)\s*\(Due:\s*\d{4}-\d{2}-\d{2}\)(.*)$";
                updated = Regex.Replace(content, removePattern, "$1$2",
                    RegexOptions.None, TimeSpan.FromSeconds(5));
            }
            else
            {
                // 既存の (Due: ...) を置換
                var replacePattern = @"(?m)^([ \t]*-\s+\[[ x]\].*" + gid + @".*?)\(Due:\s*\d{4}-\d{2}-\d{2}\)(.*)$";
                updated = Regex.Replace(content, replacePattern, $"$1(Due: {dueOn})$2",
                    RegexOptions.None, TimeSpan.FromSeconds(5));

                if (updated == content)
                {
                    // Due がない行 → [[Asana...]] の直前に挿入
                    var insertPattern = @"(?m)^([ \t]*-\s+\[[ x]\].*" + gid + @".*?)(\s*\[\[Asana\].*)$";
                    updated = Regex.Replace(content, insertPattern, $"$1 (Due: {dueOn})$2",
                        RegexOptions.None, TimeSpan.FromSeconds(5));
                }
            }

            if (updated != content)
                _fileEncodingService.WriteFile(task.AsanaFilePath, updated, encoding);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TodayQueue] UpdateDueDateInFile error: {ex}");
        }
    }

    /// <summary>
    /// tasks.md 内の該当タスク行のチェックボックスを [ ] から [x] に更新する。
    /// </summary>
    public void MarkTaskCompletedInFile(TodayQueueTask task)
    {
        if (string.IsNullOrWhiteSpace(task.AsanaFilePath) || !File.Exists(task.AsanaFilePath))
            return;
        if (string.IsNullOrWhiteSpace(task.AsanaTaskGid))
            return;

        try
        {
            var (content, encoding) = _fileEncodingService.ReadFile(task.AsanaFilePath);
            // GID を含む行の [ ] を [x] に置換 (GIDは一意のため最初の1件のみ)
            var pattern = @"(?m)^([ \t]*-\s+)\[ \](.*" + Regex.Escape(task.AsanaTaskGid) + @".*)$";
            var updated = Regex.Replace(content, pattern, "$1[x]$2", RegexOptions.None, TimeSpan.FromSeconds(5));
            if (updated != content)
                _fileEncodingService.WriteFile(task.AsanaFilePath, updated, encoding);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TodayQueue] MarkTaskCompletedInFile error: {ex}");
        }
    }

    /// <summary>
    /// ローカルタスク (GID なし) のチェックボックスを [ ] → [x] に更新する。
    /// タイトルの完全一致で対象行を特定する。
    /// </summary>
    public void MarkLocalTaskCompletedInFile(TodayQueueTask task)
    {
        if (string.IsNullOrWhiteSpace(task.AsanaFilePath) || !File.Exists(task.AsanaFilePath))
            return;

        try
        {
            var (content, encoding) = _fileEncodingService.ReadFile(task.AsanaFilePath);
            var escapedTitle = Regex.Escape(task.Title.Trim());
            var pattern = @"(?m)^([ \t]*-\s+)\[ \](\s+" + escapedTitle + @"\s*(?:\([^)]*\))?\s*)$";
            var updated = Regex.Replace(content, pattern, "$1[x]$2",
                RegexOptions.None, TimeSpan.FromSeconds(5));
            if (updated != content)
                _fileEncodingService.WriteFile(task.AsanaFilePath, updated, encoding);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TodayQueue] MarkLocalTaskCompletedInFile error: {ex}");
        }
    }

    // PS1: Get-TodayQueueAsanaToken 相当
    private string GetAsanaToken()
    {
        var envToken = Environment.GetEnvironmentVariable("ASANA_TOKEN");
        if (!string.IsNullOrWhiteSpace(envToken)) return envToken;

        return _configService.LoadAsanaGlobalConfig().AsanaToken?.Trim() ?? "";
    }

    private static DateTime? ParseDueDate(string body)
    {
        var m = DueDateRx.Match(body);
        if (!m.Success) return null;
        if (DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd", null,
            System.Globalization.DateTimeStyles.None, out var d))
            return d;
        return null;
    }

    private static (string? Url, string? Gid) ParseAsanaLink(string body)
    {
        var m = AsanaUrlRx.Match(body);
        if (!m.Success) return (null, null);
        var url = m.Groups[1].Value;
        var urlBase = url.Split('?')[0].TrimEnd('/');
        var gidMatch = AsanaGidRx.Match(urlBase);
        var gid = gidMatch.Success ? gidMatch.Groups[1].Value : null;
        return (url, gid);
    }

    private static string StripExtras(string body)
    {
        var s = AsanaUrlRx.Replace(body, "").Trim();
        s = DueDateRx.Replace(s, "").Trim();
        s = RoleTagRx.Replace(s, "");
        return s.Trim();
    }

    // PS1: Get-TodayQueuePriority 相当
    private static (int Bucket, int Rank, string Label) GetPriority(DateTime? dueDate, DateTime today)
    {
        if (dueDate == null) return (5, 9999, "No due");
        int days = (int)(dueDate.Value.Date - today).TotalDays;
        if (days < 0) return (0, Math.Abs(days), $"Overdue {Math.Abs(days)}d");
        if (days == 0) return (1, 0, "Today");
        if (days <= 2) return (2, days, $"In {days}d");
        if (days <= 7) return (3, days, $"In {days}d");
        return (4, days, $"In {days}d");
    }
}
