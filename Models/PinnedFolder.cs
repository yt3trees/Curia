using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Curia.Models;

public class PinnedFolder : INotifyPropertyChanged
{
    public string Project { get; set; } = "";
    public string? Workstream { get; set; }
    public string Folder { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string PinnedAt { get; set; } = "";

    public bool FolderExists => System.IO.Directory.Exists(FullPath);

    // "ProjectA / core-feature" or "ProjectA"
    public string ProjectLabel => Workstream is null ? Project : $"{Project} / {Workstream}";

    // リモート (shared\.curia\shared_pins.json) に共有登録済みかどうか。起動時/リフレッシュ時に計算される
    private bool _isShared;

    [JsonIgnore]
    public bool IsShared
    {
        get => _isShared;
        set
        {
            if (_isShared == value) return;
            _isShared = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsShared)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
