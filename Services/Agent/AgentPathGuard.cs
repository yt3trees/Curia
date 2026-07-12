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
        try
        {
            var physicalRoots = roots.Select(ResolveExistingReparsePoints).ToList();
            var physicalPath = ResolveExistingReparsePoints(resolvedPath);
            if (!physicalRoots.Any(root => IsWithinRoot(physicalPath, root)))
            {
                error = "Access denied: resolved path is outside managed roots.";
                return false;
            }
            fullPath = physicalPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"Access denied: could not resolve symbolic link or junction ({ex.Message}).";
            return false;
        }
        return true;
    }

    public bool Revalidate(string fullPath, out string error) => TryResolve(fullPath, out _, out error);

    private static string ResolveExistingReparsePoints(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root)) return fullPath;
        var current = root;
        var remainder = fullPath[root.Length..];
        foreach (var segment in remainder.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0) continue;
            var target = Directory.Exists(current)
                ? new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true)
                : new FileInfo(current).ResolveLinkTarget(returnFinalTarget: true);
            if (target == null) throw new IOException($"Unable to resolve {current}.");
            current = target.FullName;
        }
        return Path.GetFullPath(current);
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return path.Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}