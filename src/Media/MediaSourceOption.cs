namespace RyuBar.Media;

/// <summary>一个可切换的声音来源（= 一个 SMTC 会话）。</summary>
/// <param name="Key">稳定标识：会话的来源应用 ID（AUMID），用它做手动选择的键。</param>
/// <param name="DisplayName">给用户看的名字（如「网易云音乐」）。</param>
/// <param name="IsSelected">是不是当前正在显示的那一个。</param>
/// <param name="IsPlaying">是否正在播放（用于切换时的排序与提示）。</param>
public sealed record MediaSourceOption(string Key, string DisplayName, bool IsSelected, bool IsPlaying);
