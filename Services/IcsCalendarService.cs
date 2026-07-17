using System.Net.Http;
using System.Text;
using Curia.Models;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

namespace Curia.Services;

/// <summary>
/// ICS (iCalendar) URL からカレンダーイベントを取得するサービス。
/// 新しい Outlook / Google Calendar など COM 非対応環境向けの代替手段。
/// </summary>
public class IcsCalendarService
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    // ICS コンテンツキャッシュ (URL 単位、TTL 10分)
    private string? _cachedUrl;
    private string? _cachedContent;
    private DateTime _cachedAt = DateTime.MinValue;
    private const int CacheTtlSeconds = 600;

    /// <summary>
    /// 指定 URL の ICS を取得し、weekStart の週に重なるイベントを返す。
    /// 直近 10 分以内に同じ URL を取得済みの場合はキャッシュを利用する。
    /// 失敗時は空リストを返す (例外をスローしない)。
    /// </summary>
    public async Task<IReadOnlyList<OutlookEvent>> GetEventsForWeekAsync(
        string icsUrl, DateTime weekStart)
    {
        try
        {
            string icsContent;
            if (_cachedContent != null
                && _cachedUrl == icsUrl
                && (DateTime.Now - _cachedAt).TotalSeconds < CacheTtlSeconds)
            {
                icsContent = _cachedContent;
            }
            else
            {
                var bytes = await _http.GetByteArrayAsync(icsUrl);
                icsContent = DecodeIcsBytes(bytes);
                _cachedUrl     = icsUrl;
                _cachedContent = icsContent;
                _cachedAt      = DateTime.Now;
            }
            return ParseWeekEvents(icsContent, weekStart);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[IcsCalendarService] {ex.Message}");
            throw; // SettingsViewModel の TestIcs でエラー表示するため伝搬
        }
    }

    /// <summary>キャッシュを強制破棄する (設定変更時などに呼ぶ)。</summary>
    public void InvalidateCache()
    {
        _cachedUrl     = null;
        _cachedContent = null;
        _cachedAt      = DateTime.MinValue;
    }

    /// <summary>
    /// BOM を優先して実際のバイト列からエンコーディングを判定する。
    /// HttpClient の charset 自動判定 (レスポンスヘッダー依存) は実際の内容と食い違うことがあり、
    /// UTF-16 で配信された ICS を UTF-8 として読むと文字化けして Ical.Net のパースが失敗するため。
    /// </summary>
    private static string DecodeIcsBytes(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        try
        {
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return SanitizeInvalidEscapes(StripInvalidControlChars(UnfoldLines(strictUtf8.GetString(bytes))));
        }
        catch (DecoderFallbackException)
        {
            // BOM なしで UTF-8 として不正な場合、Exchange 系配信で見られる UTF-16 なしBOM を疑って再試行
            return SanitizeInvalidEscapes(StripInvalidControlChars(UnfoldLines(Encoding.Unicode.GetString(bytes))));
        }
    }

    /// <summary>
    /// RFC 5545 の折り返し (CRLF + 単一の SPACE/TAB) をあらかじめ解除する。
    /// エスケープ (\n など) の途中で折り返されるケースがあるため、SanitizeInvalidEscapes より前に行う必要がある。
    /// </summary>
    private static string UnfoldLines(string text)
        => System.Text.RegularExpressions.Regex.Replace(text, "\r\n[ \t]|\n[ \t]", "");

    /// <summary>
    /// RFC 5545 は TAB/CR/LF 以外の C0 制御文字 (0x00-0x1F の一部) を許可しない。
    /// 実際の Exchange/Teams 配信では、元データの破損によりこうした生の制御文字が
    /// DESCRIPTION などの値に混入することがあり、Ical.Net のパースが失敗する原因になる。
    /// </summary>
    private static string StripInvalidControlChars(string text)
        => System.Text.RegularExpressions.Regex.Replace(text, "[\\x00-\\x08\\x0B\\x0C\\x0E-\\x1F]", "");

    /// <summary>
    /// RFC 5545 上有効なエスケープは \\ \; \, \N \n のみ。
    /// Microsoft/Teams 系の ICS 配信では、元データの破損 (絵文字や特殊記号が U+FFFD 等に潰れる)
    /// によって "\(" のような不正なエスケープが混入することがあり、Ical.Net はそのプロパティ行の
    /// パースに失敗して該当週の全イベント取得が失敗してしまう。孤立したバックスラッシュを二重化して
    /// リテラルなバックスラッシュとして扱われるようにし、他の正常なイベントの取得を壊さないようにする。
    /// 折り返し解除後の文字列に対して呼び出すこと。
    /// </summary>
    private static string SanitizeInvalidEscapes(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                var next = text[i + 1];
                if (next is '\\' or ';' or ',' or 'N' or 'n')
                {
                    sb.Append(c).Append(next);
                    i++;
                    continue;
                }
                sb.Append('\\').Append('\\');
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static IReadOnlyList<OutlookEvent> ParseWeekEvents(
        string icsContent, DateTime weekStart)
    {
        var weekEnd  = weekStart.AddDays(7);
        var calendar = Calendar.Load(icsContent);
        var events   = new List<OutlookEvent>();

        foreach (var vEvent in calendar.Events)
        {
            if (vEvent.DtStart == null) continue;

            if (vEvent.RecurrenceRules == null || vEvent.RecurrenceRules.Count == 0)
            {
                // 繰り返しなし: ローカル時刻に変換して週範囲チェック
                AddIfOverlaps(vEvent, ToLocal(vEvent.DtStart),
                    vEvent.DtEnd != null ? ToLocal(vEvent.DtEnd) : null,
                    weekStart, weekEnd, events);
            }
            else
            {
                // 繰り返しあり: DTSTART のローカル日から開始して各週の出現を展開
                ExpandRecurring(vEvent, weekStart, weekEnd, events);
            }
        }

        return events.AsReadOnly();
    }

    private static void AddIfOverlaps(
        CalendarEvent vEvent,
        DateTime start, DateTime? endRaw,
        DateTime weekStart, DateTime weekEnd,
        List<OutlookEvent> result)
    {
        bool isAllDay = !vEvent.DtStart.HasTime;
        // 終日イベントは End が翌日 00:00 なのでそのまま使う
        var end = endRaw ?? (isAllDay ? start.AddDays(1) : start.AddHours(1));

        // 週と重なるか
        if (start >= weekEnd || end <= weekStart) return;

        AddEvent(result, vEvent, start, end, isAllDay);
    }

    private static void ExpandRecurring(
        CalendarEvent vEvent,
        DateTime weekStart, DateTime weekEnd,
        List<OutlookEvent> result)
    {
        // CalDateTime を TZID なし浮動時刻で生成して GetOccurrences を呼ぶ
        // TZID 付きイベントとの比較は ToLocal 後にフィルタする
        var calRangeStart = new CalDateTime(weekStart.Year, weekStart.Month, weekStart.Day, 0, 0, 0);

        IEnumerable<Occurrence> occs;
        try
        {
            occs = vEvent.GetOccurrences(calRangeStart);
        }
        catch
        {
            return;
        }

        bool isAllDay = !vEvent.DtStart.HasTime;

        foreach (var occ in occs)
        {
            if (occ.Period?.StartTime == null) continue;

            var start = ToLocal(occ.Period.StartTime);
            if (start >= weekEnd) break; // 以降は不要 (昇順前提)
            if (start < weekStart) continue;

            var end = occ.Period.EndTime != null
                ? ToLocal(occ.Period.EndTime)
                : (isAllDay ? start.AddDays(1) : start.AddHours(1));

            AddEvent(result, vEvent, start, end, isAllDay);
        }
    }

    private static void AddEvent(
        List<OutlookEvent> result,
        CalendarEvent vEvent,
        DateTime start, DateTime end, bool isAllDay)
    {
        string? body = vEvent.Description;
        if (body?.Length > 4000) body = body[..4000];

        var ev = new OutlookEvent
        {
            EntryId      = $"{vEvent.Uid}_{start:yyyyMMddHHmm}",
            Subject      = vEvent.Summary ?? "(No title)",
            Start        = start,
            End          = end,
            IsAllDay     = isAllDay,
            Location     = string.IsNullOrWhiteSpace(vEvent.Location) ? null : vEvent.Location,
            CalendarName = "ICS",
            Body         = string.IsNullOrWhiteSpace(body) ? null : body,
        };
        var link = CalendarLinkService.TryExtractAsanaLink(body);
        if (link.HasValue) ev.LinkedAsanaGid = link.Value.Gid;
        result.Add(ev);
    }

    private static DateTime ToLocal(CalDateTime icalDt)
    {
        var dt = icalDt.Value;
        if (icalDt.IsUtc)
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToLocalTime();

        // TZID 付きの場合は TimeZoneInfo 経由で変換
        if (!string.IsNullOrEmpty(icalDt.TzId))
        {
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(icalDt.TzId)
                      ?? TimeZoneInfo.FindSystemTimeZoneById(IanaToWindows(icalDt.TzId));
                return TimeZoneInfo.ConvertTimeToUtc(
                    DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), tz).ToLocalTime();
            }
            catch { /* 変換失敗時はローカルとして扱う */ }
        }

        return DateTime.SpecifyKind(dt, DateTimeKind.Local);
    }

    /// <summary>代表的な IANA タイムゾーン名を Windows タイムゾーン ID に変換する。</summary>
    private static string IanaToWindows(string ianaId) => ianaId switch
    {
        "Asia/Tokyo"       => "Tokyo Standard Time",
        "America/New_York" => "Eastern Standard Time",
        "America/Chicago"  => "Central Standard Time",
        "America/Denver"   => "Mountain Standard Time",
        "America/Los_Angeles" => "Pacific Standard Time",
        "Europe/London"    => "GMT Standard Time",
        "Europe/Paris"     => "W. Europe Standard Time",
        "UTC"              => "UTC",
        _                  => ianaId,
    };
}
