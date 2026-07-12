using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Curia.Models;

/// <summary>
/// ステータスバーの表示内容を更新するためのメッセージ。
/// </summary>
public class StatusUpdateMessage(string project, string file, string encoding, bool isDirty)
{
    public string Project { get; } = project;
    public string File { get; } = file;
    public string Encoding { get; } = encoding;
    public bool IsDirty { get; } = isDirty;
}

/// <summary>
/// AI 機能の有効/無効が変わったときのメッセージ。
/// </summary>
public class AiEnabledChangedMessage(bool enabled)
{
    public bool Enabled { get; } = enabled;
}

public class AgentCompatibilityChangedMessage(bool isCompatible)
{
    public bool IsCompatible { get; } = isCompatible;
}

/// <summary>
/// Editor / Wiki のフォントサイズが変わったときのメッセージ。
/// </summary>
public class FontSizeChangedMessage(int editorFontSize, int markdownRenderFontSize)
{
    public int EditorFontSize { get; } = editorFontSize;
    public int MarkdownRenderFontSize { get; } = markdownRenderFontSize;
}

/// <summary>
/// Silence Alert 機能の有効/無効が変わったときのメッセージ。
/// </summary>
public class SilenceAlertEnabledChangedMessage(bool enabled)
{
    public bool Enabled { get; } = enabled;
}

/// <summary>
/// Editor / Wiki の文字色が変わったときのメッセージ。
/// </summary>
public class TextColorChangedMessage(string editorTextColor, string markdownRenderTextColor)
{
    public string EditorTextColor { get; } = editorTextColor;
    public string MarkdownRenderTextColor { get; } = markdownRenderTextColor;
}
