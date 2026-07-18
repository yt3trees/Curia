namespace Curia.Models;

/// <summary>
/// プロジェクトの shared フォルダ (クラウド同期対象) に置かれる共有ピン 1 件分。
/// 保存先: {projectPath}\shared\.curia\shared_pins.json
/// パスは PC ごとに異なるため、shared 配下は相対パスで持ち、別 PC 側で解決する。
/// </summary>
public class SharedPinEntry
{
    public string? Workstream { get; set; }
    public string Folder { get; set; } = "";

    // shared フォルダからの相対パス (例: "_work\core-feature\202603\20260321_auth_impl")
    // shared 配下でないピンの場合は null
    public string? RelativePath { get; set; }

    // shared 配下でないピンのフォールバック (共有元 PC の絶対パス)
    public string? AbsolutePath { get; set; }

    public string SharedAt { get; set; } = "";
    public string SharedBy { get; set; } = "";
}

/// <summary>
/// 別 PC で共有されたピンのうち、ローカル未登録の取り込み候補。
/// </summary>
public class RemotePinCandidate
{
    public PinnedFolder Pin { get; set; } = new();
    public string SharedBy { get; set; } = "";
    public string SharedAt { get; set; } = "";

    public string DisplayLabel
    {
        get
        {
            var missing = Pin.FolderExists ? "" : "  [not synced]";
            var by = string.IsNullOrWhiteSpace(SharedBy) ? "" : $"  (from {SharedBy})";
            return $"{Pin.ProjectLabel}  -  {Pin.Folder}{by}{missing}";
        }
    }
}
