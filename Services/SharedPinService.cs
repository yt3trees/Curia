using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Curia.Models;

namespace Curia.Services;

/// <summary>
/// Pinned Folder の PC 間共有。
/// 各プロジェクトの shared フォルダ (クラウド同期 junction) 内の
/// .curia\shared_pins.json に共有ピンを登録し、別 PC ではそこから
/// ローカルの Pinned Folders へ取り込む。
/// </summary>
public class SharedPinService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string GetSharedPinsFilePath(string projectPath)
        => Path.Combine(projectPath, "shared", ".curia", "shared_pins.json");

    public List<SharedPinEntry> LoadSharedPins(string projectPath)
    {
        var path = GetSharedPinsFilePath(projectPath);
        if (!File.Exists(path))
            return [];

        try
        {
            var content = File.ReadAllText(path, Utf8NoBom);
            return JsonSerializer.Deserialize<List<SharedPinEntry>>(content, JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SharedPinService] LoadSharedPins error ({projectPath}): {ex}");
            return [];
        }
    }

    private static void SaveSharedPins(string projectPath, List<SharedPinEntry> entries)
    {
        var path = GetSharedPinsFilePath(projectPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(entries, JsonOptions);
        File.WriteAllText(path, json, Utf8NoBom);
    }

    /// <summary>
    /// ピンをリモート共有に登録する。shared フォルダが存在しない場合は false。
    /// 既に登録済みの場合は true (冪等)。
    /// </summary>
    public bool SharePin(string projectPath, PinnedFolder pf)
    {
        var sharedRoot = Path.Combine(projectPath, "shared");
        if (!Directory.Exists(sharedRoot))
            return false;

        var entries = LoadSharedPins(projectPath);
        if (entries.Any(e => Matches(e, pf)))
            return true;

        var entry = new SharedPinEntry
        {
            Workstream = pf.Workstream,
            Folder = pf.Folder,
            SharedAt = DateTime.Today.ToString("yyyy-MM-dd"),
            SharedBy = Environment.MachineName,
        };

        var relative = TryGetSharedRelativePath(sharedRoot, pf.FullPath);
        if (relative != null)
            entry.RelativePath = relative;
        else
            entry.AbsolutePath = pf.FullPath;

        entries.Add(entry);
        SaveSharedPins(projectPath, entries);
        return true;
    }

    /// <summary>ピンのリモート共有登録を解除する。</summary>
    public void UnsharePin(string projectPath, PinnedFolder pf)
    {
        var entries = LoadSharedPins(projectPath);
        var removed = entries.RemoveAll(e => Matches(e, pf));
        if (removed > 0)
            SaveSharedPins(projectPath, entries);
    }

    public static bool Matches(SharedPinEntry entry, PinnedFolder pf)
        => string.Equals(entry.Workstream, pf.Workstream, StringComparison.OrdinalIgnoreCase)
           && string.Equals(entry.Folder, pf.Folder, StringComparison.OrdinalIgnoreCase);

    /// <summary>共有ピンエントリをこの PC 上のフルパスに解決する。</summary>
    public static string ResolveFullPath(string projectPath, SharedPinEntry entry)
        => entry.RelativePath != null
            ? Path.Combine(projectPath, "shared", entry.RelativePath)
            : entry.AbsolutePath ?? "";

    private static string? TryGetSharedRelativePath(string sharedRoot, string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return null;

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sharedRoot));
        var normalizedFull = Path.GetFullPath(fullPath);
        if (!normalizedFull.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;

        return Path.GetRelativePath(normalizedRoot, normalizedFull);
    }

    /// <summary>
    /// 全プロジェクトの共有ピンを走査し、ローカル未登録の取り込み候補を返す。
    /// </summary>
    public async Task<List<RemotePinCandidate>> CollectCandidatesAsync(
        IReadOnlyList<(string Name, string Path)> projects,
        IReadOnlyList<PinnedFolder> localPins)
    {
        return await Task.Run(() =>
        {
            var candidates = new List<RemotePinCandidate>();
            foreach (var (name, path) in projects)
            {
                foreach (var entry in LoadSharedPins(path))
                {
                    var alreadyPinned = localPins.Any(p =>
                        string.Equals(p.Project, name, StringComparison.OrdinalIgnoreCase)
                        && Matches(entry, p));
                    if (alreadyPinned) continue;

                    candidates.Add(new RemotePinCandidate
                    {
                        Pin = new PinnedFolder
                        {
                            Project = name,
                            Workstream = entry.Workstream,
                            Folder = entry.Folder,
                            FullPath = ResolveFullPath(path, entry),
                            PinnedAt = DateTime.Today.ToString("yyyy-MM-dd"),
                        },
                        SharedBy = entry.SharedBy,
                        SharedAt = entry.SharedAt,
                    });
                }
            }
            return candidates;
        });
    }
}
