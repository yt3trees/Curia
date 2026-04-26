using System.Globalization;
using System.Runtime.InteropServices;
using Curia.Models;

namespace Curia.Services;

/// <summary>
/// COM Late Binding で Outlook カレンダーから予定を読み取るサービス。
/// Outlook が未インストールの場合は空リストを返す (例外をスローしない)。
/// NuGet パッケージ不要。
/// </summary>
public class OutlookCalendarService
{
    // oleaut32.dll の GetActiveObject (.NET 9 では Marshal.GetActiveObject が削除されたため P/Invoke で代替)
    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(
        ref Guid rclsid,
        IntPtr pvReserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

    private static object? TryGetActiveOutlook(Type outlookType)
    {
        try
        {
            var clsid = outlookType.GUID;
            int hr = GetActiveObject(ref clsid, IntPtr.Zero, out object ppunk);
            return hr == 0 ? ppunk : null;
        }
        catch
        {
            return null;
        }
    }
    // olAppointment クラス定数 (Outlook 側の OlObjectClass 列挙値)
    private const int OlAppointment = 26;

    // olFolderCalendar 定数
    private const int OlFolderCalendar = 9;

    /// <summary>Outlook が利用可能かどうかを確認する (設定 UI 用)。</summary>
    public bool IsOutlookAvailable()
    {
        try
        {
            return Type.GetTypeFromProgID("Outlook.Application") != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 指定週 (weekStart の月曜 0:00 〜 +7日) の Outlook 予定を返す。
    /// Outlook が使用不可の場合は空リストを返す。
    /// </summary>
    public Task<IReadOnlyList<OutlookEvent>> GetEventsForWeekAsync(DateTime weekStart)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<OutlookEvent>>();

        // COM は STA スレッドで呼び出す必要がある
        var thread = new Thread(() =>
        {
            try
            {
                var events = FetchEvents(weekStart);
                tcs.SetResult(events);
            }
            catch (Exception ex)
            {
                // 取得失敗時は空リストを返す (例外を伝搬しない)
                System.Diagnostics.Debug.WriteLine($"[OutlookCalendarService] {ex.Message}");
                tcs.SetResult([]);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        return tcs.Task;
    }

    private static IReadOnlyList<OutlookEvent> FetchEvents(DateTime weekStart)
    {
        var outlookType = Type.GetTypeFromProgID("Outlook.Application");
        if (outlookType == null) return [];

        dynamic? app = null;
        dynamic? ns = null;
        dynamic? calendar = null;
        dynamic? items = null;
        dynamic? restricted = null;

        try
        {
            // 既存の Outlook プロセスを優先して取得
            app = TryGetActiveOutlook(outlookType);
            if (app == null)
                app = Activator.CreateInstance(outlookType)!;

            ns = app.GetNamespace("MAPI");
            calendar = ns.GetDefaultFolder(OlFolderCalendar);
            items = calendar.Items;

            var weekEnd = weekStart.AddDays(7);
            string filter = BuildFilter(weekStart, weekEnd);
            restricted = items.Restrict(filter);

            var events = new List<OutlookEvent>();
            int count = restricted.Count;

            for (int i = 1; i <= count; i++) // Outlook コレクションは 1 始まり
            {
                dynamic? item = null;
                try
                {
                    item = restricted[i];

                    // AppointmentItem のみ処理 (他のアイテム種別を除外)
                    if ((int)item.Class != OlAppointment)
                        continue;

                    bool isAllDay = (bool)item.AllDayEvent;
                    DateTime start = (DateTime)item.Start;
                    DateTime end   = (DateTime)item.End;

                    // 週の範囲外は除外
                    if (start >= weekEnd || end <= weekStart) continue;

                    string entryId  = (string)item.EntryID;
                    string subject  = (string)item.Subject ?? "";
                    string location = "";
                    try { location = (string)item.Location ?? ""; } catch { }

                    string? body = null;
                    try
                    {
                        var raw = (string)item.Body;
                        if (!string.IsNullOrWhiteSpace(raw))
                            body = raw.Length > 4000 ? raw[..4000] : raw;
                    }
                    catch { }

                    var ev = new OutlookEvent
                    {
                        EntryId      = entryId,
                        Subject      = subject,
                        Start        = start,
                        End          = end,
                        IsAllDay     = isAllDay,
                        Location     = string.IsNullOrWhiteSpace(location) ? null : location,
                        CalendarName = "Outlook",
                        Body         = body,
                    };
                    var link = CalendarLinkService.TryExtractAsanaLink(body);
                    if (link.HasValue) ev.LinkedAsanaGid = link.Value.Gid;

                    events.Add(ev);
                }
                catch { /* アイテム個別のエラーは無視 */ }
                finally
                {
                    if (item != null) Marshal.ReleaseComObject(item);
                }
            }

            return events.AsReadOnly();
        }
        finally
        {
            if (restricted != null) try { Marshal.ReleaseComObject(restricted); } catch { }
            if (items     != null) try { Marshal.ReleaseComObject(items);      } catch { }
            if (calendar  != null) try { Marshal.ReleaseComObject(calendar);   } catch { }
            if (ns        != null) try { Marshal.ReleaseComObject(ns);         } catch { }
            // app は意図的に解放しない (Outlook を強制終了させないため)
        }
    }

    /// <summary>Outlook の Items.Restrict フィルタ文字列を生成する。</summary>
    private static string BuildFilter(DateTime weekStart, DateTime weekEnd)
    {
        // MM/dd/yyyy HH:mm 形式 (Outlook JET クエリで広く受け入れられる)
        string start = weekStart.ToString("MM/dd/yyyy HH:mm", CultureInfo.InvariantCulture);
        string end   = weekEnd.ToString("MM/dd/yyyy HH:mm",   CultureInfo.InvariantCulture);
        return $"[Start] < '{end}' AND [End] > '{start}'";
    }
}
