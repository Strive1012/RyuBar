using System.Windows.Media;

namespace RyuBar.Media;

/// <summary>
/// 一次媒体快照。所有字段都是"取值那一刻"的状态；
/// 进度用 <see cref="PositionAt"/> 做应用层插值（SMTC 只在播放器上报时才更新位置）。
/// </summary>
public sealed record MediaSnapshot
{
    public static readonly MediaSnapshot Empty = new();

    public bool HasSession { get; init; }

    /// <summary>来源应用标识（如 Chrome、QQ音乐），仅用于显示。</summary>
    public string Source { get; init; } = "";

    public string Title { get; init; } = "";

    public string Artist { get; init; } = "";

    public string Album { get; init; } = "";

    /// <summary>网易云的曲目 id（来自客户端本地 JSON）。有它就能按 id 精确取歌词。</summary>
    public string NeteaseSongId { get; init; } = "";

    public ImageSource? Artwork { get; init; }

    public bool IsPlaying { get; init; }

    /// <summary>
    /// 播放器是否上报了可用的时间轴（时长或真实位置）。
    /// 有些播放器（实测：网易云）整块不上报 —— 位置和时长都返回 0。
    /// 这时**不能**把"正在播放"按时间外推，否则会显示一个自己编出来的假位置。
    /// </summary>
    public bool HasTimeline { get; init; }

    public bool CanPlayPause { get; init; }

    public bool CanPrevious { get; init; }

    public bool CanNext { get; init; }

    /// <summary>播放器是否支持跳转（不支持时进度条只读，不给假反馈）。</summary>
    public bool CanSeek { get; init; }

    public TimeSpan Position { get; init; }

    public TimeSpan Duration { get; init; }

    public double PlaybackRate { get; init; } = 1.0;

    /// <summary>位置采样时刻（用于插值）。</summary>
    public DateTimeOffset PositionSampledAt { get; init; } = DateTimeOffset.MinValue;

    /// <summary>界面上显示的标题：没有媒体时给一句提示。</summary>
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? (HasSession ? "未知曲目" : "没有正在播放的媒体") : Title;

    public string DisplayArtist => string.IsNullOrWhiteSpace(Artist) ? (HasSession ? Source : "打开播放器即自动显示") : Artist;

    /// <summary>把采样到的位置按经过时间外推，并夹在 [0, Duration] 内。</summary>
    public TimeSpan PositionAt(DateTimeOffset now)
    {
        TimeSpan position = Position;
        // 没有可用时间轴就不外推：宁可不显示进度，也不显示编出来的位置
        if (HasTimeline && IsPlaying && PositionSampledAt != DateTimeOffset.MinValue && PlaybackRate > 0)
        {
            double seconds = (now - PositionSampledAt).TotalSeconds;
            if (seconds > 0)
            {
                position += TimeSpan.FromSeconds(seconds * PlaybackRate);
            }
        }

        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }
        if (Duration > TimeSpan.Zero && position > Duration)
        {
            position = Duration;
        }
        return position;
    }

    /// <summary>已播比例（0..1）；时长未知时返回 0。</summary>
    public double ProgressAt(DateTimeOffset now)
    {
        if (!HasTimeline || Duration <= TimeSpan.Zero)
        {
            return 0;
        }
        double ratio = PositionAt(now).TotalSeconds / Duration.TotalSeconds;
        return Math.Clamp(ratio, 0, 1);
    }

    /// <summary>进度条与时间是否可以显示（有可用时间轴且时长已知）。</summary>
    public bool CanShowProgress => HasTimeline && Duration > TimeSpan.Zero;
}
