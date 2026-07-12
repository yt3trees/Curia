using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Models;

namespace Curia.Services;

public class CuriaQueryService
{
    private readonly LlmClientService _llm;
    private readonly ConfigService _configService;
    private readonly ProjectDiscoveryService _discovery;
    private readonly IReadOnlyList<ICuriaSourceAdapter> _adapters;

    private List<CuriaCandidateMeta>? _candidateCache;
    private DateTime _cacheExpiry = DateTime.MinValue;

    private const int CacheTtlMinutes = 10;
    private const int MaxCandidates = 300;
    private const int MaxSelectedFiles = 8;
    private const int SnippetLength = 500;
    private const int DefaultRecencyWindowDays = 90;
    private const int MaxFullContentBytes = 200_000;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Matches [path:L42] "excerpt" or [path] optionally
    private static readonly Regex CitationRegex = new(
        @"\[(?<path>[^\]:]+?)(?::L(?<line>\d+))?\](?:\s*""(?<excerpt>[^""]{1,200})"")?",
        RegexOptions.Compiled);

    public CuriaQueryService(
        LlmClientService llm,
        ConfigService configService,
        ProjectDiscoveryService discovery,
        IEnumerable<ICuriaSourceAdapter> adapters)
    {
        _llm = llm;
        _configService = configService;
        _discovery = discovery;
        _adapters = adapters.ToList().AsReadOnly();
    }

    public async Task WarmCacheAsync(CancellationToken ct = default)
    {
        if (DateTime.Now < _cacheExpiry) return;
        await GetCandidatesInternalAsync(null, ct);
    }

    public async Task<CuriaAnswer> AskAsync(
        string question,
        CuriaQueryOptions? options,
        IReadOnlyList<(string role, string content)>? conversationHistory,
        CancellationToken ct)
    {
        var settings = _configService.LoadSettings();
        if (!settings.AiEnabled)
            throw new InvalidOperationException("AI features are not enabled.");

        var candidates = await GetCandidatesAsync(options, ct);

        if (candidates.Count == 0)
        {
            return new CuriaAnswer
            {
                Question = question,
                AnswerText = "No candidate documents found. Ensure projects have AI context files.",
                GeneratedAt = DateTime.Now,
            };
        }

        var selectedPaths = await SelectFilesAsync(candidates, question, ct);

        var pathToMeta = candidates.ToDictionary(m => m.Path, StringComparer.OrdinalIgnoreCase);
        var selectedMeta = selectedPaths
            .Where(p => pathToMeta.ContainsKey(p))
            .Select(p => pathToMeta[p])
            .ToList();

        return await GenerateAnswerAsync(question, selectedPaths, selectedMeta, conversationHistory, ct);
    }

    // ---- Cache & Stage 0 ----

    private async Task<List<CuriaCandidateMeta>> GetCandidatesAsync(
        CuriaQueryOptions? options, CancellationToken ct)
    {
        var all = await GetCandidatesInternalAsync(options, ct);
        return FilterByOptions(all, options);
    }

    private async Task<List<CuriaCandidateMeta>> GetCandidatesInternalAsync(
        CuriaQueryOptions? options, CancellationToken ct)
    {
        if (_candidateCache != null && DateTime.Now < _cacheExpiry)
            return _candidateCache;

        var projects = await _discovery.GetProjectInfoListAsync(ct: ct);
        var since = DateTime.Now.AddDays(-(options?.RecencyWindowDays ?? DefaultRecencyWindowDays));

        var adapterTasks = _adapters
            .Select(a => a.EnumerateCandidatesAsync(projects, since, ct));
        var results = await Task.WhenAll(adapterTasks);

        var all = results
            .SelectMany(r => r)
            .OrderByDescending(m => m.LastModified)
            .Take(MaxCandidates)
            .ToList();

        _candidateCache = all;
        _cacheExpiry = DateTime.Now.AddMinutes(CacheTtlMinutes);

        return all;
    }

    private static List<CuriaCandidateMeta> FilterByOptions(
        List<CuriaCandidateMeta> all, CuriaQueryOptions? options)
    {
        if (options?.SourceTypes == null) return all;
        var set = options.SourceTypes.ToHashSet();
        return all.Where(c => set.Contains(c.SourceType)).ToList();
    }

    public void InvalidateCache() => _cacheExpiry = DateTime.MinValue;

