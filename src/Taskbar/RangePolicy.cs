namespace RyuBar.Taskbar;

/// <summary>
/// 纯策略：在"已有东西"的任务栏上找一块能放下我们这条媒体的空闲区。
/// 不碰任何系统接口，所以可以单独测。
/// </summary>
public static class RangePolicy
{
    public readonly record struct Result(bool Found, int Left, int Width, string Reason)
    {
        public static readonly Result NotFound = new(false, 0, 0, "no-room");
    }

    /// <param name="occupied">已占用区间（客户区坐标，物理像素，Left 递增无要求）</param>
    /// <param name="clientWidth">任务栏客户区宽度</param>
    /// <param name="desiredWidth">我们想要的宽度</param>
    /// <param name="edgePadding">两侧最小留白</param>
    /// <param name="gap">与占用区之间的安全间距</param>
    /// <param name="minimumWidth">低于这个宽度就不显示（宁可不显示，也不要一条挤在缝里的条）</param>
    public static Result Choose(
        IReadOnlyList<OccupiedRange> occupied,
        int clientWidth,
        int desiredWidth,
        int edgePadding,
        int gap,
        int minimumWidth)
    {
        if (clientWidth <= 0)
        {
            return Result.NotFound;
        }

        int usableRight = clientWidth - edgePadding;
        if (usableRight - edgePadding < minimumWidth)
        {
            return new Result(false, 0, 0, "client-too-small");
        }

        List<(int Left, int Right)> free = FreeRanges(occupied, clientWidth, edgePadding, gap);

        if (free.Count == 0)
        {
            return new Result(false, 0, 0, "blocked");
        }

        // 1) 最左边能整条放下的区间 —— 与任务栏"从左往右排"的视觉习惯一致
        foreach ((int left, int right) in free)
        {
            if (right - left >= desiredWidth)
            {
                return new Result(true, left, desiredWidth, "fit");
            }
        }

        // 2) 放不下就退到最宽的那段，按段内宽度压窄
        (int Left, int Right) widest = free[0];
        foreach ((int left, int right) in free)
        {
            if (right - left > widest.Right - widest.Left)
            {
                widest = (left, right);
            }
        }
        int widestWidth = widest.Right - widest.Left;
        if (widestWidth >= minimumWidth)
        {
            return new Result(true, widest.Left, widestWidth, "clamped");
        }

        return new Result(false, 0, 0, "no-room");
    }

    /// <summary>
    /// 任务栏上"一条媒体条最多能用多宽"（物理像素）= 空闲段里最宽那段的宽度。
    /// ★ 与 <see cref="Choose"/> 共用同一套空闲段算法 → 只要不超过它，Choose **必然**能放下 ✓
    ///   用途：歌词"自动适配"模式的文字区上限（TaskbarBarWindow.Place 用它反推）✓
    ///   自己另写一套估算 ✗ 必然出现"上限说能放下、实际被压缩"的不一致 ✗
    /// </summary>
    public static int MaxUsableWidth(
        IReadOnlyList<OccupiedRange> occupied,
        int clientWidth,
        int edgePadding,
        int gap)
    {
        int best = 0;
        foreach ((int left, int right) in FreeRanges(occupied, clientWidth, edgePadding, gap))
        {
            best = Math.Max(best, right - left);
        }

        return best;
    }

    /// <summary>把被占用区间（两侧各留 gap）从可用区里挖掉，剩下的就是能放媒体条的空闲段。</summary>
    private static List<(int Left, int Right)> FreeRanges(
        IReadOnlyList<OccupiedRange> occupied,
        int clientWidth,
        int edgePadding,
        int gap)
    {
        List<(int Left, int Right)> free = new();
        if (clientWidth <= 0)
        {
            return free;
        }

        int usableRight = clientWidth - edgePadding;

        List<(int Left, int Right)> blocked = new();
        foreach (OccupiedRange range in occupied)
        {
            int left = range.Left - gap;
            int right = range.Right + gap;
            if (right <= edgePadding || left >= usableRight)
            {
                continue;
            }
            blocked.Add((Math.Max(0, left), Math.Min(clientWidth, right)));
        }
        blocked.Sort((a, b) => a.Left.CompareTo(b.Left));

        int cursor = edgePadding;
        foreach ((int left, int right) in blocked)
        {
            if (left > cursor)
            {
                free.Add((cursor, left));
            }
            cursor = Math.Max(cursor, right);
        }
        if (cursor < usableRight)
        {
            free.Add((cursor, usableRight));
        }

        return free;
    }
}
