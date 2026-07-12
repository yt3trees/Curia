using System.IO;
using System.Text;
using Curia.Models;

namespace Curia.Services.Adapters;

/// <summary>
/// Exposes free-form Obsidian vault notes (daily, meetings, notes, specs, troubleshooting) as
/// searchable knowledge. "ai-context" is a junction mirror already covered by the other
/// source adapters, so it is intentionally excluded here.
/// </summary>
public class ObsidianNotesSourceAdapter : ICuriaSourceAdapter
{
    private static readonly string[] Folders = ["daily", "meetings", "notes", "specs", "troubleshooting"];
    private const int MaxSnippetLength = 500;
    private const int MaxContentLength = 200_000;

    public CuriaSourceType SourceType => CuriaSourceType.ObsidianNotes;

    public async Task<List<CuriaCandidateMeta>> EnumerateCandidatesAsync(
        IEnumerable<ProjectInfo> projects,
        DateTime since,
        CancellationToken ct)
    {
        var result = new List<CuriaCandidateMeta>();

        foreach (var proj in projects)
        {
            foreach (var folder in Folders)
            {
                ct.ThrowIfCancellationRequested();
                var dir = Path.Combine(proj.AiContextPath, "obsidian_notes", folder);
                if (!Directory.Exists(dir)) continue;

                foreach (var file in Directory.GetFiles(dir, "*.md", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    var lastMod = File.GetLastWriteTime(file);
                    if (lastMod < since) continue;

                    try
                    {
                        var content = await File.ReadAllTextAsync(file, Encoding.UTF8, ct);
                        var titleLine = content.Split('\n')
                            .FirstOrDefault(l => l.StartsWith("#"))
                            ?? "";
                        var title = titleLine.TrimStart('#', ' ').Trim();
                        if (string.IsNullOrEmpty(title))
                            title = Path.GetFileNameWithoutExtension(file);

                        var snippet = content.Length > MaxSnippetLength ? content[..MaxSnippetLength] : content;

                        result.Add(new CuriaCandidateMeta
                        {
                            Path = file,
                            SourceType = CuriaSourceType.ObsidianNotes,
                            ProjectId = proj.Name,
                            Title = $"{folder}: {title}",
                            Snippet = snippet,
                            LastModified = lastMod,
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
