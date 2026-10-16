using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RyuBar.Diagnostics;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace RyuBar.Media;

/// <summary>
/// 系统媒体会话（SMTC）通道。自己封装，不依赖第三方包装库。
///
/// 设计要点：
///  1. WinRT 的事件在任意线程触发 → 一律切回 UI 线程再发布；
///  2. 快照里的位置是"采样时刻的位置"，界面用自己的时钟外推（<see cref="MediaSnapshot.PositionAt"/>）；
///  3. 封面按内容解码一次并 Freeze，之后可以跨线程使用；
///  4. 播放器不上报的曲目（没有会话）也是正常状态，用 <see cref="MediaSnapshot.Empty"/> 表示。
/// </summary>
public sealed class MediaSessionWatcher : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly object _gate = new();

    /// <summary>网易云这类完全不上报时间轴的播放器：位置读内存、时长读客户端本地 JSON。</summary>
    private readonly Readers.NetEaseMemoryReader _neteaseMemory;
    private readonly Readers.NetEaseMetadataIndex _neteaseMetadata = new();

    /// <summary>歌词（按当前曲目自动拉取，缓存 64 条 / 4 MiB）。</summary>
    public Lyrics.LyricsService Lyrics { get; } = new();

    /// <summary>当前会话是否真的能跳转（null = 还没试过）。失败的播放器就让它退回只读。</summary>
    private bool? _seekWorks;

    /// <summary>手动选定的来源键（AUMID）。非空时优先于"自动跟随正在播放的会话"。</summary>
    private string? _manualSourceKey;

    /// <summary>
    /// 位置轮询节拍。参照实现是 100 ms（<c>NetEaseMediaProvider.PollInterval</c> /
    /// <c>KuGouMediaProvider.PollInterval</c>），这里对齐：内存来源只读 8 字节，10 次/秒毫无压力；
    /// SMTC 来源只读时间轴（不读媒体属性），也便宜。
    /// </summary>
    private const long PokeIntervalMilliseconds = 100;

    /// <summary>完整刷新（含媒体属性/封面）的兜底间隔。参照实现是一个 1 秒"看门狗"（Armed 状态），这里对齐。</summary>
    private const long FullRefreshSafetyMilliseconds = 1000;

    /// <summary>重挑"当前会话"的间隔（与参照实现一致：200 ms）。</summary>
    private const long SessionRecheckMilliseconds = 200;

    private long _lastPokeAt;
    private long _lastSessionCheckAt;
    private long _lastFullRefreshAt;

    /// <summary>当前位置是否来自内存读取器（那种情况下播放器不会推事件，必须我们自己轮询）。</summary>
    private bool _memoryBackedTimeline;

    private string _lastTitle = "";

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private MediaSnapshot _snapshot = MediaSnapshot.Empty;
    private string _artworkKey = "";
    private bool _disposed;

    public MediaSessionWatcher(Dispatcher dispatcher, Configuration.SettingsStore? settings = null)
    {
        _dispatcher = dispatcher;
        _neteaseMemory = new Readers.NetEaseMemoryReader(settings);
    }

    /// <summary>快照有变化（在 UI 线程上触发）。</summary>
    public event Action<MediaSnapshot>? Changed;

    public MediaSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public void Start()
    {
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            GlobalSystemMediaTransportControlsSessionManager manager =
                await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

            _manager = manager;
            manager.CurrentSessionChanged += (_, _) => OnSessionChanged();
            manager.SessionsChanged += (_, _) => OnSessionChanged();

            Log.Write("media", $"SMTC 就绪，当前会话数={manager.GetSessions().Count}");
            OnSessionChanged();
        }
        catch (Exception ex)
        {
            Log.Write("media", "SMTC 初始化失败（不影响任务栏宿主）", ex);
        }
    }

    private void OnSessionChanged()
    {
        _ = _dispatcher.InvokeAsync(() =>
        {
            GlobalSystemMediaTransportControlsSession? session = null;
            try
            {
                session = _manager?.GetCurrentSession();
            }
            catch (Exception ex)
            {
                Log.Write("media", "取当前会话失败", ex);
            }

            if (!ReferenceEquals(session, _session))
            {
                DetachSession();
                _session = session;
                AttachSession();
                Log.Write("media", session == null ? "当前没有媒体会话" : "切换媒体会话");
            }

            _ = RefreshAsync();
        });
    }

    private void AttachSession()
    {
        if (_session == null)
        {
            return;
        }
        try
        {
            _session.MediaPropertiesChanged += (_, _) => _ = RefreshAsync();
            _session.PlaybackInfoChanged += (_, _) => _ = RefreshAsync();
            _session.TimelinePropertiesChanged += (_, _) => OnTimelineChanged(_session);
        }
        catch (Exception ex)
        {
            Log.Write("media", "订阅会话事件失败", ex);
        }
    }

    /// <summary>
    /// 时间轴事件。除了刷新，还能在诊断模式下把"播放器什么时候推、推的值有多旧"记下来 ——
    /// 这是判断"显示落后声音一秒"到底是播放器上报滞后还是我们管线滞后的唯一办法。
    /// </summary>
    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_timelineTrace && session != null)
        {
            try
            {
                GlobalSystemMediaTransportControlsSessionTimelineProperties timeline = session.GetTimelineProperties();
                DateTimeOffset updated = timeline.LastUpdatedTime;
                double reportedAge = updated == default ? -1 : (DateTimeOffset.Now - updated).TotalSeconds;
                MediaSnapshot snapshot = Snapshot;
                TimeSpan displayed = snapshot.PositionAt(DateTimeOffset.Now);
                Log.Write("trace", $"时间轴事件 上报位置={timeline.Position.TotalSeconds:0.00}s 上报时刻={updated:HH:mm:ss.fff}（距今 {reportedAge:0.00}s）" +
                                   $"  我们显示={displayed.TotalSeconds:0.00}s  差值={(displayed - timeline.Position).TotalSeconds:+0.00;-0.00}s");
            }
            catch (Exception ex)
            {
                Log.Write("trace", "时间轴事件读取失败: " + ex.Message);
            }
        }

        _ = RefreshAsync();
    }

    /// <summary>诊断开关：RYUBAR_TIMELINE_TRACE=1 时记录每次时间轴事件的上报值与显示值。</summary>
    private readonly bool _timelineTrace = Environment.GetEnvironmentVariable("RYUBAR_TIMELINE_TRACE") == "1";

    private void DetachSession()
    {
        // 事件用匿名处理器订阅，切换会话时旧会话会被丢弃，这里只需要重置封面缓存
        _artworkKey = "";
    }

    /// <summary>读一次会话并发布快照（完整：媒体属性 + 封面 + 时间轴）。</summary>
    public Task RefreshAsync() => RefreshCoreAsync(timelineOnly: false);

    /// <summary>
    /// 只更新时间轴（位置/时长/播放状态）的便宜路径：**跳过跨进程的媒体属性读取与封面解码**。
    /// 标题/封面靠事件驱动就够了，节奏高一点也不会贵 —— 这是"同步快"和"CPU 低"能兼得的关键。
    /// </summary>
    public Task RefreshTimelineAsync() => RefreshCoreAsync(timelineOnly: true);

    private async Task RefreshCoreAsync(bool timelineOnly)
    {
        if (_disposed)
        {
            return;
        }

        GlobalSystemMediaTransportControlsSession? session = _session;
        MediaSnapshot previous = Snapshot;
        MediaSnapshot next;

        if (session == null)
        {
            next = MediaSnapshot.Empty;
        }
        else
        {
            try
            {
                GlobalSystemMediaTransportControlsSessionMediaProperties? properties = null;
                if (!timelineOnly)
                {
                    properties = await session.TryGetMediaPropertiesAsync();
                }

                GlobalSystemMediaTransportControlsSessionPlaybackInfo playback = session.GetPlaybackInfo();
                GlobalSystemMediaTransportControlsSessionTimelineProperties timeline = session.GetTimelineProperties();

                bool isPlaying = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                double rate = playback.PlaybackRate ?? 1.0;
                if (rate <= 0)
                {
                    rate = 1.0;
                }

                TimeSpan duration = timeline.EndTime - timeline.StartTime;
                if (duration < TimeSpan.Zero)
                {
                    duration = TimeSpan.Zero;
                }

                TimeSpan position = timeline.Position - timeline.StartTime;
                if (position < TimeSpan.Zero)
                {
                    position = TimeSpan.Zero;
                }

                string title = properties?.Title ?? (timelineOnly ? previous.Title : "");
                string artist = properties?.Artist ?? (timelineOnly ? previous.Artist : "");
                string album = properties?.AlbumTitle ?? (timelineOnly ? previous.Album : "");
                string source = TryDescribeSource(session);
                bool canSeek = playback.Controls?.IsPlaybackPositionEnabled ?? false;

                // 有些播放器（实测网易云）时间轴整块不上报：位置与时长都是 0。
                // 这时标记为"没有时间轴"，界面退化成只显示封面/标题/歌手/按钮，不显示假进度。
                bool hasTimeline = duration > TimeSpan.Zero || position > TimeSpan.Zero;
                bool memoryBackedTimeline = false;
                string? neteaseSongId = null;

                if (!hasTimeline && source.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase))
                {
                    // 位置：内存自校准；时长：客户端本地 JSON（实测内存里位置附近没有时长常数）
                    if (_neteaseMetadata.LoadedAt == DateTime.MinValue
                        || (DateTime.Now - _neteaseMetadata.LoadedAt).TotalSeconds > 60)
                    {
                        _neteaseMetadata.Refresh();
                    }

                    double? memorySeconds = _neteaseMemory.TryReadPositionSeconds();
                    if (memorySeconds is double seconds)
                    {
                        position = TimeSpan.FromSeconds(seconds);
                        hasTimeline = true;
                        memoryBackedTimeline = true;
                    }
                    else
                    {
                        _neteaseMemory.EnsureCalibrating();
                    }

                    if (_neteaseMetadata.Lookup(title, artist) is { } match)
                    {
                        duration = match.Duration;
                        hasTimeline = true;
                        neteaseSongId = match.Id;
                    }
                    else
                    {
                        Log.Write("netease", $"本地列表里没找到「{title}／{artist}」，本次不给时长");
                    }

                    // 自己补出来的时间轴：一律先按"不可跳转"处理。
                    // 用户的要求就是：能跳的才跳，不能跳的进度条画灰、点击不做反应。
                    // 播放器声明支持却骗人的情况，仍由 SeekAsync 验证后改成只读。
                    if (duration > TimeSpan.Zero)
                    {
                        canSeek = _seekWorks ?? false;
                    }
                }

                if (!string.Equals(title, _lastTitle, StringComparison.Ordinal))
                {
                    // 换曲：重读一次本地列表，并让"可跳转"的判断重新验证
                    _lastTitle = title;
                    _seekWorks = null;
                    _neteaseMetadata.Refresh();
                }

                string key = $"{title}\u001f{artist}\u001f{album}";
                ImageSource? artwork = timelineOnly
                    ? previous.Artwork
                    : await LoadArtworkAsync(properties!, key);

                _memoryBackedTimeline = memoryBackedTimeline;

                // 插值的锚点：播放器上报该位置的时间（LastUpdatedTime），不是我们采样那一刻。
                // 播放器可能几秒才推一次；用我们的采样时间当锚点会让显示滞后（实测差 1~2 秒很常见）。
                // 只有内存来源才用"现在"，因为那个值就是我们此刻读出来的。
                DateTimeOffset sampledAt = PositionAnchor(timeline, memoryBackedTimeline);

                next = new MediaSnapshot
                {
                    HasSession = true,
                    Source = source,
                    Title = title,
                    Artist = artist,
                    Album = album,
                    NeteaseSongId = neteaseSongId ?? "",
                    Artwork = artwork,
                    IsPlaying = isPlaying,
                    HasTimeline = hasTimeline,
                    CanPlayPause = playback.Controls?.IsPlayPauseToggleEnabled ?? false,
                    CanPrevious = playback.Controls?.IsPreviousEnabled ?? false,
                    CanNext = playback.Controls?.IsNextEnabled ?? false,
                    CanSeek = canSeek,
                    Position = position,
                    Duration = duration,
                    PlaybackRate = rate,
                    PositionSampledAt = sampledAt,
                };
            }
            catch (Exception ex)
            {
                // 会话在读取过程中消失是常态
                Log.Write("media", "读取媒体属性失败（会话可能已结束）", ex);
                next = MediaSnapshot.Empty;
            }
        }

        Publish(next);
    }

    /// <summary>
    /// 决定插值的锚点时刻。
    ///
    /// 播放器上报的位置是"<c>LastUpdatedTime</c> 那一刻的位置"，不是"我们现在读到的位置"。
    /// 有些播放器（QQ音乐等）几秒才推一次时间轴，如果拿我们自己的采样时刻当锚点，
    /// 显示出来的进度就会比实际滞后 —— 这正是"进度条定位不准"的来源。
    ///
    /// 所以：SMTC 来源用 <c>LastUpdatedTime</c>（并做合理性检查）；内存来源用"现在"
    /// （那个值就是我们此刻读出来的）。
    /// </summary>
    private static DateTimeOffset PositionAnchor(
        GlobalSystemMediaTransportControlsSessionTimelineProperties timeline,
        bool memoryBackedTimeline)
    {
        if (memoryBackedTimeline)
        {
            return DateTimeOffset.Now;
        }

        try
        {
            DateTimeOffset reported = timeline.LastUpdatedTime;
            if (reported != default)
            {
                DateTimeOffset local = reported.ToLocalTime();
                double ageSeconds = (DateTimeOffset.Now - local).TotalSeconds;
                // 合理范围：不太久以前、也不是未来（时区处理差异可能给出偏移怪值）
                if (ageSeconds >= -2 && ageSeconds <= 30)
                {
                    return local;
                }
            }
        }
        catch
        {
        }
        return DateTimeOffset.Now;
    }

    private async Task<ImageSource?> LoadArtworkAsync(
        GlobalSystemMediaTransportControlsSessionMediaProperties properties,
        string key)
    {
        if (properties.Thumbnail == null)
        {
            _artworkKey = "";
            return null;
        }
        if (key == _artworkKey && Snapshot.Artwork != null)
        {
            return Snapshot.Artwork;
        }

        try
        {
            using IRandomAccessStreamWithContentType stream = await properties.Thumbnail.OpenReadAsync();
            BitmapImage image = new();
            image.BeginInit();
            image.StreamSource = stream.AsStreamForRead();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;   // 有些播放器的封面色彩配置是坏的
            image.DecodePixelWidth = 128;
            image.EndInit();
            image.Freeze();
            _artworkKey = key;
            return image;
        }
        catch (Exception ex)
        {
            Log.Write("media", "封面解码失败", ex);
            _artworkKey = "";
            return null;
        }
    }

    private static string TryDescribeSource(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            string id = session.SourceAppUserModelId ?? "";
            int slash = id.LastIndexOf('\\');
            string name = slash >= 0 ? id[(slash + 1)..] : id;
            int bang = name.IndexOf('!');
            if (bang > 0)
            {
                name = name[..bang];
            }
            return name;
        }
        catch
        {
            return "";
        }
    }

    private void Publish(MediaSnapshot snapshot)
    {
        bool changed;
        lock (_gate)
        {
            changed = !SameAs(_snapshot, snapshot);
            _snapshot = snapshot;
        }

        // 歌词：按当前曲目拉取（按 key 幂等，重复调用只是字典查一次）
        Lyrics.EnsureLoaded(
            snapshot.Title,
            snapshot.Artist,
            snapshot.Album,
            (int)snapshot.Duration.TotalMilliseconds,
            snapshot.Source,
            snapshot.NeteaseSongId);

        if (changed)
        {
            Changed?.Invoke(snapshot);
        }
    }

    private static bool SameAs(MediaSnapshot a, MediaSnapshot b)
    {
        return a.HasSession == b.HasSession
            && a.Title == b.Title
            && a.Artist == b.Artist
            && a.Album == b.Album
            && ReferenceEquals(a.Artwork, b.Artwork)
            && a.IsPlaying == b.IsPlaying
            && a.HasTimeline == b.HasTimeline
            && a.CanPlayPause == b.CanPlayPause
            && a.CanPrevious == b.CanPrevious
            && a.CanNext == b.CanNext
            && a.CanSeek == b.CanSeek
            && a.Duration == b.Duration
            && Math.Abs((a.Position - b.Position).TotalSeconds) > 0.75;
    }

    // ---------------------------------------------------------------- 控制指令

    public Task<bool> TogglePlayPauseAsync() => RunOnSessionAsync(s => s.TryTogglePlayPauseAsync().AsTask());

    public Task<bool> SkipNextAsync() => RunOnSessionAsync(s => s.TrySkipNextAsync().AsTask());

    public Task<bool> SkipPreviousAsync() => RunOnSessionAsync(s => s.TrySkipPreviousAsync().AsTask());

    /// <summary>跳转到指定位置；播放器不支持时返回 false（界面据此不给假反馈）。</summary>
    public async Task<bool> SeekAsync(TimeSpan position)
    {
        GlobalSystemMediaTransportControlsSession? session = _session;
        if (session == null)
        {
            return false;
        }
        try
        {
            if (position < TimeSpan.Zero)
            {
                position = TimeSpan.Zero;
            }
            GlobalSystemMediaTransportControlsSessionTimelineProperties timeline = session.GetTimelineProperties();
            long ticks = (timeline.StartTime + position).Ticks;
            TimeSpan before = Snapshot.Position;
            bool accepted = await session.TryChangePlaybackPositionAsync(ticks);

            // **不能只看返回值**：实测网易云返回 True 却根本没跳。
            // 所以拿真实位置（有内存读取器时是内存里的值）验证它到底动没动。
            bool verified = await VerifySeekAsync(position, before);
            _seekWorks = verified;

            if (verified)
            {
                Log.Write("media", $"跳转已确认（目标 {position:hh\\:mm\\:ss}，返回值 {accepted}）");
                await RefreshAsync();
                MediaSnapshot current = Snapshot;
                if (current.HasSession)
                {
                    // 播放器刷新时间轴有延迟（实测约 2 秒），显示位置先跟到目标，免得看起来没反应
                    Publish(current with
                    {
                        Position = position,
                        PositionSampledAt = DateTimeOffset.Now,
                        CanSeek = true,
                    });
                }
                return true;
            }

            Log.Write("media", accepted
                ? "播放器接受了跳转请求但没有真的跳，进度条改为只读"
                : "播放器拒绝了跳转请求，进度条改为只读");
            await RefreshAsync();
            MediaSnapshot snapshot = Snapshot;
            if (snapshot.HasSession)
            {
                Publish(snapshot with { CanSeek = false });
            }
            return false;
        }
        catch (Exception ex)
        {
            Log.Write("media", "跳转失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 由界面定期调用（自带上限节流）。
    ///
    /// 必须存在的原因：网易云这类播放器**不上报时间轴就不会推事件**，
    /// 只靠事件驱动的话，内存里读到的位置只会读一次，界面上的进度就永远停住。
    /// 所以由界面按固定节奏拉：该重校准就重校准，该刷新就刷新。
    /// </summary>
    public void Poke()
    {
        if (_disposed)
        {
            return;
        }
        long now = Environment.TickCount64;

        // 节奏按需来：只读时间轴这条路径很便宜，所以可以只用一个较快的节拍。
        if (now - _lastPokeAt < PokeIntervalMilliseconds)
        {
            return;
        }
        _lastPokeAt = now;

        // 会话可能在我们没订阅的会话里悄悄开始播放（事件不会通知我们），隔一秒重挑一次
        if (now - _lastSessionCheckAt >= SessionRecheckMilliseconds)
        {
            _lastSessionCheckAt = now;
            GlobalSystemMediaTransportControlsSession? best = ChooseSession();
            if (!ReferenceEquals(best, _session))
            {
                _dispatcher.InvokeAsync(() =>
                {
                    DetachSession();
                    _session = best;
                    AttachSession();
                    _seekWorks = null;
                    _lastTitle = "";
                    Log.Write("media", best == null ? "当前没有媒体会话" : "切换媒体会话（重挑）");
                    _ = RefreshAsync();
                });
                return;
            }
        }

        // 内存来源：位置每 0.25 秒读一次 8 字节就够了
        if (_memoryBackedTimeline && _session != null)
        {
            double? seconds = _neteaseMemory.TryReadPositionSeconds();
            MediaSnapshot current = Snapshot;
            if (seconds is double value && current.HasSession)
            {
                Publish(current with
                {
                    Position = TimeSpan.FromSeconds(value),
                    PositionSampledAt = DateTimeOffset.Now,
                });
                return;
            }
            _memoryBackedTimeline = false;   // 读不到就退回完整刷新（会触发重校准）
        }

        // 只读时间轴：位置/时长/播放状态最新，标题与封面交给事件
        if (now - _lastFullRefreshAt >= FullRefreshSafetyMilliseconds || !Snapshot.HasSession)
        {
            _lastFullRefreshAt = now;
            _ = RefreshAsync();
        }
        else
        {
            _ = RefreshTimelineAsync();
        }
    }

    /// <summary>
    /// 挑当前该显示的会话。
    ///
    /// 直接用 <c>GetCurrentSession()</c> 会有个常见尴尬：同时开着几个播放器时，
    /// "当前会话"可能是一个**没在播、也没元数据**的会话 → 条上就空着，而音乐其实在放。
    /// 所以：先看系统认定的当前会话是否在播；不在播就找一个正在播的。
    /// </summary>
    private GlobalSystemMediaTransportControlsSession? ChooseSession()
    {
        try
        {
            GlobalSystemMediaTransportControlsSessionManager? manager = _manager;
            if (manager == null)
            {
                return null;
            }

            IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions = manager.GetSessions();

            // 手动选定的来源优先：只要它还在，就一直显示它（不被"正在播放"的抢走）
            if (_manualSourceKey is string manual && manual.Length > 0)
            {
                foreach (GlobalSystemMediaTransportControlsSession session in sessions)
                {
                    if (SourceKeyOf(session).Equals(manual, StringComparison.Ordinal))
                    {
                        return session;
                    }
                }
                Log.Write("media", $"手动选定的来源已消失，回到自动：{manual}");
                _manualSourceKey = null;
            }

            GlobalSystemMediaTransportControlsSession? current = manager.GetCurrentSession();
            if (IsPlaying(current))
            {
                return current;
            }

            foreach (GlobalSystemMediaTransportControlsSession session in sessions)
            {
                if (IsPlaying(session))
                {
                    return session;
                }
            }
            return current;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 当前所有可切换的声音来源（按"正在播放的在前"排序，顺序稳定）。
    /// 切换手势与提示窗都用它。
    /// </summary>
    public IReadOnlyList<MediaSourceOption> CurrentSources()
    {
        List<MediaSourceOption> options = new();
        try
        {
            GlobalSystemMediaTransportControlsSessionManager? manager = _manager;
            if (manager == null)
            {
                return options;
            }

            string? selectedKey = _session == null ? null : SourceKeyOf(_session);
            foreach (GlobalSystemMediaTransportControlsSession session in manager.GetSessions())
            {
                string key = SourceKeyOf(session);
                if (string.IsNullOrEmpty(key) || options.Any(o => o.Key == key))
                {
                    continue;
                }
                string title = "";
                try
                {
                    // 只用于兜底命名，读不到不算错
                    title = session.SourceAppUserModelId ?? "";
                }
                catch
                {
                }
                options.Add(new MediaSourceOption(
                    key,
                    SourceNames.Friendly(key, title),
                    key == selectedKey,
                    IsPlaying(session)));
            }
        }
        catch (Exception ex)
        {
            Log.Write("media", "枚举声音来源失败", ex);
        }

        return options
            .OrderByDescending(o => o.IsSelected)
            .ThenByDescending(o => o.IsPlaying)
            .ThenBy(o => o.DisplayName, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>
    /// 手动选定一个来源：之后**一直显示它**，不再被"正在播放的会话"抢走；
    /// 只有它自己消失时才回到自动。与参照实现的 <c>IsManualSelection</c> 一致。
    /// </summary>
    public bool SelectSource(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        GlobalSystemMediaTransportControlsSession? match = null;
        try
        {
            foreach (GlobalSystemMediaTransportControlsSession session in _manager?.GetSessions() ?? Array.Empty<GlobalSystemMediaTransportControlsSession>())
            {
                if (SourceKeyOf(session).Equals(key, StringComparison.Ordinal))
                {
                    match = session;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("media", "按来源键查找会话失败", ex);
        }

        if (match == null)
        {
            Log.Write("media", $"切换来源失败：找不到键 {key}");
            return false;
        }

        _manualSourceKey = key;
        _dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(match, _session))
            {
                DetachSession();
                _session = match;
                AttachSession();
                _seekWorks = null;
                _lastTitle = "";
            }
            _ = RefreshAsync();
        });
        Log.Write("media", $"手动选定来源：{SourceNames.Friendly(key)}");
        return true;
    }

    /// <summary>
    /// 按方向循环切换来源（<paramref name="steps"/> 正数向后、负数向前）。
    /// 返回切换后的来源，无法切换时返回 null。
    /// </summary>
    public MediaSourceOption? CycleSource(int steps)
    {
        if (steps == 0)
        {
            return null;
        }

        IReadOnlyList<MediaSourceOption> sources = CurrentSources();
        if (sources.Count == 0)
        {
            return null;
        }

        int current = 0;
        for (int i = 0; i < sources.Count; i++)
        {
            if (sources[i].IsSelected)
            {
                current = i;
                break;
            }
        }

        int count = sources.Count;
        int target = (((current + steps) % count) + count) % count;
        MediaSourceOption option = sources[target];
        return SelectSource(option.Key) ? option : null;
    }

    /// <summary>当前是否处于"手动选定来源"状态。</summary>
    public bool IsManualSourceSelection => _manualSourceKey != null;

    private static string SourceKeyOf(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return session.SourceAppUserModelId ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession? session)
    {
        if (session == null)
        {
            return false;
        }
        try
        {
            return session.GetPlaybackInfo().PlaybackStatus
                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 验证跳转是否真的生效：轮询真实位置，看它是否落到了目标附近（或明显跳变过）。
    /// 有些播放器的时间轴刷新有延迟（实测系统媒体播放器约 2 秒），所以给足 2.5 秒。
    /// </summary>
    private async Task<bool> VerifySeekAsync(TimeSpan target, TimeSpan before)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(250);
            await RefreshAsync();
            TimeSpan now = Snapshot.Position;
            double toTarget = Math.Abs((now - target).TotalSeconds);
            double moved = Math.Abs((now - before).TotalSeconds);
            if (toTarget <= 8 || moved >= 10)
            {
                return true;
            }
        }
        return false;
    }

    private async Task<bool> RunOnSessionAsync(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> action)
    {
        GlobalSystemMediaTransportControlsSession? session = _session;
        if (session == null)
        {
            return false;
        }
        try
        {
            bool ok = await action(session);
            if (ok)
            {
                await RefreshAsync();
            }
            return ok;
        }
        catch (Exception ex)
        {
            Log.Write("media", "控制指令失败", ex);
            return false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _manager = null;
        _session = null;
        _neteaseMemory.Dispose();
    }
}
