using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Curia.Helpers;
using Curia.Models;

namespace Curia.Services;

/// <summary>
/// Global Capture 機能のオーケストレーションサービス。
/// AI 分類、Asana 起票、ファイル追記を担う。
/// </summary>
public class CaptureService
{
    private readonly LlmClientService _llm;
    private readonly ConfigService _configService;
    private readonly FileEncodingService _encoding;
    private readonly ProjectDiscoveryService _discovery;

    private static readonly HttpClient Http = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // --- 重複起票ガード: project+summary+bodyhash → task gid ---
    private readonly Dictionary<string, (string gid, DateTime at)> _recentCreations = [];
    private const int DuplicateWindowSec = 120;

    // --- Asana メタデータ短期キャッシュ (10分) ---
    private readonly Dictionary<string, (AsanaProjectMeta meta, DateTime at)> _projectMetaCache = [];
    private readonly Dictionary<string, (List<AsanaSectionMeta> sections, DateTime at)> _sectionCache = [];
    private const int MetaCacheTtlMin = 10;

    public CaptureService(
        LlmClientService llm,
        ConfigService configService,
        FileEncodingService encoding,
        ProjectDiscoveryService discovery)
    {
        _llm = llm;
        _configService = configService;
        _encoding = encoding;
        _discovery = discovery;
    }

    // ─────────────────────────────────────────────────────────
    // Classification
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// AI でテキストを分類する。AI 無効時は呼ばず、手動分類パスを使うこと。
    /// </summary>
    public async Task<CaptureClassification> ClassifyAsync(
        string input,
        string? projectHint,
        CancellationToken ct = default)
    {
        var projects = await _discovery.GetProjectInfoListAsync(ct: ct);
        var systemPrompt = BuildSystemPrompt(projects);
        // BuildUserPrompt は各プロジェクトのフォーカスファイルを同期読み込みするため
        // バックグラウンドスレッドで実行して UI をブロックしない
        var userPrompt = await Task.Run(() => BuildUserPrompt(input, projectHint, projects), ct);

        string raw;
        try
        {
            raw = await _llm.ChatCompletionAsync(systemPrompt, userPrompt, ct);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CaptureService] ClassifyAsync LLM error: {ex.Message}");
            throw;
        }

