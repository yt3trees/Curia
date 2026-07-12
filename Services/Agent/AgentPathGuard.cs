using System.IO;
using Curia.Services;

namespace Curia.Services.Agent;

public class AgentPathGuard
{
    private readonly ConfigService _config;

    public AgentPathGuard(ConfigService config) => _config = config;

    public bool TryResolve(string requestedPath, out string fullPath, out string error)
    {
        var settings = _config.LoadSettings();
        return TryResolveWithinRoots(requestedPath,
            [settings.LocalProjectsRoot, settings.CloudSyncRoot, settings.ObsidianVaultRoot],
            out fullPath, out error);
    }

    public bool TryResolveWithinRoots(string requestedPath, IEnumerable<string?> allowedRoots,
        out string fullPath, out string error)
    {
        fullPath = "";
        error = "";
        if (string.IsNullOrWhiteSpace(requestedPath) || !Path.IsPathRooted(requestedPath)
            || requestedPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part == ".."))
        {
            error = "Access denied: path traversal is not allowed.";
            return false;
        }

        string resolvedPath;
        try { resolvedPath = Path.GetFullPath(requestedPath); }
        catch (Exception ex) { error = $"Invalid path: {ex.Message}"; return false; }

        var roots = allowedRoots.Where(root => !string.IsNullOrWhiteSpace(root)).Select(root => Path.GetFullPath(root!)).ToList();
        if (!roots.Any(root => IsWithinRoot(resolvedPath, root)))
        {
            error = "Access denied: path is outside managed roots.";
            return false;
        }

        // Do not follow junctions or symbolic links. Rejecting them is deliberate: resolving
        // arbitrary reparse points leaves a TOCTOU gap between validation and file access.
        if (ContainsReparsePoint(resolvedPath, out var reparsePath))
        {
            error = $"Access denied: symbolic links and junctions are not allowed ({reparsePath}).";
            return false;
        }

        fullPath = resolvedPath;
        return true;
    }

    public bool Revalidate(string fullPath, out string error) => TryResolve(fullPath, out _, out error);

    private static bool ContainsReparsePoint(string fullPath, out string reparsePath)
    {
        reparsePath = "";
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root)) return false;
        var current = root;
        var remainder = fullPath[root.Length..];
        foreach (var segment in remainder.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0) continue;
                reparsePath = current;
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                reparsePath = current;
                return true;
            }
        }
        return false;
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return path.Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}