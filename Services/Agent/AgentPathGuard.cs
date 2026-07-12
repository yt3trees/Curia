using System.IO;
using Curia.Services;

namespace Curia.Services.Agent;

public class AgentPathGuard
{
    private readonly ConfigService _config;

    public AgentPathGuard(ConfigService config) => _config = config;

    public bool TryResolve(string requestedPath, out string fullPath, out string error)
    {
        fullPath = "";
        error = "";
        if (string.IsNullOrWhiteSpace(requestedPath) || requestedPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p == ".."))
        {
            error = "Access denied: path traversal is not allowed.";
            return false;
        }

        try { fullPath = Path.GetFullPath(requestedPath); }
        catch (Exception ex) { error = $"Invalid path: {ex.Message}"; return false; }

        var settings = _config.LoadSettings();
        var resolvedPath = fullPath;
        var roots = new[] { settings.LocalProjectsRoot, settings.CloudSyncRoot, settings.ObsidianVaultRoot, _config.ConfigDir }
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath);
        if (!roots.Any(root => IsWithinRoot(resolvedPath, root)))
        {
            error = "Access denied: path is outside managed roots.";
            return false;
        }
        return true;
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return path.Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}