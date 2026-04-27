using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Curia.Models;
using Windows.Data.Pdf;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Curia.Services;

/// <summary>
/// ソースファイルを Wiki に取り込む LLM パイプライン。
/// </summary>
public class WikiIngestService
{
    private readonly LlmClientService _llm;
    private readonly WikiService _wiki;
    private readonly ConfigService _configService;
    private const int MaxUpdateCandidates = 8;
    private const int MaxPdfOcrPages = 20;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public WikiIngestService(LlmClientService llm, WikiService wiki, ConfigService configService)
    {
        _llm = llm;
        _wiki = wiki;
        _configService = configService;
    }

    public async Task<IngestResult> IngestSource(
        string wikiRoot,
        string sourceFilePath,
        string? supplementaryPrompt = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var proposal = await GenerateIngestProposal(wikiRoot, sourceFilePath, supplementaryPrompt, progress, cancellationToken);
        if (!proposal.Success)
            return proposal;

        await ApplyIngestResult(wikiRoot, proposal, progress, cancellationToken);
        return proposal;
    }

    public async Task<IngestResult> GenerateIngestProposal(
        string wikiRoot,
        string sourceFilePath,
        string? supplementaryPrompt = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // 1. raw/ にコピー
            progress?.Report("Copying source to raw/...");
            _ = await _wiki.AddSource(wikiRoot, sourceFilePath);

            // 2. ソース内容を読み取り
            progress?.Report("Reading source content...");
            var sourceContent = await ReadSourceContent(sourceFilePath, cancellationToken);
            if (string.IsNullOrWhiteSpace(sourceContent))
                return Fail("Source file is empty or could not be read.");

            // 3. プロンプト構築
            progress?.Report("Building LLM prompt...");
            var schemaPath = WikiService.GetSchemaPath(wikiRoot);
            var schema = File.Exists(schemaPath) ? await File.ReadAllTextAsync(schemaPath, Encoding.UTF8, cancellationToken) : "";
            var index = _wiki.GetIndex(wikiRoot);
            var pages = _wiki.GetAllPages(wikiRoot);

            var promptConfig = _wiki.LoadPrompts(wikiRoot);
            var overrides = promptConfig.IsUnknownVersion ? null : promptConfig.Import;
            var effectiveOverrides = MergeSupplementaryPrompt(overrides, supplementaryPrompt);
            var categoriesConfig = _wiki.LoadCategories(wikiRoot);

            var systemPrompt = BuildSystemPrompt(schema, categoriesConfig.Categories, effectiveOverrides);
            progress?.Report("Selecting update candidates...");
            var updateCandidates = await SelectUpdateCandidates(
                wikiRoot,
                index,
                sourceContent,
                pages,
                cancellationToken);

            progress?.Report("Loading candidate pages...");
            var existingPagesContent = BuildExistingPagesContent(wikiRoot, updateCandidates);
            var existingTags = CollectExistingTags(wikiRoot, pages);

            var userPrompt = BuildUserPrompt(
                index,
                sourceContent,
                Path.GetFileName(sourceFilePath),
                existingPagesContent,
                existingTags,
                effectiveOverrides);

            // 4. LLM 呼び出し
            progress?.Report("Calling LLM...");
            var raw = await _llm.ChatCompletionAsync(systemPrompt, userPrompt, cancellationToken);

            // 5. JSON パース
            progress?.Report("Parsing LLM response...");
            var llmResp = ParseLlmResponse(raw);
            if (llmResp == null)
                return Fail("Failed to parse LLM response as JSON.");
            CanonicalizeResultTags(llmResp, existingTags);
            RescueSpecialPaths(llmResp);

            progress?.Report("Proposal generated. Awaiting review.");
            AppendLog(wikiRoot, $"[GenerateProposal] OK — newPages={llmResp.NewPages.Count}, updatedPages={llmResp.UpdatedPages.Count}, summary={llmResp.Summary}");

            return new IngestResult
            {
                Success = true,
                Summary = llmResp.Summary,
                NewPages = llmResp.NewPages,
                UpdatedPages = llmResp.UpdatedPages,
                IndexUpdate = llmResp.IndexUpdate,
                LogEntry = llmResp.LogEntry,
                DebugSystemPrompt = _llm.LastSystemPrompt,
                DebugUserPrompt = _llm.LastUserPrompt,
                DebugResponse = _llm.LastResponse
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AppendLog(wikiRoot, "[GenerateProposal] Timed out while waiting for LLM response.");
            return Fail("LLM request timed out while generating ingest proposal.");
        }
        catch (OperationCanceledException)
        {
            AppendLog(wikiRoot, "[GenerateProposal] Cancelled.");
            return Fail("Ingest cancelled.");
        }
        catch (Exception ex)
        {
            AppendLog(wikiRoot, $"[GenerateProposal] Exception: {ex}");
            return Fail($"Ingest error: {ex.Message}");
        }
    }