    /// <summary>Returns primary-source matches without invoking an LLM.</summary>
    public async Task<List<CuriaKnowledgeMatch>> SearchSourcesAsync(string query, CuriaQueryOptions? options,
        string? projectId, int limit, bool includeContent, CancellationToken ct)
    {
        var candidates = await GetCandidatesAsync(options, ct);
        var terms = query.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = candidates.Where(candidate => string.IsNullOrWhiteSpace(projectId)
                || candidate.ProjectId.Equals(projectId, StringComparison.OrdinalIgnoreCase))
            .Where(candidate => terms.Length == 0 || terms.All(term =>
                candidate.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
                || candidate.Snippet.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(candidate => candidate.LastModified)
            .Take(Math.Clamp(limit, 1, 100))
            .ToList();

        var result = new List<CuriaKnowledgeMatch>();
        foreach (var candidate in matches)
        {
            ct.ThrowIfCancellationRequested();
            string? content = null;
            if (includeContent)
            {
                var adapter = _adapters.FirstOrDefault(item => item.SourceType == candidate.SourceType);
                if (adapter != null)
                {
                    content = await adapter.ReadFullContentAsync(candidate.Path, ct);
                    if (content.Length > 8_000) content = content[..8_000];
                }
            }
            result.Add(new CuriaKnowledgeMatch
            {
                Path = candidate.Path, SourceType = candidate.SourceType, ProjectId = candidate.ProjectId,
                Title = candidate.Title, Excerpt = candidate.Snippet, LastModified = candidate.LastModified, Content = content
            });
        }
        return result;
    }

    // ---- Stage 1: file selection ----

    private async Task<List<string>> SelectFilesAsync(
        List<CuriaCandidateMeta> candidates, string question, CancellationToken ct)
    {
        var sb = new StringBuilder();
        foreach (var c in candidates)
        {
            var snippet = c.Snippet.Replace('\n', ' ');
            if (snippet.Length > SnippetLength) snippet = snippet[..SnippetLength];
            sb.AppendLine($"{c.Path}\t{c.SourceType}\t{c.ProjectId}\t{c.LastModified:yyyy-MM-dd}");
            sb.AppendLine($"  title: {c.Title}");
            sb.AppendLine($"  snippet: {snippet}");
            sb.AppendLine("---");
        }

        var systemPrompt = """
You are a retrieval assistant for a personal project management tool.
Given a list of candidate documents, pick up to 8 paths most relevant to the user's question.
Output JSON: {"paths": ["...", "..."]}.
Copy paths EXACTLY as listed (left column, before the tab). Do not invent paths.
""";

        var userPrompt = $"""
Question: {question}

Candidates:
{sb}
""";

        string response;
        try
        {
            response = await _llm.ChatCompletionAsync(systemPrompt, userPrompt, ct);
        }
        catch
        {
            return FallbackSelectPaths(candidates);
        }

        try
        {
            var start = response.IndexOf('{');
            var end = response.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                var json = response[start..(end + 1)];
                var doc = JsonDocument.Parse(json);
                var knownPaths = new HashSet<string>(
                    candidates.Select(c => c.Path), StringComparer.OrdinalIgnoreCase);
                var selected = doc.RootElement.GetProperty("paths")
                    .EnumerateArray()
                    .Select(e => e.GetString() ?? "")
                    .Where(p => !string.IsNullOrEmpty(p) && knownPaths.Contains(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(MaxSelectedFiles)
                    .ToList();
                if (selected.Count > 0) return selected;
            }
        }
        catch { }

        return FallbackSelectPaths(candidates);
    }

    private static List<string> FallbackSelectPaths(List<CuriaCandidateMeta> candidates)
        => candidates.Take(5).Select(c => c.Path).ToList();

    // ---- Stage 2: answer generation ----

    private async Task<CuriaAnswer> GenerateAnswerAsync(
        string question,
        List<string> selectedPaths,
        List<CuriaCandidateMeta> selectedMeta,
        IReadOnlyList<(string role, string content)>? conversationHistory,
        CancellationToken ct)
    {
        var language = _configService.LoadSettings().LlmLanguage;
        var docsSb = new StringBuilder();
        var validPaths = new List<string>();

        foreach (var meta in selectedMeta)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var adapter = _adapters.FirstOrDefault(a => a.SourceType == meta.SourceType);
                if (adapter == null) continue;

                var content = await adapter.ReadFullContentAsync(meta.Path, ct);
                if (string.IsNullOrWhiteSpace(content)) continue;

                if (Encoding.UTF8.GetByteCount(content) > MaxFullContentBytes)
                    content = content[..MaxFullContentBytes];

                docsSb.AppendLine($"=== {meta.Path} ({meta.SourceType}, {meta.ProjectId}) ===");
                docsSb.AppendLine(content);
                docsSb.AppendLine();
                validPaths.Add(meta.Path);
            }
            catch { }
        }

        if (validPaths.Count == 0)
        {
            return new CuriaAnswer
            {
                Question = question,
                AnswerText = "Could not read the selected documents.",
                SelectedPaths = selectedPaths,
                GeneratedAt = DateTime.Now,
            };
        }

        var systemPrompt = $"""
You are Curia's knowledge assistant. Answer the user's question using ONLY the provided documents.
- Cite every claim with the source path in square brackets: [path:L<line>] "excerpt up to 120 chars"
- Example: [_ai-context/decision_log/db.md:L42] "Decided to use PostgreSQL because..."
- If the documents do not contain the answer, say so explicitly.
- Respond in {language}.
""";

        var userPrompt = $"""
Question: {question}

Documents:
{docsSb}
""";

        string rawAnswer;
        try
        {
            if (conversationHistory != null && conversationHistory.Count > 0)
            {
                // Multi-turn: prepend previous Q&A, append current user message
                var messages = new List<(string role, string content)>(conversationHistory)
                {
                    ("user", userPrompt)
                };
                rawAnswer = await _llm.ChatWithHistoryAsync(systemPrompt, messages, ct);
            }
            else
            {
                rawAnswer = await _llm.ChatCompletionAsync(systemPrompt, userPrompt, ct);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CuriaAnswer
            {
                Question = question,
                AnswerText = $"Error generating answer: {ex.Message}",
                SelectedPaths = selectedPaths,
                GeneratedAt = DateTime.Now,
            };
        }

        var citations = ExtractCitations(rawAnswer, selectedMeta);

        return new CuriaAnswer
        {
            Question = question,
            AnswerText = rawAnswer,
            Citations = citations,
            SelectedPaths = selectedPaths,
            GeneratedAt = DateTime.Now,
        };
    }

    // ---- Citation extraction (D4) ----

    private static List<CuriaCitation> ExtractCitations(
        string answer, List<CuriaCandidateMeta> selectedMeta)
    {
        var pathToMeta = selectedMeta.ToDictionary(m => m.Path, StringComparer.OrdinalIgnoreCase);
        var citations = new List<CuriaCitation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in CitationRegex.Matches(answer))
        {
            var citedPath = m.Groups["path"].Value.Trim();
            var lineStr = m.Groups["line"].Value;
            var excerpt = m.Groups["excerpt"].Success ? m.Groups["excerpt"].Value : null;

            // Try exact match first, then suffix match
            var matchedPath = pathToMeta.Keys.FirstOrDefault(k =>
                string.Equals(k, citedPath, StringComparison.OrdinalIgnoreCase))
                ?? pathToMeta.Keys.FirstOrDefault(k =>
                    k.EndsWith(citedPath.Replace('/', Path.DirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase))
                ?? citedPath;

            if (seen.Contains(matchedPath)) continue;
            seen.Add(matchedPath);

            pathToMeta.TryGetValue(matchedPath, out var meta);

            // Grep fallback: resolve line hint from excerpt when LLM line is absent (D4)
            int? lineHint = int.TryParse(lineStr, out var ln) ? ln : null;
            if (lineHint == null && excerpt != null && meta != null)
                lineHint = TryResolveLineFromExcerpt(meta.Path, excerpt);

            citations.Add(new CuriaCitation
            {
                Path = matchedPath,
                SourceType = meta?.SourceType ?? CuriaSourceType.DecisionLog,
                ProjectId = meta?.ProjectId ?? "",
                LineHint = lineHint,
                Excerpt = excerpt,
            });
        }

        return citations;
    }

    private static int? TryResolveLineFromExcerpt(string path, string excerpt)
    {
        // Strip task anchor if present
        var hashIdx = path.LastIndexOf('#');
        var filePath = hashIdx >= 0 ? path[..hashIdx] : path;

        if (!File.Exists(filePath)) return null;
        try
        {
            var lines = File.ReadAllLines(filePath);
            // Exact match
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].Contains(excerpt, StringComparison.OrdinalIgnoreCase))
                    return i + 1;
            // Partial match (40+ chars)
            if (excerpt.Length >= 40)
            {
                var sub = excerpt[..40];
                for (int i = 0; i < lines.Length; i++)
                    if (lines[i].Contains(sub, StringComparison.OrdinalIgnoreCase))
                        return i + 1;
            }
            // First heading
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].StartsWith("#"))
                    return i + 1;
        }
        catch { }
        return null;
    }
}