        return ParseClassification(raw, input);
    }

    /// <summary>AI 無効時の手動分類パス。</summary>
    public CaptureClassification BuildManualClassification(
        string input,
        string category,
        string projectName)
    {
        return new CaptureClassification
        {
            Category = category,
            ProjectName = projectName,
            Summary = input.Length <= 50 ? input : input[..50],
            Body = input,
            Confidence = 1.0,
            Reasoning = "manual"
        };
    }

    // ─────────────────────────────────────────────────────────
    // Routing
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// 分類結果に基づいてルーティングを実行する。
    /// task カテゴリは CreateAsanaTaskAsync を使うこと（承認フロー込み）。
    /// </summary>
    public async Task<CaptureRouteResult> RouteAsync(
        CaptureClassification classification,
        string originalInput,
        CancellationToken ct = default)
    {
        var projects = await _discovery.GetProjectInfoListAsync(ct: ct);
        var project = projects.FirstOrDefault(p =>
            string.Equals(p.Name, classification.ProjectName, StringComparison.OrdinalIgnoreCase));

        CaptureRouteResult result = classification.Category switch
        {
            "tension" => await AppendToOpenIssuesAsync(classification, project, ct),
            "memo" => await AppendToCaptureLogAsync(classification, originalInput, ct),
            "focus_update" => BuildFocusUpdateNavigation(classification, project),
            "decision" => BuildDecisionNavigation(classification, project),
            _ => new CaptureRouteResult { Success = false, Message = $"Unknown category: {classification.Category}" }
        };

        // memo は AppendToCaptureLogAsync で書き込み済みのためスキップ。
        // task は CreateAsanaTaskAsync 内で記録するためスキップ。
        // その他のカテゴリはここで capture_log.md に副次記録する。
        if (classification.Category is not ("memo" or "task"))
        {
            var proj = string.IsNullOrWhiteSpace(classification.ProjectName) ? "" : $" [{classification.ProjectName}]";
            await AppendToCaptureLogInternalAsync(
                $"[{classification.Category}]{proj} {classification.Summary}\n{originalInput}", ct);
        }

        return result;
    }

    // ─────────────────────────────────────────────────────────
    // Asana task 起票
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// Asana API で task を起票する。呼び出し前に必ず承認画面を経ること。
    /// </summary>
    public async Task<CaptureRouteResult> CreateAsanaTaskAsync(
        AsanaTaskCreatePreview preview,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        // 重複起票ガード
        if (_recentCreations.TryGetValue(idempotencyKey, out var existing) &&
            (DateTime.Now - existing.at).TotalSeconds < DuplicateWindowSec)
        {
            return new CaptureRouteResult
            {
                Success = true,
                Message = $"Already created (task {existing.gid})",
                AsanaTaskGid = existing.gid,
                AsanaTaskUrl = $"https://app.asana.com/0/{preview.ProjectGid}/{existing.gid}"
            };
        }

        var (token, _) = ResolveAsanaToken();
        if (string.IsNullOrWhiteSpace(token))
            return new CaptureRouteResult { Success = false, Message = "Asana token not configured." };

        // payload 組み立て (最終確定値から)
        var dataObj = new Dictionary<string, object>
        {
            ["name"] = preview.TaskName,
            ["notes"] = preview.Notes,
            ["projects"] = new[] { preview.ProjectGid }
        };

        // 担当者: 常に自分 (asana_global.json の user_gid)
        var globalConfig = _configService.LoadAsanaGlobalConfig();
        if (!string.IsNullOrWhiteSpace(globalConfig.UserGid))
            dataObj["assignee"] = globalConfig.UserGid;

        if (!string.IsNullOrWhiteSpace(preview.DueAt))
            dataObj["due_at"] = preview.DueAt;
        else if (!string.IsNullOrWhiteSpace(preview.DueOn))
            dataObj["due_on"] = preview.DueOn;

        if (!string.IsNullOrWhiteSpace(preview.SectionGid) && !string.IsNullOrWhiteSpace(preview.ProjectGid))
        {
            // section 整合性: ProjectGid と SectionGid の組み合わせ検証済みであること前提
            dataObj["memberships"] = new[]
            {
                new Dictionary<string, string>
                {
                    ["project"] = preview.ProjectGid,
                    ["section"] = preview.SectionGid
                }
            };
        }

        var payload = JsonSerializer.Serialize(new { data = dataObj });
        var req = new HttpRequestMessage(HttpMethod.Post, "https://app.asana.com/api/1.0/tasks");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try
        {
            resp = await Http.SendAsync(req, ct);
        }
        catch (Exception ex)
        {
            return new CaptureRouteResult { Success = false, Message = $"Network error: {ex.Message}" };
        }

        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            var status = (int)resp.StatusCode;
            var hint = status == 403 ? "Permission denied on this project."
                : status == 400 ? "Bad request - check project/section GIDs."
                : $"HTTP {status}";
            var errMsg = ExtractAsanaErrorMessage(body);
            return new CaptureRouteResult
            {
                Success = false,
                Message = $"{hint} {errMsg}".Trim()
            };
        }

        string gid;
        try
        {
            using var doc = JsonDocument.Parse(body);
            gid = doc.RootElement.GetProperty("data").GetProperty("gid").GetString() ?? "";
        }
        catch
        {
            gid = "";
        }

        // 起票済みキャッシュ
        if (!string.IsNullOrWhiteSpace(gid))
            _recentCreations[idempotencyKey] = (gid, DateTime.Now);

        LogCaptureEvent("task_created", new Dictionary<string, string>
        {
            ["project_gid"] = preview.ProjectGid,
            ["section_gid"] = preview.SectionGid,
            ["task_gid"] = gid,
            ["task_name"] = preview.TaskName,
        });

        // 3-3: 補助ログ (設定で有効時のみ)
        var settingsForLog = _configService.LoadSettings();
        if (settingsForLog.CaptureTaskLogEnabled)
            await AppendTaskToAsanaLogAsync(preview, gid, ct);

        // capture_log.md に常時記録 (category に関わらず全キャプチャを追跡できるようにする)
        await AppendToCaptureLogInternalAsync(
            $"[task] {preview.TaskName}\n→ {preview.ProjectName}"
            + (string.IsNullOrWhiteSpace(preview.SectionName) ? "" : $" / {preview.SectionName}")
            + (string.IsNullOrWhiteSpace(gid) ? "" : $" (gid:{gid})"),
            ct);

        return new CaptureRouteResult
        {
            Success = true,
            Message = $"Task created in {preview.ProjectName}",
            AsanaTaskGid = gid,
            AsanaTaskUrl = string.IsNullOrWhiteSpace(gid)
                ? null
                : $"https://app.asana.com/0/{preview.ProjectGid}/{gid}"
        };
    }

    /// <summary>
    /// task 起票成功後の補助ログ追記 (asana-tasks-view.md)。失敗は無視する。
    /// </summary>
    private async Task AppendTaskToAsanaLogAsync(AsanaTaskCreatePreview preview, string gid, CancellationToken ct)
    {
        var settings = _configService.LoadSettings();
        var obsidianRoot = settings.ObsidianVaultRoot.Trim();
        if (string.IsNullOrWhiteSpace(obsidianRoot)) return;

        var outputFile = Path.Combine(obsidianRoot, "asana-tasks-view.md");
        if (!File.Exists(outputFile)) return;

        try
        {
            var (content, enc) = await _encoding.ReadFileAsync(outputFile, ct);
            var idTag = string.IsNullOrWhiteSpace(gid) ? "" : $" [id:{gid}]";
            var dueLabel = !string.IsNullOrWhiteSpace(preview.DueAt) ? preview.DueAt[..16].Replace("T", " ")
                         : preview.DueOn;
            var dueTag = string.IsNullOrWhiteSpace(dueLabel) ? "" : $" (Due: {dueLabel})";
            var entry = $"\n- [ ] {preview.TaskName}{idTag}{dueTag}\n";
            await _encoding.WriteFileAsync(outputFile, content.TrimEnd() + entry, enc, ct);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CaptureService] AppendTaskToAsanaLogAsync failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Asana project メタデータを取得 (10分キャッシュ)。
    /// </summary>
    public async Task<AsanaProjectMeta?> FetchProjectMetaAsync(string gid, CancellationToken ct = default)
    {
        if (_projectMetaCache.TryGetValue(gid, out var cached) &&
            (DateTime.Now - cached.at).TotalMinutes < MetaCacheTtlMin)
            return cached.meta;

        var (token, _) = ResolveAsanaToken();
        if (string.IsNullOrWhiteSpace(token)) return null;

        try
        {
            var uri = $"https://app.asana.com/api/1.0/projects/{gid}?opt_fields=name";
            var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var raw = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(raw);
            var name = doc.RootElement.GetProperty("data").GetProperty("name").GetString() ?? "";
            var meta = new AsanaProjectMeta { Gid = gid, Name = name };
            _projectMetaCache[gid] = (meta, DateTime.Now);
            return meta;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Asana project の section 一覧を取得 (10分キャッシュ)。
    /// </summary>
    public async Task<List<AsanaSectionMeta>> FetchSectionsAsync(string projectGid, CancellationToken ct = default)
    {
        if (_sectionCache.TryGetValue(projectGid, out var cached) &&
            (DateTime.Now - cached.at).TotalMinutes < MetaCacheTtlMin)
            return cached.sections;

        var (token, _) = ResolveAsanaToken();
        if (string.IsNullOrWhiteSpace(token)) return [];

        try
        {
            var uri = $"https://app.asana.com/api/1.0/projects/{projectGid}/sections?opt_fields=name";
            var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return [];
            var raw = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(raw);
            var sections = new List<AsanaSectionMeta>();
            foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                sections.Add(new AsanaSectionMeta
                {
                    Gid = item.GetProperty("gid").GetString() ?? "",
                    Name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""
                });
            }
            _sectionCache[projectGid] = (sections, DateTime.Now);
            return sections;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Asana タスクにコメント (story) を追加する。
    /// エンドポイント: POST /tasks/{gid}/stories { data: { text } }
    /// </summary>
    public async Task<(bool Success, string Message)> AddTaskCommentAsync(
        string taskGid, string commentText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskGid))
            return (false, "Task GID is empty.");

        var (token, _) = ResolveAsanaToken();
        if (string.IsNullOrWhiteSpace(token))
            return (false, "Asana token not configured.");

        try
        {
            var uri     = $"https://app.asana.com/api/1.0/tasks/{taskGid}/stories";
            var payload = JsonSerializer.Serialize(new { data = new { text = commentText } });
            var req     = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await Http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
                return (true, $"Comment added to task {taskGid}.");

            var err     = await resp.Content.ReadAsStringAsync(ct);
            var snippet = err.Length > 200 ? err[..200] : err;
            return (false, $"Asana API error: {(int)resp.StatusCode} {snippet}");
        }
        catch (Exception ex)
        {
            return (false, $"Asana API error: {ex.Message}");
        }
    }

    /// <summary>キャッシュを手動クリア (Refresh ボタン用)。</summary>
    public void InvalidateMetaCache(string? projectGid = null)
    {
        if (projectGid == null)
        {
            _projectMetaCache.Clear();
            _sectionCache.Clear();
        }
        else
        {
            _projectMetaCache.Remove(projectGid);
            _sectionCache.Remove(projectGid);
        }
    }

    /// <summary>
    /// 選択中 project の asana_config.json から GID マッピングを読み込む。
    /// asana_global.json の personal_project_gids も含めて返す (重複除去済み)。
    /// </summary>
    public (List<string> projectGids, Dictionary<string, string> workstreamMap) LoadAsanaProjectGids(ProjectInfo project)
    {
        AsanaProjectConfig cfg = new();
        var path = ResolveAsanaConfigPath(project);
        if (File.Exists(path))
        {
            try
            {
                var (content, _) = _encoding.ReadFile(path);
                cfg = JsonSerializer.Deserialize<AsanaProjectConfig>(content, JsonOpts) ?? cfg;
            }
            catch { }
        }

        // asana_global.json の personal_project_gids を追加
        var personalGids = _configService.LoadAsanaGlobalConfig().PersonalProjectGids ?? [];
        var mergedGids = cfg.AsanaProjectGids
            .Concat(personalGids)
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct()
            .ToList();

        return (mergedGids, cfg.WorkstreamProjectMap);
    }

    /// <summary>
    /// asana_config.json の AnkenAliases を返す。プロジェクト名自体も含む。
    /// </summary>
    public List<string> LoadAnkenAliases(ProjectInfo project)
    {
        var aliases = new List<string> { project.Name };
        var path = ResolveAsanaConfigPath(project);
        if (!File.Exists(path)) return aliases;
        try
        {
            var (content, _) = _encoding.ReadFile(path);
            var cfg = JsonSerializer.Deserialize<AsanaProjectConfig>(content, JsonOpts);
            if (cfg?.AnkenAliases is { Count: > 0 } list)
                aliases.AddRange(list);
        }
        catch { }
        return aliases;
    }

    /// <summary>
    /// asana_global.json の personal_project_gids を返す。
    /// </summary>
    public List<string> GetPersonalProjectGids() =>
        (_configService.LoadAsanaGlobalConfig().PersonalProjectGids ?? [])
        .Where(g => !string.IsNullOrWhiteSpace(g))
        .ToList();

    /// <summary>
    /// idempotency キーを生成する (project + summary + body hash)。
    /// </summary>
    public static string BuildIdempotencyKey(string projectGid, string summary, string body)
    {
        var raw = $"{projectGid}|{summary}|{body}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..16];
    }

    // ─────────────────────────────────────────────────────────
    // ローカルタスク作成 (Asana 未設定時)
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// Asana 未設定時のローカルタスク作成。tasks.md の In Progress セクションに追記。
    /// </summary>
    public async Task<CaptureRouteResult> CreateLocalTaskAsync(
        CaptureClassification classification,
        string originalInput,
        CancellationToken ct = default)
    {
        var projects = await _discovery.GetProjectInfoListAsync(ct: ct);
        var project = projects.FirstOrDefault(p =>
            string.Equals(p.Name, classification.ProjectName, StringComparison.OrdinalIgnoreCase));

        if (project == null)
            return new CaptureRouteResult { Success = false, Message = "Project not found." };

        var taskLine = BuildTaskLine(classification);
        var tasksPath = ResolveTasksPath(project, classification);

        EnsureTaskFileExists(tasksPath);
        AppendToInProgressSection(tasksPath, taskLine);

        await AppendToCaptureLogInternalAsync(
            $"[task][local] [{classification.ProjectName}] {classification.Summary}\n{originalInput}", ct);

        return new CaptureRouteResult
        {
            Success = true,
            Message = $"Local task added to {Path.GetFileName(tasksPath)}",
            TargetFilePath = tasksPath
        };
    }

    private static string BuildTaskLine(CaptureClassification classification)
    {
        var line = $"- [ ] {classification.Summary}";
        if (!string.IsNullOrWhiteSpace(classification.DueOn))
            line += $" (Due: {classification.DueOn})";
        return line;
    }

    private string ResolveTasksPath(ProjectInfo project, CaptureClassification classification)
    {
        var obsidianNotes = Path.Combine(project.AiContextPath, "obsidian_notes");

        if (!string.IsNullOrWhiteSpace(classification.WorkstreamHint))
        {
            var wsPath = Path.Combine(obsidianNotes, "workstreams",
                classification.WorkstreamHint, "tasks.md");
            if (File.Exists(wsPath) || Directory.Exists(Path.GetDirectoryName(wsPath)!))
                return wsPath;
        }

        return Path.Combine(obsidianNotes, "tasks.md");
    }

    private void EnsureTaskFileExists(string tasksPath)
    {
        if (File.Exists(tasksPath)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(tasksPath)!);
        var template = "## In Progress\n\n## Completed\n";
        _encoding.WriteFile(tasksPath, template, "utf-8");
    }

    private void AppendToInProgressSection(string tasksPath, string taskLine)
    {
        var (content, encoding) = _encoding.ReadFile(tasksPath);
        var lines = content.Split('\n').ToList();

        int insertIdx = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (Regex.IsMatch(lines[i], @"^\s*#{2,3}\s*In\s+Progress\b"))
            {
                insertIdx = i + 1;
                while (insertIdx < lines.Count
                    && !Regex.IsMatch(lines[insertIdx], @"^\s*#{2,3}\s")
                    && !string.IsNullOrWhiteSpace(lines[insertIdx]))
                {
                    insertIdx++;
                }
                break;
            }
        }

        if (insertIdx < 0)
        {
            lines.Add("");
            lines.Add("## In Progress");
            lines.Add(taskLine);
        }
        else
        {
            lines.Insert(insertIdx, taskLine);
        }

        _encoding.WriteFile(tasksPath, string.Join('\n', lines), encoding);
    }

    // ─────────────────────────────────────────────────────────
    // Routing helpers
    // ─────────────────────────────────────────────────────────

    private async Task<CaptureRouteResult> AppendToOpenIssuesAsync(
        CaptureClassification c,
        ProjectInfo? project,
        CancellationToken ct)
    {
        if (project == null)
            return new CaptureRouteResult { Success = false, Message = "Project not found for open issue routing." };

        var openIssuesPath = Path.Combine(project.AiContextContentPath, "open_issues.md");

        try
        {
            string existingContent;
            string encoding;

            if (File.Exists(openIssuesPath))
            {
                (existingContent, encoding) = await _encoding.ReadFileAsync(openIssuesPath, ct);
            }
            else
            {
                var dir = Path.GetDirectoryName(openIssuesPath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);
                existingContent = "# Open Issues\n\n";
                encoding = "utf-8";
            }

            var newContent = BuildOpenIssueAppend(existingContent, c);
            await _encoding.WriteFileAsync(openIssuesPath, newContent, encoding, ct);

            return new CaptureRouteResult
            {
                Success = true,
                Message = $"Added to {project.Name}/open_issues.md",
                TargetFilePath = openIssuesPath
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CaptureService] AppendToOpenIssuesAsync failed: {ex.Message}");
            var memoResult = await AppendToCaptureLogAsync(c, $"[open_issue] {c.Summary}\n{c.Body}", ct);
            return new CaptureRouteResult
            {
                Success = false,
                Message = $"Failed to write open_issues.md ({ex.GetType().Name}). Saved as memo instead.",
                TargetFilePath = memoResult.TargetFilePath
            };
        }
    }

    // ─────────────────────────────────────────────────────────
    // Open Issues proposal (AI review flow)
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// AI を使って open_issues.md の更新提案を生成する。
    /// AI 有効時に CaptureWindow から呼び出し、差分確認ダイアログを経由して書き込む。
    /// </summary>
    public async Task<(FileUpdateProposal proposal, string encoding)> GenerateOpenIssuesProposalAsync(
        CaptureClassification c,
        ProjectInfo project,
        CancellationToken ct = default)
    {
        var openIssuesPath = Path.Combine(project.AiContextContentPath, "open_issues.md");

        string existingContent;
        string enc;
        if (File.Exists(openIssuesPath))
        {
            (existingContent, enc) = await _encoding.ReadFileAsync(openIssuesPath, ct);
        }
        else
        {
            var dir = Path.GetDirectoryName(openIssuesPath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            existingContent = "# Open Issues\n\n";
            enc = "utf-8";
        }

        var systemPrompt = BuildOpenIssuesSystemPrompt();
        var userPrompt   = BuildOpenIssuesUserPrompt(existingContent, c);
        var proposed     = await _llm.ChatCompletionAsync(systemPrompt, userPrompt, ct);

        var proposal = new FileUpdateProposal
        {
            CurrentContent    = existingContent,
            ProposedContent   = proposed.Trim(),
            Summary           = c.Summary,
            DebugSystemPrompt = _llm.LastSystemPrompt,
            DebugUserPrompt   = _llm.LastUserPrompt,
            DebugResponse     = _llm.LastResponse,
        };

        return (proposal, enc);
    }

    /// <summary>
    /// 差分確認後に open_issues.md へ書き込む。
    /// </summary>
    public Task WriteOpenIssuesAsync(string path, string content, string encoding, CancellationToken ct = default)
        => _encoding.WriteFileAsync(path, content, encoding, ct);

    /// <summary>
    /// Open Issues 提案のリファイン (FocusUpdateService.RefineAsync と同パターン)。
    /// </summary>
    public async Task<string> RefineTensionsAsync(
        string initialUserPrompt,
        string initialProposed,
        string instructions,
        IReadOnlyList<(string instruction, string result)> history,
        CancellationToken ct = default)
    {
        var messages = new List<(string role, string content)>
        {
            ("user",      initialUserPrompt),
            ("assistant", initialProposed)
        };
        foreach (var (instr, result) in history)
        {
            messages.Add(("user",      instr));
            messages.Add(("assistant", result));
        }
        messages.Add(("user", instructions));

        var refined = await _llm.ChatWithHistoryAsync(BuildOpenIssuesSystemPrompt(), messages, ct);
        return refined.Trim();
    }

    /// <summary>
    /// capture_log.md に任意のエントリを追記する (CaptureWindow から呼び出す)。
    /// </summary>
    public Task AppendCaptureLogEntryAsync(string body, CancellationToken ct = default)
        => AppendToCaptureLogInternalAsync(body, ct);

    private static string BuildOpenIssueAppend(string existingContent, CaptureClassification c)
    {
        var entry = string.IsNullOrWhiteSpace(c.Body)
            ? $"- {c.Summary}"
            : $"- {c.Summary}: {c.Body.Split('\n')[0].Trim()}";
        return existingContent.TrimEnd() + "\n" + entry + "\n";
    }

    private static string BuildOpenIssuesSystemPrompt() => """
        You are an assistant that maintains an open_issues.md file for a project management system.
        An "open issue" is an unresolved question, concern, trade-off, or risk — not yet a decision.

        ## Output rules
        - Output ONLY the full updated content of open_issues.md. No explanations, no preamble, no markdown fences.
        - Never truncate. Always output the complete file from the first line to the last.

        ## Update rules
        1. PRESERVE the existing Markdown heading/section structure exactly (do not add, remove, or rename sections).
        2. If the new item is essentially the same as an existing item, merge them naturally rather than duplicating.
        3. If the new item is closely related to an existing item, insert it near that item with appropriate context.
        4. If the new item is distinct, insert it at the most semantically appropriate location — not necessarily at the end.
        5. Rephrase the new item to match the document's existing writing style and tone.
        6. Do not fabricate or remove existing items. Only add the new item.
        """;

    private static string BuildOpenIssuesUserPrompt(string existingContent, CaptureClassification c)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Current open_issues.md");
        sb.AppendLine(existingContent);
        sb.AppendLine();
        sb.AppendLine("## New item to integrate");
        sb.AppendLine($"Summary: {c.Summary}");
        if (!string.IsNullOrWhiteSpace(c.Body))
            sb.AppendLine($"Detail: {c.Body}");
        sb.AppendLine();
        sb.AppendLine("## Instruction");
        sb.AppendLine("Integrate the new item into open_issues.md following the update rules.");
        sb.AppendLine("Output the full file content only. Do not include any explanation.");
        return sb.ToString();
    }

    private async Task<CaptureRouteResult> AppendToCaptureLogAsync(
        CaptureClassification c,
        string originalInput,
        CancellationToken ct)
    {
        var logPath = Path.Combine(_configService.ConfigDir, "capture_log.md");
        var ok = await AppendToCaptureLogInternalAsync(originalInput, ct);
        return ok
            ? new CaptureRouteResult { Success = true, Message = "Added to capture_log.md", TargetFilePath = logPath }
            : new CaptureRouteResult { Success = false, Message = "Failed to write capture_log.md" };
    }

    /// <summary>
    /// capture_log.md にエントリを追記する共通実装。失敗時は false を返す。
    /// </summary>
    private async Task<bool> AppendToCaptureLogInternalAsync(string body, CancellationToken ct)
    {
        var logPath = Path.Combine(_configService.ConfigDir, "capture_log.md");
        try
        {
            string existingContent = "";
            string encoding = "utf-8";

            if (File.Exists(logPath))
                (existingContent, encoding) = await _encoding.ReadFileAsync(logPath, ct);

            var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            var entry = $"\n## {ts}\n{body}\n";
            var newContent = existingContent.TrimEnd() + entry;
            await _encoding.WriteFileAsync(logPath, newContent, encoding, ct);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CaptureService] AppendToCaptureLogInternalAsync failed: {ex.Message}");
            return false;
        }
    }

    private CaptureRouteResult BuildFocusUpdateNavigation(CaptureClassification c, ProjectInfo? project)
    {
        if (project == null)
            return new CaptureRouteResult { Success = false, Message = "Project not found for focus_update routing." };

        var focusPath = Path.Combine(project.AiContextContentPath, "current_focus.md");
        EnsureFocusBackup(focusPath);

        return new CaptureRouteResult
        {
            Success = true,
            RequiresNavigation = true,
            NavigationProjectName = project.Name,
            NavigationFilePath = focusPath,
            Message = $"Open {project.Name}/current_focus.md and start Update Focus"
        };
    }

    private CaptureRouteResult BuildDecisionNavigation(CaptureClassification c, ProjectInfo? project)
    {
        if (project == null)
            return new CaptureRouteResult { Success = false, Message = "Project not found for decision routing." };

        return new CaptureRouteResult
        {
            Success = true,
            RequiresNavigation = true,
            NavigationProjectName = project.Name,
            NavigationFilePath = null,   // EditorViewModel の Decision Log フローに委譲
            Message = $"Navigate to {project.Name} for decision log"
        };
    }

    // ─────────────────────────────────────────────────────────
    // Prompt building
    // ─────────────────────────────────────────────────────────

    private static string BuildSystemPrompt(List<ProjectInfo> projects)
    {
        return """
You are a classifier for a multi-project manager's quick capture system.
Your job is to analyze free-form input and classify it into the most appropriate category,
identify which project it belongs to, and generate a concise summary.

## Categories
- "task": An actionable to-do item. Something that needs to be done.
- "tension": An unresolved question, concern, trade-off, or risk. Not yet a decision. Will be saved to open_issues.md.
- "focus_update": A shift in priorities or focus. The user wants to record a change in what they're working on.
- "decision": A concluded choice. The user has decided something and wants to record it.
- "memo": General note, idea, or thought that doesn't fit other categories.

## Output rules
- Return a single JSON object. No explanation, no markdown fences.
- Fields:
  {
    "category": "task" | "tension" | "focus_update" | "decision" | "memo",
    "project": "exact project name from the list, or empty string if unclear",
    "summary": "concise one-line summary (max 80 chars)",
    "body": "detail text for routing target (task uses this as Asana notes)",
    "workstream_hint": "candidate workstream id or empty string",
    "project_candidate_gid": "candidate Asana project gid or empty string",
    "section_candidate_gid": "candidate Asana section gid or empty string",
    "due_on": "YYYY-MM-DD or empty string",
    "confidence": 0.0 to 1.0,
    "reasoning": "brief explanation of classification"
  }

## Project matching rules
- Match based on keywords, project names, technology mentions, or domain context
- If the input explicitly mentions a project name, use that
- If ambiguous between projects, set confidence < 0.5 and leave project empty
- Project names are case-insensitive for matching
- If task destination is ambiguous across multiple Asana projects, prefer leaving candidate gid empty
- section candidate is optional; return empty if not confident

## Body formatting rules
- For "task": Write only the substantive task description for Asana notes. Strip all meta-instructions from the body: remove phrases like "Asanaに追加して", "タスク追加して", "create a task", "add to Asana", due date mentions ("期日: ...", "due: ..."), and project name prefixes. Write what actually needs to be DONE, not how the user asked to capture it. If the task is simple, a short one-line description is fine; leave body empty if the summary alone is sufficient.
- For "tension": "- {question, trade-off, concern, or risk, naturally phrased}"
- For "focus_update": the full input text, lightly edited for clarity
- For "decision": the full input text, structured as "Decision: X. Reason: Y"
- For "memo": the full input text as-is
""";
    }

    private static string BuildUserPrompt(string input, string? projectHint, List<ProjectInfo> projects)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Available Projects");
        foreach (var p in projects)
        {
            var focusSnippet = p.FocusFile != null && File.Exists(p.FocusFile)
                ? ReadFirstChars(p.FocusFile, 100)
                : "no focus file";
            var tasksPath = Path.Combine(p.AiContextPath, "obsidian_notes", "tasks.md");
            var tasksSnippet = File.Exists(tasksPath)
                ? ReadFirstChars(tasksPath, 150)
                : "";
            var tasksInfo = string.IsNullOrWhiteSpace(tasksSnippet) ? "" : $" / Tasks: {tasksSnippet}";
            sb.AppendLine($"- {p.Name} (Tier: {p.Tier}) - Focus: {focusSnippet}{tasksInfo}");
        }
        sb.AppendLine();
        sb.AppendLine("## User Input");
        sb.AppendLine(input);
        sb.AppendLine();
        sb.AppendLine("## Context");
        sb.AppendLine($"- Date: {DateTime.Now:yyyy-MM-dd}");
        sb.AppendLine($"- User-selected project: {projectHint ?? "auto-detect"}");
        sb.AppendLine();
        sb.AppendLine("Classify the input above.");
        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────

    private static CaptureClassification ParseClassification(string raw, string fallbackInput)
    {
        try
        {
            // JSON フェンスがあれば除去
            var json = raw.Trim();
            if (json.StartsWith("```")) json = System.Text.RegularExpressions.Regex.Replace(json, @"```[a-z]*", "").Trim('`').Trim();

            var result = JsonSerializer.Deserialize<CaptureClassification>(json, JsonOpts);
            if (result != null) return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CaptureService] ParseClassification error: {ex.Message}");
        }

        // フォールバック
        return new CaptureClassification
        {
            Category = "memo",
            Summary = fallbackInput.Length <= 80 ? fallbackInput : fallbackInput[..80],
            Body = fallbackInput,
            Confidence = 0.0,
            Reasoning = "parse_failure"
        };
    }

    private (string token, string source) ResolveAsanaToken()
    {
        var envToken = Environment.GetEnvironmentVariable("ASANA_TOKEN");
        if (!string.IsNullOrWhiteSpace(envToken)) return (envToken, "env");

        var global = _configService.LoadAsanaGlobalConfig();
        if (!string.IsNullOrWhiteSpace(global.AsanaToken)) return (global.AsanaToken, "config");

        return ("", "none");
    }

    private string ResolveAsanaConfigPath(ProjectInfo project)
    {
        var settings = _configService.LoadSettings();
        var syncRoot = settings.CloudSyncRoot.TrimEnd('\\', '/');

        return project.Category == "domain"
            ? project.Tier == "mini"
                ? Path.Combine(syncRoot, "_domains", "_mini", project.Name, "asana_config.json")
                : Path.Combine(syncRoot, "_domains", project.Name, "asana_config.json")
            : project.Tier == "mini"
                ? Path.Combine(syncRoot, "_mini", project.Name, "asana_config.json")
                : Path.Combine(syncRoot, project.Name, "asana_config.json");
    }

    private static void EnsureFocusBackup(string focusPath)
    {
        if (!File.Exists(focusPath)) return;
        var dir = Path.GetDirectoryName(focusPath) ?? "";
        var backupDir = Path.Combine(dir, "focus_history");
        Directory.CreateDirectory(backupDir);
        var backupPath = Path.Combine(backupDir, $"{DateTime.Now:yyyy-MM-dd}.md");
        if (!File.Exists(backupPath))
            File.Copy(focusPath, backupPath);
    }

    private static string ReadFirstChars(string path, int count)
    {
        try
        {
            var (content, _) = EncodingDetector.ReadFile(path);
            return content.Length <= count ? content.Replace('\n', ' ') : content[..count].Replace('\n', ' ');
        }
        catch { return ""; }
    }

    private static string ExtractAsanaErrorMessage(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("errors", out var errors) &&
                errors.GetArrayLength() > 0)
            {
                var msg = errors[0].TryGetProperty("message", out var m) ? m.GetString() : null;
                return msg ?? "";
            }
        }
        catch { }
        return "";
    }

    private static void LogCaptureEvent(string eventType, Dictionary<string, string> data)
    {
        var sb = new StringBuilder();
        sb.Append($"[CaptureService] {eventType}");
        foreach (var kv in data) sb.Append($" | {kv.Key}={kv.Value}");
        Debug.WriteLine(sb.ToString());
    }

    // ─────────────────────────────────────────────────────────
    // Inner types
    // ─────────────────────────────────────────────────────────

    private sealed class AsanaProjectConfig
    {
        [JsonPropertyName("asana_project_gids")]
        public List<string> AsanaProjectGids { get; set; } = [];

        [JsonPropertyName("workstream_project_map")]
        public Dictionary<string, string> WorkstreamProjectMap { get; set; } = [];

        [JsonPropertyName("anken_aliases")]
        public List<string> AnkenAliases { get; set; } = [];
    }
}
