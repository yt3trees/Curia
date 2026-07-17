using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Curia.Models;

namespace Curia.Services;

/// <summary>
/// pinned folder のファイル活動、git 活動、capture_log.md のエントリから
/// current_focus.md 自動更新の入力となる行動シグナルを収集する。ローカル I/O のみで LLM は呼ばない。
/// </summary>
public class FocusSignalCollectorService
{
    private static readonly HashSet<string> ExcludedDirNames =
        new(StringComparer.OrdinalIgnoreCase) { "node_modules", ".git", "bin", "obj", ".vs" };

    private const int MaxScanDepth = 6;
    private const int MaxFilesPerFolder = 500;
    private const int MaxTotalChars = 8000;

    private readonly ConfigService _configService;
    private readonly CaptureService _captureService;
    private readonly FileEncodingService _encoding;

    public FocusSignalCollectorService(
        ConfigService configService,
        CaptureService captureService,
        FileEncodingService encoding)
    {
        _configService = configService;
        _captureService = captureService;
        _encoding = encoding;
    }

    public async Task<FocusActivitySignals> CollectAsync(ProjectInfo project, CancellationToken ct = default)
    {
        var settings = _configService.LoadSettings();
        var lookbackDays = settings.FocusSignalLookbackDays > 0 ? settings.FocusSignalLookbackDays : 14;
        var cutoff = DateTime.Today.AddDays(-lookbackDays);
        var latestSnapshot = project.FocusHistoryDates.Count > 0 ? project.FocusHistoryDates.Max() : (DateTime?)null;
        var since = latestSnapshot.HasValue && latestSnapshot.Value >= cutoff ? latestSnapshot.Value : cutoff;

        var signals = new FocusActivitySignals { Since = since };

        signals.PinnedFolderFiles = await Task.Run(() => CollectPinnedFolderFiles(project, since), ct);
        signals.RecentWorkFolders = await Task.Run(() => CollectWorkFolders(project, since), ct);

        var (commits, uncommitted) = await Task.Run(() => CollectGitActivity(project, since), ct);
        signals.RecentCommits = commits;
        signals.UncommittedFiles = uncommitted;

        signals.Captures = await CollectCaptureLogEntriesAsync(project, since, ct);

        ApplySizeCap(signals);
        return signals;
    }

