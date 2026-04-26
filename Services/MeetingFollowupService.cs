using Curia.Models;

namespace Curia.Services;

/// <summary>
/// リンク済み Outlook/ICS 会議の終了を 1 分毎に検知し、トースト通知を出す。
/// ScheduleNotificationService と同型のタイマー構造。
/// </summary>
public class MeetingFollowupService : IDisposable
{
    private readonly OutlookCalendarService _outlookCalendarService;
    private readonly IcsCalendarService _icsCalendarService;
    private readonly TrayService _trayService;
    private readonly ConfigService _configService;

    private System.Threading.Timer? _timer;
    private readonly HashSet<string> _notifiedKeys = [];
    private string _notifiedDate = "";
    private bool _disposed;

    // UI スレッドからダイアログを開くためのコールバック
    public Action<OutlookEvent>? OnMeetingEnded { get; set; }

    public MeetingFollowupService(
        OutlookCalendarService outlookCalendarService,
        IcsCalendarService icsCalendarService,
        TrayService trayService,
        ConfigService configService)
    {
        _outlookCalendarService = outlookCalendarService;
        _icsCalendarService     = icsCalendarService;
        _trayService            = trayService;
        _configService          = configService;
    }

    public void Start()
    {
        var now = DateTime.Now;
        var delay = TimeSpan.FromSeconds(60 - now.Second);
        _timer = new System.Threading.Timer(OnTick, null, delay, TimeSpan.FromMinutes(1));
    }

    private void OnTick(object? state)
    {
        try { _ = CheckAndNotifyAsync(); }
        catch { }
    }

    private async Task CheckAndNotifyAsync()
    {
        var settings = _configService.LoadSettings();
        if (!settings.AiEnabled) return;
        if (!settings.IcsCalendarEnabled && !settings.OutlookCalendarEnabled) return;

        var now     = DateTime.Now;
        var today   = now.Date;
        var todayStr = today.ToString("yyyy-MM-dd");

        if (_notifiedDate != todayStr)
        {
            _notifiedKeys.Clear();
            _notifiedDate = todayStr;
        }

        IReadOnlyList<OutlookEvent> events;
        try
        {
            if (settings.IcsCalendarEnabled && !string.IsNullOrWhiteSpace(settings.IcsCalendarUrl))
            {
                var weekStart = GetMondayOf(today);
                events = await _icsCalendarService.GetEventsForWeekAsync(settings.IcsCalendarUrl, weekStart);

                var excludes = settings.IcsExcludeSubjects
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (excludes.Count > 0)
                    events = events.Where(e => !excludes.Contains(e.Subject)).ToList();
            }
            else if (settings.OutlookCalendarEnabled)
            {
                var weekStart = GetMondayOf(today);
                events = await _outlookCalendarService.GetEventsForWeekAsync(weekStart);
            }
            else
                return;
        }
        catch { return; }

        // 終了直後 (End <= now < End+10min) かつ Asana リンク済みのイベントを検知
        foreach (var ev in events)
        {
            if (!ev.HasLinkedTask) continue;
            if (ev.IsAllDay) continue;
            if (ev.End.Date != today) continue;

            var minutesSinceEnd = (now - ev.End).TotalMinutes;
            if (minutesSinceEnd < 0 || minutesSinceEnd > 10) continue;

            var key = $"{ev.EntryId}:{todayStr}";
            if (!_notifiedKeys.Add(key)) continue;

            _trayService.ShowBalloonTip(
                ev.Subject,
                "Meeting ended. Log meeting notes?",
                timeoutMs: 10000);

            OnMeetingEnded?.Invoke(ev);
        }
    }

    private static DateTime GetMondayOf(DateTime date)
    {
        int diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        return date.Date.AddDays(-diff);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
        _timer = null;
    }
}
