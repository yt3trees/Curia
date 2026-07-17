namespace Curia.Models;

/// <summary>
/// pinned folder / git / capture_log から収集した行動シグナル。FocusUpdateService のプロンプト拡張に使う。
/// </summary>
public class FocusActivitySignals
{
    public DateTime Since { get; set; }
    public List<FileActivityEntry> PinnedFolderFiles { get; set; } = [];
    public List<WorkFolderEntry> RecentWorkFolders { get; set; } = [];
    public List<GitCommitEntry> RecentCommits { get; set; } = [];
    public List<string> UncommittedFiles { get; set; } = [];
    public List<CaptureLogEntry> Captures { get; set; } = [];

    public bool IsEmpty =>
        PinnedFolderFiles.Count == 0 &&
        RecentWorkFolders.Count == 0 &&
        RecentCommits.Count == 0 &&
        UncommittedFiles.Count == 0 &&
        Captures.Count == 0;
}

/// <summary>
/// shared/_work 配下の日付付き作業フォルダ (yyyyMMdd_feature-name)。
/// フォルダ名自体に作業日と内容が入っているため、pinned folder より直接的なシグナルになる。
/// </summary>
public class WorkFolderEntry
{
    public DateTime Date { get; set; }
    public string FeatureName { get; set; } = "";
    public string? WorkstreamLabel { get; set; }
}

public class FileActivityEntry
{
    public string RelativePath { get; set; } = "";
    public DateTime ModifiedAt { get; set; }
    public string PinnedFolderLabel { get; set; } = "";
}

public class GitCommitEntry
{
    public string Date { get; set; } = "";
    public string Message { get; set; } = "";
    public string RepoName { get; set; } = "";
}

public class CaptureLogEntry
{
    public DateTime Timestamp { get; set; }
    public string Body { get; set; } = "";
}
