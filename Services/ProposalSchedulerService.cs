using System.Diagnostics;
using System.IO;
using System.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Curia.Models;

namespace Curia.Services;

/// <summary>
/// バックグラウンドで Focus 更新提案を生成するスケジューラ。
/// StandupGeneratorService と同じ Timer パターン。対象は
/// 「行動シグナルが current_focus.md より新しい」プロジェクトのみで、
/// 1日あたりの生成上限と同一プロジェクトの Pending 重複を制御する。
/// </summary>
public class ProposalSchedulerService : IDisposable
{
    private readonly ConfigService _configService;
    private readonly ProjectDiscoveryService _discoveryService;
    private readonly FocusSignalCollectorService _signalCollector;
    private readonly FocusUpdateService _focusUpdateService;
    private readonly ProposalInboxService _inbox;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private System.Threading.Timer? _timer;

    public ProposalSchedulerService(
        ConfigService configService,
        ProjectDiscoveryService discoveryService,
        FocusSignalCollectorService signalCollector,
        FocusUpdateService focusUpdateService,
        ProposalInboxService inbox)
    {
        _configService = configService;
        _discoveryService = discoveryService;
        _signalCollector = signalCollector;
        _focusUpdateService = focusUpdateService;
        _inbox = inbox;

        // 設定変更 / AI トグル変更でスケジューラを再起動 (HotkeyService の再登録パターン)
        WeakReferenceMessenger.Default.Register<ProposalInboxSettingsChangedMessage>(this,
            (_, _) => RestartScheduler());
        WeakReferenceMessenger.Default.Register<AiEnabledChangedMessage>(this,
            (_, _) => RestartScheduler());
    }

    public void StartScheduler()
    {
        var settings = _configService.LoadSettings();
        if (!settings.AiEnabled || !settings.ProposalInboxEnabled) return;

        var intervalHours = Math.Max(1, settings.ProposalScanIntervalHours);
        // 起動直後は Discovery のウォームアップを待つため 5 分後に初回スキャン
        _timer = new System.Threading.Timer(_ => _ = ScanAndGenerateAsync(),
                                            null,
                                            TimeSpan.FromMinutes(5),
                                            TimeSpan.FromHours(intervalHours));
        Log($"Scheduler started (interval {intervalHours}h, first scan in 5 min)");
    }

    public void RestartScheduler()
    {
        _timer?.Dispose();
        _timer = null;
        StartScheduler();
    }

