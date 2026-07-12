using System.IO;
using System.Text;
using Curia.Models;

namespace Curia.Services.Adapters;

/// <summary>
/// Exposes managed Wiki pages as durable cross-project knowledge-base documents.
/// Wiki pages intentionally bypass the working-log recency window because they
/// contain reference knowledge that remains relevant after it becomes old.
/// </summary>
public class WikiSourceAdapter : ICuriaSourceAdapter
{
    private const int MaxSnippetLength = 500;
    private const int MaxContentLength = 200_000;
    private readonly WikiService _wikiService;

    public WikiSourceAdapter(WikiService wikiService) => _wikiService = wikiService;

    public CuriaSourceType SourceType => CuriaSourceType.Wiki;

    public async Task<List<CuriaCandidateMeta>> EnumerateCandidatesAsync(
        IEnumerable<ProjectInfo> projects,
        DateTime since,
        CancellationToken ct)
    {
        var result = new List<CuriaCandidateMeta>();

        foreach (var project in projects)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var domain in WikiService.GetDomains(project.AiContextContentPath))
            {
                ct.ThrowIfCancellationRequested();
                var wikiRoot = WikiService.GetWikiRoot(project.AiContextContentPath, domain);
                foreach (var page in _wikiService.GetAllPages(wikiRoot).Where(page => !page.IsRoot))
                {
                    ct.ThrowIfCancellationRequested();
                    var fullPath = Path.Combine(wikiRoot, page.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    try
                    {
                        var content = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                        result.Add(new CuriaCandidateMeta
                        {
                            Path = fullPath,
                            SourceType = SourceType,
                            ProjectId = project.Name,
                            Title = $"{domain}: {page.Title}",
                            Snippet = content.Length > MaxSnippetLength ? content[..MaxSnippetLength] : content,
                            LastModified = page.LastModified,
                        });
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        return result;
    }

    public async Task<string> ReadFullContentAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return "";
        var content = await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
        return content.Length > MaxContentLength ? content[..MaxContentLength] : content;
    }
}