    public async Task ApplyIngestResult(
        string wikiRoot,
        IngestResult result,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // ---- パスバリデーション（全件事前検証: AC-19） ----
        var categoriesConfig = _wiki.LoadCategories(wikiRoot);
        var validCategories = categoriesConfig.Categories;

        var validationErrors = new List<string>();

        // newPages/updatedPages のパス検証
        var pagesToValidate = result.NewPages
            .Where(p => !string.IsNullOrWhiteSpace(p.Path) && !string.IsNullOrWhiteSpace(p.Content))
            .Select(p => (path: p.Path, isNew: true))
            .Concat(result.UpdatedPages
                .Where(u => !string.IsNullOrWhiteSpace(u.Path) && !string.IsNullOrWhiteSpace(u.Diff))
                .Select(u => (path: u.Path, isNew: false)))
            .ToList();

        foreach (var (path, _) in pagesToValidate)
        {
            var vr = _wiki.ValidatePagePath(wikiRoot, path, validCategories);
            if (!vr.IsValid)
                validationErrors.Add($"Invalid path '{path}': {vr.ErrorReason}");
        }

        if (validationErrors.Count > 0)
        {
            result.Success = false;
            result.ErrorMessage = string.Join("\n", validationErrors);
            AppendLog(wikiRoot, $"[ApplyIngest] Validation failed:\n{result.ErrorMessage}");
            return;
        }

        // targetPath 重複チェック（正規化後 OrdinalIgnoreCase）
        var allTargetPaths = new List<string>();
        foreach (var (path, _) in pagesToValidate)
        {
            var normalized = WikiService.NormalizeRelativePath(path);
            // 拡張子を .md に正規化
            var stem = Path.GetFileNameWithoutExtension(normalized);
            var cat  = normalized.Split('/')[1];
            allTargetPaths.Add(Path.GetFullPath(Path.Combine(wikiRoot, $"pages/{cat}/{stem}.md")));
        }
        if (!string.IsNullOrWhiteSpace(result.IndexUpdate))
            allTargetPaths.Add(Path.GetFullPath(WikiService.GetIndexPath(wikiRoot)));
        if (!string.IsNullOrWhiteSpace(result.LogEntry))
            allTargetPaths.Add(Path.GetFullPath(WikiService.GetLogPath(wikiRoot)));

        var dups = allTargetPaths.GroupBy(p => p, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList();
        if (dups.Count > 0)
        {
            result.Success = false;
            result.ErrorMessage = $"Duplicate target paths detected: {string.Join(", ", dups.Select(g => g.Key))}";
            AppendLog(wikiRoot, $"[ApplyIngest] Duplicate paths: {result.ErrorMessage}");
            return;
        }

        // ---- ドメインロック取得（AC-34, AC-43） ----
        progress?.Report("Acquiring domain lock...");
        IDisposable? domainLock = null;
        try
        {
            domainLock = await WikiService.AcquireDomainLockAsync(wikiRoot, cancellationToken);
        }
        catch (WikiDomainLockException ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            AppendLog(wikiRoot, $"[ApplyIngest] Domain lock failed: {ex.Message}");
            return;
        }

        try
        {
            // ---- トランザクション開始 ----
            progress?.Report("Preparing transaction...");
            var targets = pagesToValidate.Select(x =>
            {
                var norm = WikiService.NormalizeRelativePath(x.path);
                var stem = Path.GetFileNameWithoutExtension(norm);
                var cat  = norm.Split('/')[1];
                return ($"pages/{cat}/{stem}.md", x.isNew);
            }).ToList();

            if (!string.IsNullOrWhiteSpace(result.IndexUpdate))
                targets.Add(("index.md", !File.Exists(WikiService.GetIndexPath(wikiRoot))));
            if (!string.IsNullOrWhiteSpace(result.LogEntry))
                targets.Add(("log.md", !File.Exists(WikiService.GetLogPath(wikiRoot))));

            var txn = await _wiki.BeginTransactionAsync(wikiRoot, targets);

            // コンテンツマップ（normalizedRelPath → content）
            var contentMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in result.NewPages.Where(x => !string.IsNullOrWhiteSpace(x.Path) && !string.IsNullOrWhiteSpace(x.Content)))
            {
                var norm = WikiService.NormalizeRelativePath(p.Path);
                var stem = Path.GetFileNameWithoutExtension(norm);
                var cat  = norm.Split('/')[1];
                contentMap[$"pages/{cat}/{stem}.md"] = p.Content;
            }
            foreach (var u in result.UpdatedPages.Where(x => !string.IsNullOrWhiteSpace(x.Path) && !string.IsNullOrWhiteSpace(x.Diff)))
            {
                var norm = WikiService.NormalizeRelativePath(u.Path);
                var stem = Path.GetFileNameWithoutExtension(norm);
                var cat  = norm.Split('/')[1];
                contentMap[$"pages/{cat}/{stem}.md"] = u.Diff;
            }
            if (!string.IsNullOrWhiteSpace(result.IndexUpdate))
                contentMap["index.md"] = result.IndexUpdate;
            if (!string.IsNullOrWhiteSpace(result.LogEntry))
            {
                // log.md は追記なので既存内容と結合
                var existing = File.Exists(WikiService.GetLogPath(wikiRoot))
                    ? await File.ReadAllTextAsync(WikiService.GetLogPath(wikiRoot), Encoding.UTF8)
                    : "# Wiki Log\n";
                contentMap["log.md"] = existing + "\n" + result.LogEntry;
            }

            // ---- コミット実行（AC-14, AC-38） ----
            progress?.Report("Writing approved pages...");
            AppendLog(wikiRoot, $"[ApplyIngest] wikiRoot={wikiRoot}");
            AppendLog(wikiRoot, $"[ApplyIngest] targets={string.Join(", ", targets.Select(t => $"{t.Item1}(isNew={t.Item2})"))}");
            AppendLog(wikiRoot, $"[ApplyIngest] contentMap keys={string.Join(", ", contentMap.Keys)}");
            try
            {
                // カテゴリディレクトリを事前作成
                foreach (var (relPath, _) in targets)
                {
                    if (relPath is "index.md" or "log.md") continue;
                    var catDir = Path.Combine(WikiService.GetPagesDir(wikiRoot), relPath.Split('/')[1]);
                    Directory.CreateDirectory(catDir);
                }

                await _wiki.CommitTransactionAsync(wikiRoot, txn, entry =>
                {
                    var relPath = Path.GetRelativePath(wikiRoot, entry.TargetPath).Replace('\\', '/');
                    AppendLog(wikiRoot, $"[Commit] Writing entry: targetPath={entry.TargetPath}, relPath={relPath}, contentFound={contentMap.ContainsKey(relPath)}");
                    if (contentMap.TryGetValue(relPath, out var c)) return Task.FromResult(c);
                    return Task.FromResult("");
                });
            }
            catch (Exception ex)
            {
                progress?.Report("Error occurred. Rolling back...");
                try { await _wiki.RollbackTransactionAsync(wikiRoot, txn); }
                catch { /* best effort */ }
                result.Success = false;
                result.ErrorMessage = $"Write failed: {ex.Message}. Changes have been rolled back.";
                AppendLog(wikiRoot, $"[ApplyIngest] Commit exception (rolled back): {ex}");
                return;
            }

            // ---- メタ更新 ----
            try
            {
                await _wiki.UpdateMeta(wikiRoot, m =>
                {
                    m.Stats.LastIngest = DateTime.UtcNow;
                    m.Stats.TotalSources = _wiki.GetAllSources(wikiRoot).Count;
                    m.Stats.TotalPages = _wiki.GetAllPages(wikiRoot).Count(p => !p.IsRoot);
                });
            }
            catch { /* メタ更新失敗はエラーとしない */ }

            AppendLog(wikiRoot, "[ApplyIngest] Committed successfully.");
            progress?.Report("Saved.");
        }
        finally
        {
            domainLock?.Dispose();
        }
    }