    public async Task ScanAndGenerateAsync()
    {
        // 多重実行防止: スキャン中に次の Timer 発火が来たらスキップ
        if (!await _scanGate.WaitAsync(0)) return;
        try
        {
            // 深夜帯 (0:00-5:59) は生成しない (Standup / SilenceAlert と同じ扱い)
            if (DateTime.Now.Hour < 6)
            {
                Log("Scan skipped: quiet hours (0:00-5:59)");
                return;
            }

            var ct = _cts.Token;
            var settings = _configService.LoadSettings();
            if (!settings.AiEnabled || !settings.ProposalInboxEnabled)
            {
                Log("Scan skipped: AI or Proposal Inbox disabled");
                return;
            }

            var maxPerDay = Math.Max(1, settings.ProposalMaxPerDay);
            var createdToday = await _inbox.CountCreatedTodayAsync();
            if (createdToday >= maxPerDay)
            {
                Log($"Scan skipped: daily cap reached ({createdToday}/{maxPerDay})");
                return;
            }

            // 手動編集で陳腐化した Pending を先に整理しておく
            await _inbox.ExpireStaleAsync();

            var allProjects = await _discoveryService.GetProjectInfoListAsync(force: false);
            var hiddenKeys = _configService.LoadHiddenProjects();
            var pending = await _inbox.LoadPendingAsync();

            Log($"Scan started: {allProjects.Count} projects, created today {createdToday}/{maxPerDay}");
            int skippedHidden = 0, skippedNoFocus = 0, skippedNoSignals = 0, skippedPending = 0, generated = 0;

            foreach (var project in allProjects)
            {
                ct.ThrowIfCancellationRequested();
                if (createdToday >= maxPerDay)
                {
                    Log($"Daily cap reached ({createdToday}/{maxPerDay}), stopping scan");
                    break;
                }

                try
                {
                    if (hiddenKeys.Contains(project.HiddenKey))
                    {
                        skippedHidden++;
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(project.FocusFile) || !File.Exists(project.FocusFile))
                    {
                        skippedNoFocus++;
                        continue;
                    }

                    // 同一プロジェクト (general) の Pending が残っている間は再生成しない
                    if (pending.Any(p =>
                            string.Equals(p.ProjectName, project.Name, StringComparison.OrdinalIgnoreCase) &&
                            string.IsNullOrEmpty(p.WorkstreamId)))
                    {
                        skippedPending++;
                        continue;
                    }

                    var signals = await _signalCollector.CollectAsync(project, ct);
                    if (signals.IsEmpty)
                    {
                        skippedNoSignals++;
                        continue;
                    }

                    // focus が実活動より古い場合のみ対象
                    var latestSignal = GetLatestSignalTime(signals);
                    var focusLastWrite = File.GetLastWriteTime(project.FocusFile);
                    if (latestSignal is null)
                    {
                        // 未コミットファイルのみ = 日付を持たないシグナルなので鮮度判定できない
                        Log($"  {project.Name}: only undated signals (uncommitted files), skip");
                        continue;
                    }
                    if (latestSignal.Value <= focusLastWrite)
                    {
                        Log($"  {project.Name}: latest signal {latestSignal:yyyy-MM-dd HH:mm} <= focus {focusLastWrite:yyyy-MM-dd HH:mm}, skip");
                        continue;
                    }

                    Log($"  {project.Name}: latest signal {latestSignal:yyyy-MM-dd HH:mm} > focus {focusLastWrite:yyyy-MM-dd HH:mm}, generating...");
                    var result = await _focusUpdateService.GenerateProposalAsync(
                        project, workstreamId: null, ct, signals: signals);

                    var item = ProposalItem.FromFocusResult(result, project.Name);
                    await _inbox.AddAsync(item);
                    createdToday++;
                    generated++;
                    Log($"  {project.Name}: proposal generated ({item.Id})");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // 1 プロジェクトの失敗で全体を止めない
                    Log($"  {project.Name}: generation FAILED - {ex.Message}");
                }
            }

            Log($"Scan finished: generated {generated}, skipped (hidden {skippedHidden} / no focus {skippedNoFocus} / no signals {skippedNoSignals} / pending exists {skippedPending})");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log($"Scan FAILED: {ex.Message}");
            Debug.WriteLine($"[ProposalScheduler] ScanAndGenerateAsync failed: {ex}");
        }
        finally
        {
            _scanGate.Release();
        }
    }

    // -----------------------------------------------------------------------
    // スキャンログ: [config_dir]\proposals\scan_log.txt
    // 動作の可視化用。肥大化しないよう上限行数を超えたら古い行を切り捨てる。
    // -----------------------------------------------------------------------
    private const int LogMaxLines = 500;
    private const int LogKeepLines = 250;
    private readonly object _logLock = new();

    private string LogPath => Path.Combine(_configService.ConfigDir, "proposals", "scan_log.txt");

    private void Log(string message)
    {
        try
        {
            lock (_logLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");

                var lines = File.ReadAllLines(LogPath);
                if (lines.Length > LogMaxLines)
                    File.WriteAllLines(LogPath, lines[^LogKeepLines..]);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProposalScheduler] Log write failed: {ex.Message}");
        }
        Debug.WriteLine($"[ProposalScheduler] {message}");
    }

    /// <summary>収集済みシグナルのうち最も新しい活動日時を返す。</summary>
    private static DateTime? GetLatestSignalTime(FocusActivitySignals signals)
    {
        DateTime? latest = null;

        void Update(DateTime candidate)
        {
            if (latest is null || candidate > latest.Value) latest = candidate;
        }

        foreach (var f in signals.PinnedFolderFiles) Update(f.ModifiedAt);
        foreach (var w in signals.RecentWorkFolders) Update(w.Date);
        foreach (var c in signals.RecentCommits)
            if (DateTime.TryParse(c.Date, out var d)) Update(d);
        foreach (var c in signals.Captures) Update(c.Timestamp);

        return latest;
    }

    public void Dispose()
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
        _cts.Cancel();
        _cts.Dispose();
        _timer?.Dispose();
        _timer = null;
    }
}
