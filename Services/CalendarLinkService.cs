using System.Text.RegularExpressions;

namespace Curia.Services;

/// <summary>
/// カレンダーイベントの本文から Asana リンクを抽出するヘルパー。
/// ICS description / Outlook body に共通で適用する。
/// </summary>
public static class CalendarLinkService
{
    // https://app.asana.com/0/<project>/<task-gid>/... 形式
    private static readonly Regex AsanaUrlRx =
        new(@"https://app\.asana\.com/[^\s)\]""]+", RegexOptions.Compiled);

    private static readonly Regex AsanaGidRx =
        new(@"/(\d{10,})(?:[/?]|$)", RegexOptions.Compiled);

    /// <summary>
    /// body テキストから最初に見つかった Asana タスク URL と GID を返す。
    /// 見つからなければ null を返す。
    /// </summary>
    public static (string Url, string Gid)? TryExtractAsanaLink(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        var urlMatch = AsanaUrlRx.Match(body);
        if (!urlMatch.Success) return null;

        var url = urlMatch.Value.TrimEnd('.');
        var urlBase = url.Split('?')[0].TrimEnd('/');
        var gidMatch = AsanaGidRx.Match(urlBase);
        if (!gidMatch.Success) return null;

        return (url, gidMatch.Groups[1].Value);
    }
}
