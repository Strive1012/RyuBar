namespace RyuBar.Lyrics;

/// <summary>一个音节（逐字时间轴的一小段）。只有播放器/歌词源真给了逐字数据时才存在。</summary>
public sealed record LyricSyllable(string Text, int StartMs, int EndMs);

/// <summary>
/// 一行歌词（本项目自己的最小模型，不让界面依赖歌词库的类型）。
/// </summary>
public sealed record LyricLine(string Text, int StartMs, int EndMs, IReadOnlyList<LyricSyllable>? Syllables = null)
{
    /// <summary>是否带真实的逐字时间轴（行级歌词绝不猜）。</summary>
    public bool HasSyllables => Syllables is { Count: > 0 };

    public bool Contains(int positionMs) => positionMs >= StartMs && positionMs < EndMs;
}

/// <summary>一首歌的歌词（已经整理成按时间排序的行）。</summary>
public sealed record LyricTrack(
    string Title,
    string Artist,
    IReadOnlyList<LyricLine> Lines,
    int OffsetMs = 0)
{
    public static readonly LyricTrack Empty = new("", "", Array.Empty<LyricLine>());

    public bool HasLines => Lines.Count > 0;

    /// <summary>
    /// 按播放位置找当前行；返回它的下标，找不到返回 -1。
    /// 用二分而不是线性扫描：位置每 250ms 问一次，行数可能上千。
    /// </summary>
    public int IndexAt(int positionMs)
    {
        int position = positionMs - OffsetMs;
        int low = 0;
        int high = Lines.Count - 1;
        int found = -1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            LyricLine line = Lines[middle];
            if (position < line.StartMs)
            {
                high = middle - 1;
            }
            else if (position >= line.EndMs)
            {
                low = middle + 1;
            }
            else
            {
                found = middle;
                break;
            }
        }

        // 落在行间空隙里：退回到"最后一条已经开始的行"
        if (found < 0)
        {
            for (int index = Math.Min(low, Lines.Count - 1); index >= 0; index--)
            {
                if (position >= Lines[index].StartMs)
                {
                    return index;
                }
            }
        }
        return found;
    }
}
