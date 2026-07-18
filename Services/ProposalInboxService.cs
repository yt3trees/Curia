using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Curia.Models;

namespace Curia.Services;

/// <summary>
/// バックグラウンド生成された AI 提案の永続的なインボックス。
/// 保存先: [config_dir]\proposals\{Id}.json、処理済みは proposals\_archive\ へ移動する。
/// 変更時は ProposalInboxChangedMessage を発行する。
/// </summary>
public class ProposalInboxService
{
    private const int MaxPendingCount = 5;      // Pending 合計の上限。超過時は古いものを Expired に落とす
    private const int ArchiveRetentionDays = 30;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ConfigService _configService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _pendingCount = -1;

    public ProposalInboxService(ConfigService configService)
    {
        _configService = configService;
    }

    private string ProposalsDir => Path.Combine(_configService.ConfigDir, "proposals");
    private string ArchiveDir => Path.Combine(ProposalsDir, "_archive");

    /// <summary>Pending 件数 (キャッシュ)。未ロード時はファイル数から算出する。</summary>
    public int PendingCount
    {
        get
        {
            if (_pendingCount >= 0) return _pendingCount;
            try
            {
                _pendingCount = Directory.Exists(ProposalsDir)
                    ? Directory.EnumerateFiles(ProposalsDir, "*.json").Count()
                    : 0;
            }
            catch { _pendingCount = 0; }
            return _pendingCount;
        }
    }

    /// <summary>起動時処理: 陳腐化チェック + 古いアーカイブの削除 + 初期件数の通知。</summary>
    public async Task InitializeAsync()
    {
        try
        {
            await ExpireStaleAsync();
            CleanupOldArchives();
            Notify(await GetPendingCountAsync());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProposalInbox] InitializeAsync failed: {ex}");
        }
    }

    public async Task<List<ProposalItem>> LoadPendingAsync()
    {
        await _gate.WaitAsync();
        try
        {
            return LoadFromDir(ProposalsDir)
                .Where(p => p.Status == ProposalStatus.Pending)
                .OrderByDescending(p => p.CreatedAt)
                .ToList();
        }
        finally { _gate.Release(); }
    }

    public async Task<List<ProposalItem>> LoadAllAsync()
    {
        await _gate.WaitAsync();
        try
        {
            return LoadFromDir(ProposalsDir)
                .Concat(LoadFromDir(ArchiveDir))
                .OrderByDescending(p => p.CreatedAt)
                .ToList();
        }
        finally { _gate.Release(); }
    }

    /// <summary>当日 (CreatedAt が今日) に生成された提案数。アーカイブ済みも含む。</summary>
    public async Task<int> CountCreatedTodayAsync()
    {
        var all = await LoadAllAsync();
        return all.Count(p => p.CreatedAt.Date == DateTime.Today);
    }

    public async Task AddAsync(ProposalItem item)
    {
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(ProposalsDir);

            // Pending 上限: 超過分は古いものから Expired に落としてアーカイブ
            var pending = LoadFromDir(ProposalsDir)
                .Where(p => p.Status == ProposalStatus.Pending)
                .OrderBy(p => p.CreatedAt)
                .ToList();
            while (pending.Count >= MaxPendingCount)
            {
                var oldest = pending[0];
                pending.RemoveAt(0);
                MoveToArchive(oldest, ProposalStatus.Expired);
            }

            WriteItem(Path.Combine(ProposalsDir, $"{item.Id}.json"), item);
            _pendingCount = pending.Count + 1;
        }
        finally { _gate.Release(); }

        Notify(_pendingCount, item);
    }

    public async Task UpdateStatusAsync(ProposalItem item, ProposalStatus status)
    {
        await _gate.WaitAsync();
        try
        {
            MoveToArchive(item, status);
            _pendingCount = LoadFromDir(ProposalsDir).Count(p => p.Status == ProposalStatus.Pending);
        }
        finally { _gate.Release(); }

        Notify(_pendingCount);
    }

    /// <summary>
    /// Pending 提案のターゲットファイルが生成後に手動更新されていたら Expired に落とす。
    /// 起動時とインボックス表示時に実行する。
    /// </summary>
    public async Task<int> ExpireStaleAsync()
    {
        int expired = 0;
        await _gate.WaitAsync();
        try
        {
            foreach (var item in LoadFromDir(ProposalsDir).Where(p => p.Status == ProposalStatus.Pending))
            {
                if (!IsStale(item)) continue;
                MoveToArchive(item, ProposalStatus.Expired);
                expired++;
            }
            _pendingCount = LoadFromDir(ProposalsDir).Count(p => p.Status == ProposalStatus.Pending);
        }
        finally { _gate.Release(); }

        if (expired > 0) Notify(_pendingCount);
        return expired;
    }

    /// <summary>ターゲットファイルが提案生成後に更新されている (適用すると事故る) かどうか。</summary>
    public static bool IsStale(ProposalItem item)
    {
        try
        {
            if (!File.Exists(item.TargetFocusPath)) return true;
            // FAT/コピー等での秒未満の誤差を許容する
            var lastWrite = File.GetLastWriteTime(item.TargetFocusPath);
            return (lastWrite - item.TargetFileLastWriteAt).TotalSeconds > 2;
        }
        catch { return true; }
    }

    /// <summary>指定プロジェクト + workstream の Pending 提案が存在するか。</summary>
    public async Task<bool> HasPendingForAsync(string projectName, string? workstreamId)
    {
        var pending = await LoadPendingAsync();
        return pending.Any(p =>
            string.Equals(p.ProjectName, projectName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.WorkstreamId ?? "", workstreamId ?? "", StringComparison.OrdinalIgnoreCase));
    }

    // -----------------------------------------------------------------------

    private async Task<int> GetPendingCountAsync()
    {
        var pending = await LoadPendingAsync();
        _pendingCount = pending.Count;
        return _pendingCount;
    }

    private static void Notify(int pendingCount, ProposalItem? added = null)
        => WeakReferenceMessenger.Default.Send(new ProposalInboxChangedMessage(pendingCount, added));

    private static List<ProposalItem> LoadFromDir(string dir)
    {
        var items = new List<ProposalItem>();
        if (!Directory.Exists(dir)) return items;

        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var item = JsonSerializer.Deserialize<ProposalItem>(json, JsonOptions);
                if (item != null && !string.IsNullOrWhiteSpace(item.Id))
                    items.Add(item);
            }
            catch (Exception ex)
            {
                // 破損ファイルはスキップしてログのみ
                Debug.WriteLine($"[ProposalInbox] Skipping corrupt file {file}: {ex.Message}");
            }
        }
        return items;
    }

    private void MoveToArchive(ProposalItem item, ProposalStatus status)
    {
        item.Status = status;
        Directory.CreateDirectory(ArchiveDir);
        WriteItem(Path.Combine(ArchiveDir, $"{item.Id}.json"), item);

        var sourcePath = Path.Combine(ProposalsDir, $"{item.Id}.json");
        try
        {
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProposalInbox] Failed to remove {sourcePath}: {ex.Message}");
        }
    }

    private static void WriteItem(string path, ProposalItem item)
    {
        var json = JsonSerializer.Serialize(item, JsonOptions);
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void CleanupOldArchives()
    {
        if (!Directory.Exists(ArchiveDir)) return;
        var cutoff = DateTime.Now.AddDays(-ArchiveRetentionDays);
        foreach (var file in Directory.EnumerateFiles(ArchiveDir, "*.json"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ProposalInbox] Archive cleanup failed for {file}: {ex.Message}");
            }
        }
    }
}
