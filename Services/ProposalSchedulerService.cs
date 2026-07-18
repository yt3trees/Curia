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
            if (DateTime.Now.Hour < 6) return;

            var ct = _cts.Token;
            var settings = _configService.LoadSettings();
            if (!settings.AiEnabled || !settings.ProposalInboxEnabled) return;

            var maxPerDay = Math.Max(1, settings.ProposalMaxPerDay);
            var createdToday = await _inbox.CountCreatedTodayAsync();
            if (createdToday >= maxPerDay) return;

            // 手動編集で陳腐化した Pending を先に整理しておく
            await _inbox.ExpireStaleAsync();

            var allProjects = await _discoveryService.GetProjectInfoListAsync(force: false);
            var hiddenKeys = _configService.LoadHiddenProjects();
            var pending = await _inbox.LoadPendingAsync();

            foreach (var project in allProjects)
            {
                ct.ThrowIfCancellationRequested();
                if (createdToday >= maxPerDay) break;

                try
                {
                    if (hiddenKeys.Contains(project.HiddenKey)) continue;
                    if (string.IsNullOrWhiteSpace(project.FocusFile) || !File.Exists(project.FocusFile)) continue;

                    // 同一プロジェクト (general) の Pending が残っている間は再生成しない
                    if (pending.Any(p =>
                            string.Equals(p.ProjectName, project.Name, StringComparison.OrdinalIgnoreCase) &&
                            string.IsNullOrEmpty(p.WorkstreamId)))
                        continue;

                    var signals = await _signalCollector.CollectAsync(project, ct);
                    if (signals.IsEmpty) continue;

                    // focus が実活動より古い場合のみ対象
                    var latestSignal = GetLatestSignalTime(signals);
                    if (latestSignal is null) continue;
                    if (latestSignal.Value <= File.GetLastWriteTime(project.FocusFile)) continue;

                    var result = await _focusUpdateService.GenerateProposalAsync(
                        project, workstreamId: null, ct, signals: signals);

                    // 生成中に手動編集されていないかを最終確認してから登録
                    var item = ProposalItem.FromFocusResult(result, project.Name);
                    await _inbox.AddAsync(item);
                    createdToday++;
                    Debug.WriteLine($"[ProposalScheduler] Generated proposal for {project.Name}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // 1 プロジェクトの失敗で全体を止めない
                    Debug.WriteLine($"[ProposalScheduler] Generation failed for {project.Name}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProposalScheduler] ScanAndGenerateAsync failed: {ex}");
        }
        finally
        {
            _scanGate.Release();
        }
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