    // ---- プロンプト構築 ----

    private string BuildSystemPrompt(string schema, IReadOnlyList<string>? categories = null, WikiPromptOverrides? overrides = null)
    {
        var language = _configService.LoadSettings().LlmLanguage;
        var categoryLine = categories is { Count: > 0 }
            ? $"- Allowed page categories (use ONLY these): {string.Join(", ", categories)}"
            : "- Page paths must be: pages/<category>/filename.md";
        var basePrompt = $$"""
You are the Wiki maintainer for Curia.
Follow the wiki-schema.md below exactly.

{{schema}}

IMPORTANT:
- Always respond with valid JSON matching the schema below.
- For updatedPages, return the FULL updated content in the "diff" field (not a patch).
- Keep pages concise and well-structured in Markdown.
- Use [[PageName]] wikilink format for cross-references.
- Write all page content in {{language}}.
- {{categoryLine}}
- Include YAML frontmatter at the top of each page:
  ---
  title: "..."
  created: "YYYY-MM-DD"
  updated: "YYYY-MM-DD"
  sources: ["filename"]
  tags: [tag1, tag2]
  ---

Response JSON schema:
{
  "summary": "string — brief description of what was done",
  "newPages": [{ "path": "pages/category/filename.md", "content": "full markdown" }],
  "updatedPages": [{ "path": "pages/category/filename.md", "diff": "full updated markdown" }],
  "indexUpdate": "full updated index.md content",
  "logEntry": "markdown log entry to append"
}
""";
        return ApplySystemOverrides(basePrompt, overrides);
    }

