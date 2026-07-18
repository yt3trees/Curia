using System.IO;
using System.Text.Json.Serialization;

namespace Curia.Models;

public enum ProposalType { FocusUpdate }          // 将来: DecisionLog, MeetingFollowup

public enum ProposalStatus { Pending, Accepted, Rejected, Expired }

/// <summary>
/// バックグラウンド生成された AI 変更提案 1 件。[config_dir]\proposals\{Id}.json として永続化される。
/// </summary>
public class ProposalItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ProposalType Type { get; set; } = ProposalType.FocusUpdate;
    public ProposalStatus Status { get; set; } = ProposalStatus.Pending;
    public string ProjectName { get; set; } = "";
    public string? WorkstreamId { get; set; }
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string TargetFocusPath { get; set; } = "";
    public DateTime TargetFileLastWriteAt { get; set; }   // 陳腐化検出用
    public string CurrentContent { get; set; } = "";
    public string ProposedContent { get; set; } = "";
    public string? BackupPath { get; set; }

    // ---- 表示用 (永続化しない) ----

    [JsonIgnore]
    public string CreatedAgoText
    {
        get
        {
            var span = DateTime.Now - CreatedAt;
            if (span.TotalMinutes < 60) return $"{Math.Max(0, (int)span.TotalMinutes)}m ago";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
            return $"{(int)span.TotalDays}d ago";
        }
    }

    [JsonIgnore]
    public string SummaryFirstLine
    {
        get
        {
            var idx = Summary.IndexOf('\n');
            return (idx >= 0 ? Summary[..idx] : Summary).Trim();
        }
    }

    /// <summary>FocusUpdateResult から ProposalItem を生成する。</summary>
    public static ProposalItem FromFocusResult(FocusUpdateResult result, string projectName)
    {
        var wsId = string.IsNullOrWhiteSpace(result.WorkstreamId) ? null : result.WorkstreamId;
        var scope = wsId ?? "general";
        DateTime lastWrite;
        try { lastWrite = File.GetLastWriteTime(result.TargetFocusPath); }
        catch { lastWrite = DateTime.Now; }

        return new ProposalItem
        {
            Type = ProposalType.FocusUpdate,
            Status = ProposalStatus.Pending,
            ProjectName = projectName,
            WorkstreamId = wsId,
            Title = $"{projectName}: Focus update ({scope})",
            Summary = result.Summary,
            CreatedAt = DateTime.Now,
            TargetFocusPath = result.TargetFocusPath,
            TargetFileLastWriteAt = lastWrite,
            CurrentContent = result.CurrentContent,
            ProposedContent = result.ProposedContent,
            BackupPath = result.BackupPath,
        };
    }

    /// <summary>Accept 時に既存の ApplyProposalAsync へ渡すための FocusUpdateResult を復元する。</summary>
    public FocusUpdateResult ToFocusUpdateResult() => new()
    {
        CurrentContent = CurrentContent,
        ProposedContent = ProposedContent,
        Summary = Summary,
        TargetFocusPath = TargetFocusPath,
        BackupPath = BackupPath ?? "",
        BackupStatus = BackupStatus.AlreadyExists,
        WorkMode = string.IsNullOrWhiteSpace(WorkstreamId) ? WorkMode.General : WorkMode.SharedWork,
        WorkstreamId = WorkstreamId ?? "",
    };
}