    // -----------------------------------------------------------------------
    private List<FileActivityEntry> CollectPinnedFolderFiles(ProjectInfo project, DateTime since)
    {
        var pinned = _configService.LoadPinnedFolders()
            .Where(p => string.Equals(p.Project, project.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var entries = new List<FileActivityEntry>();
        foreach (var pf in pinned)
        {
            if (!Directory.Exists(pf.FullPath)) continue;
            try
            {
                ScanFolder(pf.FullPath, pf.FullPath, pf.ProjectLabel, since, 0, entries);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FocusSignalCollectorService] ScanFolder failed for {pf.FullPath}: {ex.Message}");
            }
        }

        return entries
            .OrderByDescending(e => e.ModifiedAt)
            .Take(20)
            .ToList();
    }

    private static void ScanFolder(
        string root, string dir, string label, DateTime since, int depth, List<FileActivityEntry> entries)
    {
        if (depth > MaxScanDepth || entries.Count >= MaxFilesPerFolder) return;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir); }
        catch { return; }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("~$", StringComparison.Ordinal)) continue;

            DateTime modified;
            try { modified = File.GetLastWriteTime(file); }
            catch { continue; }

            if (modified < since) continue;

            var relative = file.Length > root.Length
                ? file[root.Length..].TrimStart('\\', '/')
                : name;

            entries.Add(new FileActivityEntry
            {
                RelativePath = relative.Replace('\\', '/'),
                ModifiedAt = modified,
                PinnedFolderLabel = label,
            });
        }

        IEnumerable<string> subDirs;
        try { subDirs = Directory.EnumerateDirectories(dir); }
        catch { return; }

        foreach (var sub in subDirs)
        {
            if (ExcludedDirNames.Contains(Path.GetFileName(sub))) continue;
            ScanFolder(root, sub, label, since, depth + 1, entries);
        }
    }

    // -----------------------------------------------------------------------
    // shared/_work 配下は次のいずれかの構造:
    //   general:    shared/_work/{yyyy}/{yyyyMM}/{yyyyMMdd}_{feature}
    //   workstream: shared/_work/{workstreamId}/{yyyyMM}/{yyyyMMdd}_{feature}
    private static readonly Regex YearDirRegex = new(@"^\d{4}$");
    private static readonly Regex YearMonthDirRegex = new(@"^\d{6}$");
    private static readonly Regex DayFeatureDirRegex = new(@"^(\d{8})_(.+)$");

    private static List<WorkFolderEntry> CollectWorkFolders(ProjectInfo project, DateTime since)
    {
        var entries = new List<WorkFolderEntry>();
        var workRoot = Path.Combine(project.Path, "shared", "_work");
        if (!Directory.Exists(workRoot)) return entries;

        IEnumerable<string> topDirs;
        try { topDirs = Directory.EnumerateDirectories(workRoot); }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FocusSignalCollectorService] work root scan failed: {ex.Message}");
            return entries;
        }

        foreach (var topDir in topDirs)
        {
            var topName = Path.GetFileName(topDir);
            if (YearDirRegex.IsMatch(topName))
            {
                // general: yyyy/yyyyMM/yyyyMMdd_feature
                CollectDayFolders(topDir, workstreamLabel: null, since, entries);
            }
            else
            {
                // workstream: {workstreamId}/yyyyMM/yyyyMMdd_feature
                var ws = project.Workstreams.FirstOrDefault(w => string.Equals(w.Id, topName, StringComparison.OrdinalIgnoreCase));
                var label = ws?.Label ?? topName;
                CollectYearMonthFolders(topDir, label, since, entries);
            }
        }

        return entries
            .OrderByDescending(e => e.Date)
            .Take(20)
            .ToList();
    }

    private static void CollectDayFolders(string yearDir, string? workstreamLabel, DateTime since, List<WorkFolderEntry> entries)
    {
        IEnumerable<string> yearMonthDirs;
        try { yearMonthDirs = Directory.EnumerateDirectories(yearDir); }
        catch { return; }

        foreach (var ymDir in yearMonthDirs)
        {
            if (!YearMonthDirRegex.IsMatch(Path.GetFileName(ymDir))) continue;
            CollectYearMonthFolders(ymDir, workstreamLabel, since, entries);
        }
    }

    private static void CollectYearMonthFolders(string yearMonthDir, string? workstreamLabel, DateTime since, List<WorkFolderEntry> entries)
    {
        IEnumerable<string> dayDirs;
        try { dayDirs = Directory.EnumerateDirectories(yearMonthDir); }
        catch { return; }

        foreach (var dayDir in dayDirs)
        {
            var match = DayFeatureDirRegex.Match(Path.GetFileName(dayDir));
            if (!match.Success) continue;
            if (!DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMdd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var date))
                continue;
            if (date < since) continue;

            entries.Add(new WorkFolderEntry
            {
                Date = date,
                FeatureName = match.Groups[2].Value,
                WorkstreamLabel = workstreamLabel,
            });
        }
    }

    // -----------------------------------------------------------------------
    private static (List<GitCommitEntry> commits, List<string> uncommitted) CollectGitActivity(
        ProjectInfo project, DateTime since)
    {
        var commits = new List<GitCommitEntry>();
        var uncommitted = new List<string>();

        var devSource = Path.Combine(project.Path, "development", "source");
        if (!Directory.Exists(devSource)) return (commits, uncommitted);

        IEnumerable<string> gitDirs;
        try { gitDirs = Directory.EnumerateDirectories(devSource, ".git", SearchOption.AllDirectories); }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FocusSignalCollectorService] git dir scan failed: {ex.Message}");
            return (commits, uncommitted);
        }

        foreach (var gitDir in gitDirs)
        {
            var repoPath = Path.GetDirectoryName(gitDir);
            if (string.IsNullOrWhiteSpace(repoPath)) continue;

            var repoName = repoPath.Length > devSource.Length
                ? repoPath[devSource.Length..].TrimStart('\\', '/').Replace('\\', '/')
                : ".";

            var log = RunGit(repoPath, $"log --since={since:yyyy-MM-dd} --pretty=format:\"%ad %s\" --date=short -n 20");
            if (log != null)
            {
                foreach (var line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = line.Trim();
                    var spaceIdx = trimmed.IndexOf(' ');
                    if (spaceIdx <= 0) continue;
                    commits.Add(new GitCommitEntry
                    {
                        Date = trimmed[..spaceIdx],
                        Message = trimmed[(spaceIdx + 1)..].Trim(),
                        RepoName = repoName,
                    });
                }
            }

            var status = RunGit(repoPath, "status --porcelain");
            if (status != null)
            {
                foreach (var line in status.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var fileName = line.Length > 3 ? line[3..].Trim() : line.Trim();
                    if (fileName.Length == 0) continue;
                    uncommitted.Add(repoName == "." ? fileName : $"{repoName}/{fileName}");
                }
            }
        }

        var orderedCommits = commits.OrderByDescending(c => c.Date).Take(20).ToList();
        return (orderedCommits, uncommitted.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static string? RunGit(string repoPath, string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"-C \"{repoPath}\" {arguments}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };
            process.Start();
            var stdout = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? stdout : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FocusSignalCollectorService] git command failed ({repoPath}): {ex.Message}");
            return null;
        }
    }

    // -----------------------------------------------------------------------
    private async Task<List<CaptureLogEntry>> CollectCaptureLogEntriesAsync(
        ProjectInfo project, DateTime since, CancellationToken ct)
    {
        var logPath = Path.Combine(_configService.ConfigDir, "capture_log.md");
        if (!File.Exists(logPath)) return [];

        string content;
        try
        {
            (content, _) = await _encoding.ReadFileAsync(logPath, ct);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FocusSignalCollectorService] capture_log read failed: {ex.Message}");
            return [];
        }

        var aliases = _captureService.LoadAnkenAliases(project);
        var headingRegex = new Regex(@"(?m)^##\s+(\d{4}-\d{2}-\d{2})\s+(\d{2}:\d{2})\s*$");
        var matches = headingRegex.Matches(content).Cast<Match>().ToList();

        var entries = new List<CaptureLogEntry>();
        for (int i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            if (!DateTime.TryParse($"{match.Groups[1].Value} {match.Groups[2].Value}", out var timestamp))
                continue;
            if (timestamp < since) continue;

            var bodyStart = match.Index + match.Length;
            var bodyEnd = i + 1 < matches.Count ? matches[i + 1].Index : content.Length;
            var body = content[bodyStart..bodyEnd].Trim();
            if (body.Length == 0) continue;

            if (!aliases.Any(a => body.Contains(a, StringComparison.OrdinalIgnoreCase))) continue;

            entries.Add(new CaptureLogEntry
            {
                Timestamp = timestamp,
                Body = body.Length > 500 ? body[..500] + "..." : body,
            });
        }

        return entries
            .OrderByDescending(e => e.Timestamp)
            .Take(10)
            .ToList();
    }

    // -----------------------------------------------------------------------
    private static void ApplySizeCap(FocusActivitySignals signals)
    {
        int EstimateSize() =>
            signals.PinnedFolderFiles.Sum(f => f.RelativePath.Length + 40) +
            signals.RecentWorkFolders.Sum(f => f.FeatureName.Length + 40) +
            signals.RecentCommits.Sum(c => c.Message.Length + 40) +
            signals.UncommittedFiles.Sum(f => f.Length + 4) +
            signals.Captures.Sum(c => c.Body.Length + 20);

        while (EstimateSize() > MaxTotalChars)
        {
            if (signals.Captures.Count > 0) { signals.Captures.RemoveAt(signals.Captures.Count - 1); continue; }
            if (signals.PinnedFolderFiles.Count > 0) { signals.PinnedFolderFiles.RemoveAt(signals.PinnedFolderFiles.Count - 1); continue; }
            if (signals.RecentWorkFolders.Count > 0) { signals.RecentWorkFolders.RemoveAt(signals.RecentWorkFolders.Count - 1); continue; }
            if (signals.RecentCommits.Count > 0) { signals.RecentCommits.RemoveAt(signals.RecentCommits.Count - 1); continue; }
            if (signals.UncommittedFiles.Count > 0) { signals.UncommittedFiles.RemoveAt(signals.UncommittedFiles.Count - 1); continue; }
            break;
        }
    }
}