    /// <summary>UI 表示用デフォルトシステムプロンプトのプレビュー（スキーマ部分はプレースホルダ）。</summary>
    public static string GetDefaultSystemPromptPreview(string language) =>
        $"""
You are the Wiki maintainer for Curia.
Follow the wiki-schema.md below exactly.

[wiki-schema.md is loaded from disk and injected here at runtime]

IMPORTANT:
- Always respond with valid JSON matching the schema below.
- For updatedPages, return the FULL updated content in the "diff" field (not a patch).
- Keep pages concise and well-structured in Markdown.
- Use [[PageName]] wikilink format for cross-references.
- Write all page content in {language}.
- Include YAML frontmatter at the top of each page:
  ---
  title: "..."
  created: "YYYY-MM-DD"
  updated: "YYYY-MM-DD"
  sources: ["filename"]
  tags: [tag1, tag2]
  ---

Response JSON schema:
""" + """
{
  "summary": "string — brief description of what was done",
  "newPages": [{ "path": "pages/category/filename.md", "content": "full markdown" }],
  "updatedPages": [{ "path": "pages/category/filename.md", "diff": "full updated markdown" }],
  "indexUpdate": "full updated index.md content",
  "logEntry": "markdown log entry to append"
}
""";

    private static string ApplySystemOverrides(string basePrompt, WikiPromptOverrides? overrides)
    {
        if (overrides == null) return basePrompt;
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(overrides.SystemPrefix)) parts.Add(overrides.SystemPrefix);
        parts.Add(basePrompt);
        if (!string.IsNullOrEmpty(overrides.SystemSuffix)) parts.Add(overrides.SystemSuffix);
        return string.Join("\n\n", parts);
    }

    private static WikiPromptOverrides? MergeSupplementaryPrompt(WikiPromptOverrides? overrides, string? extra)
    {
        if (string.IsNullOrWhiteSpace(extra)) return overrides;
        return overrides != null
            ? new WikiPromptOverrides
              {
                  SystemPrefix = overrides.SystemPrefix,
                  SystemSuffix = overrides.SystemSuffix,
                  UserSuffix   = string.IsNullOrEmpty(overrides.UserSuffix)
                                 ? extra
                                 : overrides.UserSuffix + "\n\n" + extra
              }
            : new WikiPromptOverrides { UserSuffix = extra };
    }

    private static string BuildUserPrompt(
        string index,
        string sourceContent,
        string fileName,
        string existingPagesContent,
        IReadOnlyList<string> existingTags,
        WikiPromptOverrides? overrides = null)
    {
        var basePrompt = $"""
Please ingest the following source into the Wiki.

## Current index.md
{index}

## Source File: {fileName}
{sourceContent}

## Existing pages available for update (full content)
{existingPagesContent}

## Existing tag vocabulary (reuse these when possible)
{BuildTagVocabulary(existingTags)}

## Instructions
1. Create a summary page in sources/ for this source
2. If updating an existing page, use the "Existing pages available for update" section as the source of truth for current content before editing
3. Create new entity/concept pages if needed
4. Generate the full updated index.md
5. Generate a log.md entry for this ingest
6. Prefer reusing existing tags and avoid near-duplicate variants (case/plural/separator differences)

Respond with JSON only (no markdown code fences).
""";
        if (overrides != null && !string.IsNullOrEmpty(overrides.UserSuffix))
            return basePrompt + "\n\n" + overrides.UserSuffix;
        return basePrompt;
    }

    private async Task<IReadOnlyList<string>> SelectUpdateCandidates(
        string wikiRoot,
        string index,
        string sourceContent,
        IReadOnlyList<WikiPageItem> pages,
        CancellationToken cancellationToken)
    {
        var existingPages = pages.Where(p => !p.IsRoot).ToList();
        if (existingPages.Count == 0)
            return [];

        var pageList = string.Join('\n', existingPages.Select(p => p.RelativePath));
        var systemPrompt = """
You are a wiki update planner.
From the given wiki index and source document, select existing page paths that are likely to require updates.
Respond with JSON only:
{
  "updateCandidates": ["pages/...md"]
}
Rules:
- Return existing page paths only.
- Return at most 8 paths.
- Do not include index.md or log.md.
""";

        var userPrompt = $"""
## Existing wiki page paths
{pageList}

## Current index.md
{index}

## New source content
{sourceContent}

Select up to {MaxUpdateCandidates} existing pages that most likely need updates.
""";

        var raw = await _llm.ChatCompletionAsync(systemPrompt, userPrompt, cancellationToken);
        var parsed = ParseUpdateSelectionResponse(raw);
        if (parsed.Count == 0)
            return [];

        var existingSet = new HashSet<string>(existingPages.Select(p => p.RelativePath), StringComparer.OrdinalIgnoreCase);
        return parsed
            .Select(NormalizeRelativePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => existingSet.Contains(path!))
            .Take(MaxUpdateCandidates)
            .Cast<string>()
            .ToList();
    }

    private static string BuildExistingPagesContent(string wikiRoot, IReadOnlyList<string> candidatePaths)
    {
        if (candidatePaths.Count == 0)
            return "(none)";

        var sb = new StringBuilder();
        foreach (var path in candidatePaths)
        {
            var fullPath = Path.Combine(wikiRoot, path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath)) continue;
            var content = File.ReadAllText(fullPath, Encoding.UTF8);
            sb.AppendLine($"### [{path}]");
            sb.AppendLine(content);
            sb.AppendLine();
        }

        return sb.Length == 0 ? "(none)" : sb.ToString();
    }

    private static List<string> ParseUpdateSelectionResponse(string raw)
    {
        var json = raw.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            var start = json.IndexOf('\n') + 1;
            var end = json.LastIndexOf("```", StringComparison.Ordinal);
            if (start > 0 && end > start) json = json[start..end].Trim();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<UpdateSelectionResponse>(json, JsonOpts);
            return parsed?.UpdateCandidates?
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string? NormalizeRelativePath(string path)
    {
        var normalized = path.Trim().Trim('`', '"', '\'').Replace('\\', '/');
        if (!normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!normalized.StartsWith("pages/", StringComparison.OrdinalIgnoreCase))
            return null;
        if (normalized.Contains("..", StringComparison.Ordinal))
            return null;
        return normalized;
    }

    // ---- ソース読み取り ----

    private static async Task<string> ReadSourceContent(string filePath, CancellationToken cancellationToken)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".md" or ".txt" => await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken),
            ".pdf"          => await ReadPdfWithOcr(filePath, cancellationToken),
            ".docx"         => $"[DOCX file: {Path.GetFileName(filePath)} — text extraction not yet supported. Please convert to .md or .txt first.]",
            _               => await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken)
        };
    }

    private static async Task<string> ReadPdfWithOcr(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(filePath);
            var pdf = await PdfDocument.LoadFromFileAsync(storageFile);
            if (pdf.PageCount == 0)
                return $"[PDF file: {Path.GetFileName(filePath)} — no pages found.]";

            var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                         ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
            if (engine == null)
                return $"[PDF file: {Path.GetFileName(filePath)} — OCR engine is unavailable on this Windows environment. Please convert to .md or .txt first.]";

            var pageLimit = (uint)Math.Min((int)pdf.PageCount, MaxPdfOcrPages);
            var sb = new StringBuilder();
            sb.AppendLine($"# OCR Extracted Text ({Path.GetFileName(filePath)})");
            sb.AppendLine();

            for (uint i = 0; i < pageLimit; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var page = pdf.GetPage(i);
                using var renderStream = new InMemoryRandomAccessStream();
                var renderOptions = BuildRenderOptions(page.Size);
                await page.RenderToStreamAsync(renderStream, renderOptions);
                renderStream.Seek(0);

                var decoder = await BitmapDecoder.CreateAsync(renderStream);
                using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                var result = await engine.RecognizeAsync(bitmap);
                var text = (result?.Text ?? string.Empty).Trim();

                sb.AppendLine($"## Page {i + 1}");
                if (string.IsNullOrWhiteSpace(text))
                    sb.AppendLine("(no text recognized)");
                else
                    sb.AppendLine(text);
                sb.AppendLine();
            }

            if (pdf.PageCount > pageLimit)
            {
                sb.AppendLine($"[Truncated: OCR processed first {pageLimit} pages out of {pdf.PageCount}.]");
            }

            var extracted = sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(extracted))
                return $"[PDF file: {Path.GetFileName(filePath)} — OCR completed but no text was recognized.]";

            return extracted;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"[PDF file: {Path.GetFileName(filePath)} — OCR failed: {ex.Message}. Please convert to .md or .txt first.]";
        }
    }

    private static PdfPageRenderOptions BuildRenderOptions(Windows.Foundation.Size pageSize)
    {
        const double scale = 2.0;
        var width = (uint)Math.Max(1, Math.Round(pageSize.Width * scale));
        var height = (uint)Math.Max(1, Math.Round(pageSize.Height * scale));
        return new PdfPageRenderOptions
        {
            DestinationWidth = width,
            DestinationHeight = height
        };
    }

    // ---- JSON パース ----

    /// <summary>
    /// LLMが index.md / log.md を newPages / updatedPages に誤って含めた場合に
    /// 専用フィールド (IndexUpdate / LogEntry) へ移し替える。
    /// </summary>
    private static void RescueSpecialPaths(IngestLlmResponse resp)
    {
        // index.md を newPages から救済
        var indexNew = resp.NewPages.FirstOrDefault(p =>
            p.Path.Equals("index.md", StringComparison.OrdinalIgnoreCase));
        if (indexNew != null)
        {
            if (string.IsNullOrWhiteSpace(resp.IndexUpdate))
                resp.IndexUpdate = indexNew.Content;
            resp.NewPages.Remove(indexNew);
        }

        // index.md を updatedPages から救済
        var indexUpd = resp.UpdatedPages.FirstOrDefault(p =>
            p.Path.Equals("index.md", StringComparison.OrdinalIgnoreCase));
        if (indexUpd != null)
        {
            if (string.IsNullOrWhiteSpace(resp.IndexUpdate))
                resp.IndexUpdate = indexUpd.Diff;
            resp.UpdatedPages.Remove(indexUpd);
        }

        // log.md を newPages から救済
        var logNew = resp.NewPages.FirstOrDefault(p =>
            p.Path.Equals("log.md", StringComparison.OrdinalIgnoreCase));
        if (logNew != null)
        {
            if (string.IsNullOrWhiteSpace(resp.LogEntry))
                resp.LogEntry = logNew.Content;
            resp.NewPages.Remove(logNew);
        }

        // log.md を updatedPages から救済
        var logUpd = resp.UpdatedPages.FirstOrDefault(p =>
            p.Path.Equals("log.md", StringComparison.OrdinalIgnoreCase));
        if (logUpd != null)
        {
            if (string.IsNullOrWhiteSpace(resp.LogEntry))
                resp.LogEntry = logUpd.Diff;
            resp.UpdatedPages.Remove(logUpd);
        }
    }

    private static IngestLlmResponse? ParseLlmResponse(string raw)
    {
        // コードフェンスを除去
        var json = raw.Trim();
        if (json.StartsWith("```"))
        {
            var start = json.IndexOf('\n') + 1;
            var end   = json.LastIndexOf("```");
            if (end > start) json = json[start..end].Trim();
        }

        try
        {
            return JsonSerializer.Deserialize<IngestLlmResponse>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    // ---- ファイルログ ----

    public static void AppendLog(string wikiRoot, string message)
    {
        try
        {
            var logDir = Path.Combine(wikiRoot, "log");
            Directory.CreateDirectory(logDir);
            var logFile = Path.Combine(logDir, $"ingest-{DateTime.Now:yyyyMMdd}.log");
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
            File.AppendAllText(logFile, line, Encoding.UTF8);
        }
        catch { /* ログ失敗は無視 */ }
    }

    private static IngestResult Fail(string msg) => new() { Success = false, ErrorMessage = msg };

    private static IReadOnlyList<string> CollectExistingTags(string wikiRoot, IReadOnlyList<WikiPageItem> pages)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in pages.Where(p => !p.IsRoot))
        {
            var fullPath = Path.Combine(wikiRoot, page.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath)) continue;

            var content = File.ReadAllText(fullPath, Encoding.UTF8);
            foreach (var tag in ExtractFrontmatterTags(content))
                tags.Add(tag);
        }

        return tags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string BuildTagVocabulary(IReadOnlyList<string> tags)
        => tags.Count == 0 ? "(none)" : string.Join(", ", tags);

    private static void CanonicalizeResultTags(IngestLlmResponse resp, IReadOnlyList<string> existingTags)
    {
        var canonicalByKey = BuildCanonicalTagMap(existingTags);

        foreach (var p in resp.NewPages)
        {
            if (string.IsNullOrWhiteSpace(p.Content)) continue;
            p.Content = CanonicalizeFrontmatterTags(p.Content, canonicalByKey);
        }

        foreach (var u in resp.UpdatedPages)
        {
            if (string.IsNullOrWhiteSpace(u.Diff)) continue;
            u.Diff = CanonicalizeFrontmatterTags(u.Diff, canonicalByKey);
        }
    }

    private static Dictionary<string, string> BuildCanonicalTagMap(IReadOnlyList<string> existingTags)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in existingTags)
        {
            var normalized = NormalizeTagToken(tag);
            if (string.IsNullOrWhiteSpace(normalized)) continue;

            var key = TagMatchKey(normalized);
            if (!map.ContainsKey(key))
                map[key] = normalized;
        }
        return map;
    }

    private static string CanonicalizeFrontmatterTags(string markdown, Dictionary<string, string> canonicalByKey)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            markdown,
            @"(?ms)\A---\s*\r?\n(?<fm>.*?)(?:\r?\n)---");
        if (!match.Success) return markdown;

        var frontmatter = match.Groups["fm"].Value;
        var lines = frontmatter.Split('\n').ToArray();
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("tags:", StringComparison.OrdinalIgnoreCase))
                continue;

            var tags = ParseTagsLine(trimmed);
            if (tags.Count == 0) continue;

            var canonical = tags
                .Select(t =>
                {
                    var normalized = NormalizeTagToken(t);
                    var key = TagMatchKey(normalized);
                    return canonicalByKey.TryGetValue(key, out var existing) ? existing : normalized;
                })
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();

            lines[i] = $"tags: [{string.Join(", ", canonical)}]";
            break;
        }

        var newFrontmatter = string.Join('\n', lines);
        return markdown.Substring(0, match.Groups["fm"].Index)
               + newFrontmatter
               + markdown.Substring(match.Groups["fm"].Index + match.Groups["fm"].Length);
    }

    private static List<string> ExtractFrontmatterTags(string markdown)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            markdown,
            @"(?ms)\A---\s*\r?\n(?<fm>.*?)(?:\r?\n)---");
        if (!match.Success) return [];

        var lines = match.Groups["fm"].Value.Split('\n');
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (!line.StartsWith("tags:", StringComparison.OrdinalIgnoreCase))
                continue;
            return ParseTagsLine(line);
        }

        return [];
    }

    private static List<string> ParseTagsLine(string line)
    {
        var idx = line.IndexOf(':');
        if (idx < 0) return [];
        var value = line[(idx + 1)..].Trim();
        if (!value.StartsWith("[", StringComparison.Ordinal) || !value.EndsWith("]", StringComparison.Ordinal))
            return [];

        var inner = value[1..^1];
        return inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Trim().Trim('"', '\''))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
    }

    private static string NormalizeTagToken(string tag)
    {
        var t = tag.Trim().Trim('"', '\'').ToLowerInvariant();
        t = t.Replace('_', '-').Replace(' ', '-');
        t = System.Text.RegularExpressions.Regex.Replace(t, @"-+", "-");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"[^a-z0-9\-]", "");
        return t.Trim('-');
    }

    private static string TagMatchKey(string normalizedTag)
    {
        var key = normalizedTag;
        if (key.EndsWith("ies", StringComparison.Ordinal) && key.Length > 4)
            key = key[..^3] + "y";
        else if (key.EndsWith("s", StringComparison.Ordinal) && !key.EndsWith("ss", StringComparison.Ordinal) && key.Length > 3)
            key = key[..^1];
        return key;
    }

    private sealed class UpdateSelectionResponse
    {
        public List<string> UpdateCandidates { get; set; } = [];
    }
}
