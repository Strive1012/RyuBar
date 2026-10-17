using System.Net.Http;
using System.Text.Json;
using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Searchers;
using Lyricify.Lyrics.Searchers.Helpers;
using RyuBar.Diagnostics;

namespace RyuBar.Lyrics;

/// <summary>
/// 歌词拉取与缓存。
///
/// 设计：
///   * 用公开库 <c>Lyricify.Lyrics.Helper</c> 做搜索/解密/解析，不自己写各家接口；
///   * **来源可配置**（默认全开）：按配置顺序逐个尝试，命中一个就停；
///   * 内存 LRU（64 条 / 4 MiB 预算，照参照实现的规格），换曲不重复请求；
///   * 只在**后台线程**发请求，界面线程只读结果；
///   * 拉不到就返回 null —— 界面安静回落到标题/歌手，不显示噪音。
/// </summary>
public sealed partial class LyricsService
{
    private const int CacheCapacity = 64;
    private const long CacheByteBudget = 4L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = new();

    private sealed record CacheEntry(LyricTrack? Track, long Bytes, LinkedListNode<string> Node);

    /// <summary>最近一次成功解析出的歌词来自哪个库（空字符串表示未知或来自磁盘缓存回放）。</summary>
    public string LastSource { get; private set; } = "";

    private string _inFlightKey = "";

    /// <summary>歌词变化（在后台线程触发，调用方自己切到 UI 线程）。</summary>
    public event Action<LyricTrack?>? Changed;

    /// <summary>当前已加载的歌词（没有就是 <see cref="LyricTrack.Empty"/>）。</summary>
    public LyricTrack Current { get; private set; } = LyricTrack.Empty;

    /// <summary>最近一次拉取的结果描述，写日志/诊断用。</summary>
    public string LastStatus { get; private set; } = "尚未拉取";

    /// <summary>
    /// 确保这首曲目的歌词已加载（幂等）。曲名/歌手/时长任一变化都会触发新的拉取。
    /// </summary>
    /// <param name="sourceKey">当前来源标识（如 cloudmusic.exe）—— 用来决定走哪条取词链。</param>
    public void EnsureLoaded(string title, string artist, string album, int durationMs, string sourceKey, string? neteaseSongId)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        string key = string.Join('\u001f', title, artist, album, durationMs.ToString(), neteaseSongId ?? "");
        lock (_gate)
        {
            if (_cache.ContainsKey(key) || _inFlightKey == key)
            {
                return;
            }
            _inFlightKey = key;
        }

        // 先同步把"当前曲目"清掉，避免显示上一首的歌词
        SetCurrent(LyricTrack.Empty, "换曲，等待歌词");

        _ = Task.Run(() => LoadAsync(key, title, artist, album, durationMs, sourceKey, neteaseSongId));
    }

    private async Task LoadAsync(
        string key,
        string title,
        string artist,
        string album,
        int durationMs,
        string sourceKey,
        string? neteaseSongId)
    {
        LyricTrack? track = null;
        string status = "没有可用的歌词来源";
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        long searchMs = 0;
        long fetchMs = 0;

        try
        {
            // 方案 B：取词我们自己写（库负责搜索、解密与解析）。
            // ① 网易云：先看本地曲目 id，没有再按 曲名/歌手/时长 搜索兜底（实测匹配常常是 Perfect）
            bool netease = !string.IsNullOrEmpty(neteaseSongId)
                || sourceKey.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase);
            if (netease)
            {
                string? songId = neteaseSongId;
                if (string.IsNullOrEmpty(songId))
                {
                    long t0 = watch.ElapsedMilliseconds;
                    songId = (await ResolveSearchIdAsync(title, artist, album, durationMs, Searchers.Netease)
                        .ConfigureAwait(false)).Id;
                    searchMs += watch.ElapsedMilliseconds - t0;
                    status = string.IsNullOrEmpty(songId) ? "网易云搜索没命中" : $"网易云搜索命中 id={songId}";
                }

                if (!string.IsNullOrEmpty(songId))
                {
                    string rawKey = $"netease|{title}|{artist}";
                    track = TryFromDisk(rawKey, title, artist, null, out string diskNote);
                    status = diskNote.Length > 0 ? $"网易云 {diskNote}" : "";
                    if (track == null)
                    {
                    try
                    {
                        long t0 = watch.ElapsedMilliseconds;
                        string? raw = await FetchNetEaseAsync(songId).ConfigureAwait(false);
                        fetchMs += watch.ElapsedMilliseconds - t0;
                        if (!string.IsNullOrWhiteSpace(raw))
                        {
                            track = BuildAndCache(rawKey, raw, title, artist, null, out string note);
                            status = $"网易云(id={songId}) {note}" + (track is { HasLines: true } ? "（已缓存）" : "（解析不出行，未缓存）");
                        }
                        else
                        {
                            status = $"网易云(id={songId}) 接口没返回歌词";
                        }
                    }
                    catch (Exception ex)
                    {
                        status = "网易云取词失败: " + ex.Message;
                    }
                    }
                }
            }

            if (track == null || !track.HasLines)
            {
                // ② QQ音乐：搜索拿 id/mid → 库的 Api 先新接口后旧接口 → 解密 → 解析
                bool qq = sourceKey.Contains("qqmusic", StringComparison.OrdinalIgnoreCase);
                if (qq)
                {
                    long t0 = watch.ElapsedMilliseconds;
                    (string? qqId, string? qqMid) =
                        await ResolveSearchIdAsync(title, artist, album, durationMs, Searchers.QQMusic).ConfigureAwait(false);
                    searchMs += watch.ElapsedMilliseconds - t0;
                    if (string.IsNullOrEmpty(qqId) && string.IsNullOrEmpty(qqMid))
                    {
                        status = "QQ音乐搜索没命中";
                    }
                    else
                    {
                        string rawKey = $"qq|{title}|{artist}";
                        track = TryFromDisk(rawKey, title, artist, LyricsRawTypes.Qrc, out string diskNote);
                        status = diskNote.Length > 0 ? $"QQ音乐 {diskNote}" : "";
                        if (track == null)
                        {
                        try
                        {
                            long t1 = watch.ElapsedMilliseconds;
                            string? decrypted = await FetchQqMusicAsync(qqId, qqMid).ConfigureAwait(false);
                            fetchMs += watch.ElapsedMilliseconds - t1;
                            if (!string.IsNullOrWhiteSpace(decrypted))
                            {
                                track = BuildAndCache(rawKey, decrypted, title, artist, LyricsRawTypes.Qrc, out string note);
                                status = $"QQ音乐(id={qqId} mid={qqMid}) {note}" + (track is { HasLines: true } ? "（已缓存）" : "（解析不出行，未缓存）");
                            }
                            else
                            {
                                status = $"QQ音乐(id={qqId} mid={qqMid}) 没取到歌词";
                            }
                        }
                        catch (Exception ex)
                        {
                            status = "QQ音乐取词失败: " + ex.Message;
                        }
                        }
                    }
                }
                else if (!netease)
                {
                    status = "当前来源没有对应的歌词通道";
                }
            }

            // ③ 跨来源兜底（参照实现本来就是跨源比较的）：
            //    任一来源被限流/取不到词时，用另一家按「曲名 + 歌手」取词 —— 歌词文本与播放器无关。
            if (track == null || !track.HasLines)
            {
                string otherLabel = netease ? "qq" : "netease";
                Searchers otherSearcher = netease ? Searchers.QQMusic : Searchers.Netease;
                // ★ 网易云返回的是**标准 LRC 文本** ✗ 显式告诉解析器 ✓
                //   原来是 null ⇒ ParseHelper 走"自动判断" ⇒ 判断失败 ⇒ "解析后没有行"✓
                //   （实测：原始长度=922，内容就是 `[00:17.43]和你并肩同行` 这种标准 LRC ✓）
                LyricsRawTypes? otherType = otherLabel == "qq" ? LyricsRawTypes.Qrc : LyricsRawTypes.Lrc;
                string rawKey = $"{otherLabel}|{title}|{artist}";

                track = TryFromDisk(rawKey, title, artist, otherType, out string fallbackDiskNote);
                if (track != null)
                {
                    status = $"跨来源兜底 {fallbackDiskNote}";
                }
                else
                {
                    long t0 = watch.ElapsedMilliseconds;
                    (string? otherId, string? otherMid) = await ResolveSearchIdAsync(
                        title, artist, album, durationMs, otherSearcher).ConfigureAwait(false);
                    searchMs += watch.ElapsedMilliseconds - t0;

                    if (!string.IsNullOrEmpty(otherId) || !string.IsNullOrEmpty(otherMid))
                    {
                        long t1 = watch.ElapsedMilliseconds;
                        string? fallbackRaw = otherLabel == "qq"
                            ? await FetchQqMusicAsync(otherId, otherMid).ConfigureAwait(false)
                            : await FetchNetEaseAsync(otherId!).ConfigureAwait(false);
                        fetchMs += watch.ElapsedMilliseconds - t1;

                        if (!string.IsNullOrWhiteSpace(fallbackRaw))
                        {
                            track = BuildAndCache(rawKey, fallbackRaw, title, artist, otherType, out string note2);
                            status = $"跨来源兜底({otherLabel}) {note2}" +
                                     (track is { HasLines: true } ? "（已缓存）" : "（解析不出行，未缓存）");
                        }
                        else
                        {
                            status += $"；跨来源兜底({otherLabel}) 也没取到";
                        }
                    }
                    else
                    {
                        status += $"；跨来源兜底({otherLabel}) 搜索没命中";
                    }
                }
            }
        }
        catch (Exception ex)
        {
            status = "歌词拉取异常: " + ex.Message;
            Log.Write("lyrics", status, ex);
        }

        LastStatus = status;
        long totalMs = watch.ElapsedMilliseconds;
        Log.Write("lyrics", $"{title} / {artist} → {status}");
        Log.Write("lyrics", $"耗时 合计={totalMs}ms（搜索={searchMs}ms 取词={fetchMs}ms 解析+其它={totalMs - searchMs - fetchMs}ms）");

        if (Environment.GetEnvironmentVariable("RYUBAR_LYRIC_TRACE") == "1" && track is { HasLines: true })
        {
            foreach (string structureLine in DescribeStructure(track))
            {
                Log.Write("lyrics", "结构 " + structureLine);
            }
        }

        lock (_gate)
        {
            _inFlightKey = "";
            Store(key, track);
        }

        // ④ 两家都没拿到（多为接口临时限流）：15 秒后自动重试一次。
        //    做法是把这条缓存记录删掉 —— 宿主每次状态更新都会调 EnsureLoaded，
        //    记录一没就会重新走一遍链路（当前仍在放这首才有意义，唱片换了自然也不会查它）。
        if (track == null || !track.HasLines)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_cache.Remove(key, out CacheEntry? removed))
                    {
                        _order.Remove(removed.Node);
                    }
                }
                Log.Write("lyrics", $"「{title}」没取到歌词，15 秒后已允许重试");
            });
        }

        SetCurrent(track ?? LyricTrack.Empty, status);
    }

    /// <summary>取词用的 HttpClient（复用连接；超时短一点，歌词不值得等）。</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// 库的客户端与搜索器都**长期复用**（参照实现就是把 `Api` 当字段持有的 ✓）。
    /// 每次 `new` 都会重建连接池，冷连接要多花约 300ms（实测：热连接 100ms vs 冷连接 286ms）。
    /// </summary>
    private static readonly Lyricify.Lyrics.Providers.Web.QQMusic.Api QqLyricApi = new();

    private static readonly Lyricify.Lyrics.Providers.Web.Netease.Api NeteaseLyricApi = new();

    /// <summary>
    /// 搜索器**每次新建**：库的搜索器不是线程安全的，池化会导致并发调用互相污染
    /// （实测症状：同一个查询时而命中、时而 0 候选，耗时忽长忽短）。
    /// 参照实现也是每次 `source.GetSearcher()` 新建，只有 `Api`（连接池）才长期持有。
    /// </summary>
    private static ISearcher SearcherFor(Searchers searcher) => searcher.GetSearcher();

    /// <summary>
    /// 网易云取词：用库的公开 <c>Netease.Api</c>，**新接口（逐字 yrc）优先，退回旧接口（行级 lrc）**。
    /// 与参照实现的取向一致（它把新旧两路并发取回再合并）；这里保持顺序试，够用且简单。
    /// </summary>
    public static async Task<string?> FetchNetEaseAsync(string songId)
    {
        Lyricify.Lyrics.Providers.Web.Netease.Api api = NeteaseLyricApi;

        try
        {
            Lyricify.Lyrics.Providers.Web.Netease.LyricResult? wordLevel =
                await api.GetLyricNew(songId).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(wordLevel?.Yrc?.Lyric))
            {
                return wordLevel.Yrc.Lyric;
            }
            if (!string.IsNullOrWhiteSpace(wordLevel?.Lrc?.Lyric))
            {
                return wordLevel.Lrc.Lyric;
            }
        }
        catch (Exception ex)
        {
            Log.Write("lyrics", "网易云新接口失败，改试旧接口: " + ex.Message);
        }

        try
        {
            Lyricify.Lyrics.Providers.Web.Netease.LyricResult? legacy =
                await api.GetLyric(songId).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(legacy?.Lrc?.Lyric) ? null : legacy.Lrc.Lyric;
        }
        catch (Exception ex)
        {
            Log.Write("lyrics", "网易云旧接口失败: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// QQ音乐取词。
    ///
    /// 行为与参照实现一致（**行为对齐，代码自研**）：用库的公开 <c>QQMusic.Api</c> 类，
    /// **先试新接口（按数字 id，返回已解密的 QRC）**，失败再试**旧接口（按 songmid，要求 Code==0）**。
    /// 之前我只走一条自写 HTTP，遇到旧公开接口 <c>retcode 1101</c>（要鉴权）就没有退路 ——
    /// 稳定性正是来自"两个端点 + 逐层回退 + Code 校验"。
    /// </summary>
    public static async Task<string?> FetchQqMusicAsync(string? songId, string? songMid)
    {
        Lyricify.Lyrics.Providers.Web.QQMusic.Api api = QqLyricApi;

        if (!string.IsNullOrWhiteSpace(songId))
        {
            try
            {
                var response = await api.GetLyricsAsync(songId).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(response?.Lyrics))
                {
                    return response!.Lyrics;
                }
            }
            catch (Exception ex)
            {
                Log.Write("lyrics", "QQ 新接口失败，改试旧接口: " + ex.Message);
            }
        }

        if (string.IsNullOrWhiteSpace(songMid))
        {
            return null;
        }

        try
        {
            var legacy = (await api.GetLyric(songMid).ConfigureAwait(false))?.Decode();
            if (legacy is { Code: 0 } && !string.IsNullOrWhiteSpace(legacy.Lyric))
            {
                return legacy.Lyric;
            }
        }
        catch (Exception ex)
        {
            Log.Write("lyrics", "QQ 旧接口失败: " + ex.Message);
        }

        return null;
    }

    /// <summary>
    /// 用库的搜索按 曲名/歌手/时长 找曲目 id（各来源通用；本地已知 id 时不走这里）。
    /// 注意：库的异步续体没有 <c>ConfigureAwait(false)</c>，必须丢到线程池上跑，
    /// 否则在 UI 线程同步等待会死锁（实测踩过）。
    /// 搜索是网络调用，会偶发失败（实测同一个查询时而命中时而不中），所以**重试两次**，
    /// 并把解析结果记下来，避免同一首歌反复打网络。
    /// </summary>
    /// <summary>
    /// 搜索候选并自己打分选最佳（行为对齐参照实现，代码自研）：
    ///
    /// * **查询词构造**：SMTC 的曲名常带译名括号（如「The Other Side of Paradise（天堂彼岸）」），
    ///   原样去查必然查不到 → 依次试「剥掉末尾含汉字的括号段」后的标题、原始标题、以及只给标题；
    ///   但括号里若是**版本标记**（现场/live/remix/伴奏…）就不剥，那本来就是歌名的一部分。
    /// * **用 `SearchForResults(string)` 原串重载**：`ITrackMetadata` 重载会套用库内评分（不可控）。
    /// * **自己打分**：标题 + 歌手 + 时长（我们还额外有已知时长，可以当硬校验）。
    /// </summary>
    private static async Task<(string? Id, string? Mid)> ResolveSearchIdAsync(
        string title,
        string artist,
        string album,
        int durationMs,
        Searchers searcher)
    {
        // 键里**不放时长**：切换曲目瞬间播放器常把时长报成 0，会把缓存键打散导致次次未命中（实测踩过）。
        // 时长仍然参与打分（校验用），只是不参与缓存键。
        string cacheKey = string.Join('\u001f', searcher.ToString(), title, artist);
        lock (SearchIdCache)
        {
            if (SearchIdCache.TryGetValue(cacheKey, out string? cached))
            {
                return SplitIds(cached);
            }
        }
        if (LyricsDiskCache.TryGetId(cacheKey, out string persisted))
        {
            lock (SearchIdCache)
            {
                SearchIdCache[cacheKey] = persisted;
            }
            Log.Write("lyrics", $"{searcher} 曲目 id 走磁盘缓存（0 网络）");
            return SplitIds(persisted);
        }

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            foreach (string query in BuildQueries(title, artist))
            {
                List<ISearchResult> candidates;
                try
                {
                    ISearcher instance = SearcherFor(searcher);
                    candidates = await Task.Run(() => instance.SearchForResults(query)).ConfigureAwait(false)
                        ?? new List<ISearchResult>();
                }
                catch (Exception ex)
                {
                    Log.Write("lyrics", $"{searcher} 搜索「{query}」异常: " + ex.Message);
                    continue;
                }

                string? bestId = null;
                string? bestMid = null;
                int bestScore = 0;
                foreach (ISearchResult candidate in candidates)
                {
                    int score = ScoreCandidate(title, artist, durationMs, candidate, out string? id, out string? mid);
                    if (score > bestScore && (id != null || mid != null))
                    {
                        bestScore = score;
                        bestId = id;
                        bestMid = mid;
                    }
                }

                if (bestScore >= MinimumCandidateScore)
                {
                    string record = $"{bestId}|{bestMid}";
                    lock (SearchIdCache)
                    {
                        SearchIdCache[cacheKey] = record;
                    }
                    LyricsDiskCache.SaveId(cacheKey, record);
                    Log.Write("lyrics", $"{searcher} 搜索「{query}」命中 id={bestId} mid={bestMid} 得分={bestScore}");
                    return (bestId, bestMid);
                }

                Log.Write("lyrics", $"{searcher} 查询「{query}」→ 候选 {candidates.Count} 个，最佳得分 {bestScore}（下限 {MinimumCandidateScore}）");
            }

            Log.Write("lyrics", $"{searcher} 第 {attempt} 次搜索没命中，重试");
            if (attempt < 2)
            {
                // 重试等待要短：搜索本身只要 ~100ms，等 500ms 反而成了体感"慢"的主因
                await Task.Delay(150).ConfigureAwait(false);
            }
        }

        return (null, null);
    }

    private static (string? Id, string? Mid) SplitIds(string record)
    {
        string[] parts = record.Split('|');
        return (parts[0].Length == 0 ? null : parts[0],
            parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null);
    }

    /// <summary>候选取用下限（满分 100：标题 60 + 歌手 25 + 时长 15）。</summary>
    private const int MinimumCandidateScore = 60;

    private static int ScoreCandidate(
        string title,
        string artist,
        int durationMs,
        ISearchResult candidate,
        out string? id,
        out string? mid)
    {
        ReadSearchIds(candidate, out id, out mid);

        string wantTitle = Normalize(title);
        string gotTitle = Normalize(ReadText(candidate, "Title"));
        string wantArtist = Normalize(artist);
        string gotArtist = Normalize(string.Join(" ", ReadArtists(candidate)));

        int score = 0;
        if (gotTitle.Length > 0 && gotTitle == wantTitle)
        {
            score += 60;
        }
        else if (wantTitle.Length >= 4 && gotTitle.Length >= 4
            && (gotTitle.Contains(wantTitle, StringComparison.Ordinal)
                || wantTitle.Contains(gotTitle, StringComparison.Ordinal)))
        {
            score += 35;
        }

        if (gotArtist.Length > 0 && wantArtist.Length > 0)
        {
            if (gotArtist == wantArtist)
            {
                score += 25;
            }
            else if (gotArtist.Contains(wantArtist, StringComparison.Ordinal)
                || wantArtist.Contains(gotArtist, StringComparison.Ordinal))
            {
                score += 12;
            }
        }

        // 时长不吻合基本就是另一首（有些播放器不上报时长，那就跳过这一项）
        if (durationMs > 0 && candidate.DurationMs is int candidateMs and > 0)
        {
            double deltaSeconds = Math.Abs(candidateMs - durationMs) / 1000.0;
            if (deltaSeconds <= 3)
            {
                score += 15;
            }
            else if (deltaSeconds <= 10)
            {
                score += 8;
            }
            else if (deltaSeconds > 25)
            {
                score -= 40;
            }
        }

        return score;
    }

    /// <summary>去掉末尾"含汉字的括号段"（通常是播放器补的译名）；版本标记不剥。</summary>
    internal static string StripTranslation(string value)
    {
        System.Text.RegularExpressions.Match match = TranslationSuffixPattern().Match(value);
        if (!match.Success)
        {
            return value.Trim();
        }
        return VersionMarkerPattern().IsMatch(match.Value) ? value.Trim() : value[..match.Index].Trim();
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"\s*[（(【\[][^（()）【\[\]]*[\p{IsCJKUnifiedIdeographs}][^（()）【\[\]]*[）)】\]]\s*$")]
    private static partial System.Text.RegularExpressions.Regex TranslationSuffixPattern();

    [System.Text.RegularExpressions.GeneratedRegex(
        "现场|新版|新编|伴奏|翻唱|混音|重制|重录|纯音乐|live|remix|cover|acoustic|instrumental|version",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex VersionMarkerPattern();

    /// <summary>依次尝试的查询词（去重、非空）。</summary>
    private static IReadOnlyList<string> BuildQueries(string title, string artist)
    {
        string cleanTitle = StripTranslation(title);
        string cleanArtist = StripTranslation(artist);
        List<string> queries = new();
        void Add(string value)
        {
            string trimmed = value.Trim();
            if (trimmed.Length > 0 && !queries.Contains(trimmed, StringComparer.Ordinal))
            {
                queries.Add(trimmed);
            }
        }

        Add($"{cleanTitle} {cleanArtist}");
        Add($"{title} {artist}");
        Add(cleanTitle);
        return queries;
    }

    /// <summary>比较用的归一化：去空白、去常见标点与括号、转小写。</summary>
    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        System.Text.StringBuilder builder = new(value.Length);
        foreach (char ch in value)
        {
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch))
            {
                continue;
            }
            builder.Append(char.ToLowerInvariant(ch));
        }
        return builder.ToString();
    }

    private static string ReadText(ISearchResult candidate, string name)
    {
        Type type = candidate.GetType();
        object? value = type.GetProperty(name)?.GetValue(candidate) ?? type.GetField(name)?.GetValue(candidate);
        return value?.ToString() ?? "";
    }

    private static IEnumerable<string> ReadArtists(ISearchResult candidate)
    {
        Type type = candidate.GetType();
        object? value = type.GetProperty("Artists")?.GetValue(candidate) ?? type.GetField("Artists")?.GetValue(candidate);
        if (value is System.Collections.IEnumerable list and not string)
        {
            foreach (object? item in list)
            {
                if (item != null)
                {
                    yield return item.ToString() ?? "";
                }
            }
            yield break;
        }
        string text = value?.ToString() ?? "";
        if (text.Length > 0)
        {
            yield return text;
        }
    }

    /// <summary>曲目 → id/mid 的记忆（同一首歌不再重复搜索）。</summary>
    private static readonly Dictionary<string, string> SearchIdCache = new(StringComparer.Ordinal);

    /// <summary>
    /// 从搜索结果里读曲目 id / mid。各来源的具体类型字段名不完全一致，这里按名字找：
    /// <c>Id</c> = 数字曲目 id（QQ 新接口、网易云用它），<c>Mid</c> = 字符串曲目 mid（QQ 旧接口用它）。
    /// 找不到就返回空（宁可没歌词，也不乱猜）。
    /// </summary>
    private static void ReadSearchIds(ISearchResult? result, out string? id, out string? mid)
    {
        id = null;
        mid = null;
        if (result == null)
        {
            return;
        }

        try
        {
            Type type = result.GetType();
            foreach (string name in new[] { "Id", "SongId", "ProviderLyricsId" })
            {
                if (ReadMember(result, type, name) is { } value)
                {
                    id = value;
                    break;
                }
            }
            foreach (string name in new[] { "Mid", "SongMid" })
            {
                if (ReadMember(result, type, name) is { } value)
                {
                    mid = value;
                    break;
                }
            }

            if (id == null && mid == null)
            {
                // 读不到就把真实成员名列出来，免得靠猜（这次就是靠它定位的）
                IEnumerable<string> names = type
                    .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                    .Select(p => p.Name)
                    .Concat(type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                        .Select(f => f.Name + "(field)"));
                Log.Write("lyrics", $"搜索结果 {type.Name} 里没找到 id/mid，实际成员: {string.Join(", ", names)}");
            }
        }
        catch (Exception ex)
        {
            Log.Write("lyrics", "读取搜索结果的 id 失败: " + ex.Message);
        }
    }

    private static string? ReadMember(ISearchResult result, Type type, string name)
    {
        object? value = type.GetProperty(name)?.GetValue(result) ?? type.GetField(name)?.GetValue(result);
        return value == null || string.IsNullOrWhiteSpace(value.ToString()) ? null : value.ToString();
    }

    /// <summary>
    /// 启动后台预热：先把库客户端的连接池热起来。
    ///
    /// 冷连接建立（DNS+TCP+TLS）实测要 ~700ms，而热连接只要 ~100ms ——
    /// 启动时用一次无害请求把连接池填上，用户切歌时就是热的（这就是参照实现"看起来很快"的一半原因）。
    /// </summary>
    public static void WarmUp()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await SearcherFor(Searchers.QQMusic).SearchForResults("test").ConfigureAwait(false);
                await SearcherFor(Searchers.Netease).SearchForResults("test").ConfigureAwait(false);
                await QqLyricApi.GetLyric("001HeMEN0hplNv").ConfigureAwait(false);
                await NeteaseLyricApi.GetLyric("347230").ConfigureAwait(false);
                Log.Write("lyrics", "连接预热完成");
            }
            catch (Exception ex)
            {
                Log.Write("lyrics", "连接预热失败（忽略）: " + ex.Message);
            }
        });
    }

    /// <summary>
    /// 诊断（RYUBAR_LYRIC_TRACE=1）：把解析结果的结构摊开，用于定位
    /// 逐字不同步（时间到底是绝对还是行内相对）与"源里一行写了两句"的结构问题。
    /// </summary>
    private static IEnumerable<string> DescribeStructure(LyricTrack track)
    {
        List<string> lines = new();

        LyricLine? sample = track.Lines.FirstOrDefault(l => l.HasSyllables);
        if (sample?.Syllables is { Count: > 0 } syllables)
        {
            lines.Add($"逐字抽样 行[{sample.StartMs}-{sample.EndMs}ms] 「{sample.Text}」");
            int index = 0;
            foreach (LyricSyllable syllable in syllables)
            {
                lines.Add($"    #{index} 「{syllable.Text}」 {syllable.StartMs}-{syllable.EndMs}ms");
                if (++index >= 10)
                {
                    break;
                }
            }
            int lastStart = syllables[^1].StartMs;
            lines.Add(lastStart <= sample.EndMs + 2000
                ? "    末字起点落在行内 → 绝对时间 ✓"
                : $"    末字起点 {lastStart}ms 远超行尾 → 疑似行内相对时间 ✗");
        }

        int embedded = track.Lines.Count(l => l.Text.Contains('\n'));
        lines.Add($"文本内含换行的行数 {embedded}/{track.Lines.Count}");
        foreach (LyricLine line in track.Lines.Where(l => l.Text.Contains('\n')).Take(3))
        {
            lines.Add($"    换行行 {line.StartMs}ms: " + line.Text.Replace("\n", " ⏎ "));
        }

        int sameTime = 0;
        for (int i = 1; i < track.Lines.Count; i++)
        {
            if (Math.Abs(track.Lines[i].StartMs - track.Lines[i - 1].StartMs) <= 1)
            {
                sameTime++;
                if (sameTime <= 3)
                {
                    lines.Add($"    同一时刻 {track.Lines[i].StartMs}ms: 「{track.Lines[i - 1].Text}」/「{track.Lines[i].Text}」");
                }
            }
        }
        lines.Add($"同一时刻并列的行数 {sameTime}");

        return lines;
    }

    /// <summary>
    /// 从磁盘缓存取词：**必须能解析出至少一行**才算命中。
    /// 之前只看"有没有原文"就写入缓存，结果解析不出行的废文本被永久缓存、每次重试都在复用（实测就是这个 bug
    /// 导致某首歌一直没歌词）→ 现在缓存无效立刻丢弃并按未命中处理，交给下面的网络取词。
    /// </summary>
    private static LyricTrack? TryFromDisk(
        string rawKey,
        string title,
        string artist,
        LyricsRawTypes? rawType,
        out string status)
    {
        status = "";
        if (!LyricsDiskCache.TryGetRaw(rawKey, out string cached) || string.IsNullOrWhiteSpace(cached))
        {
            return null;
        }

        LyricTrack? track = Build(cached, title, artist, out string note, rawType);
        if (track is { HasLines: true })
        {
            status = $"磁盘缓存命中（{note}）";
            return track;
        }

        LyricsDiskCache.RemoveRaw(rawKey);
        status = "磁盘缓存无效（解析不出行），已丢弃并将重新取词";
        return null;
    }

    /// <summary>取词成功后**先解析、有行才写缓存**。</summary>
    private static LyricTrack? BuildAndCache(
        string rawKey,
        string raw,
        string title,
        string artist,
        LyricsRawTypes? rawType,
        out string note)
    {
        LyricTrack? track = Build(raw, title, artist, out note, rawType);
        if (track is { HasLines: true })
        {
            LyricsDiskCache.SaveRaw(rawKey, raw);
        }
        return track;
    }

    /// <summary>用库（它负责解密与解析）把原始歌词文本变成我们的模型。</summary>
    public static LyricTrack? Build(string raw, string title, string artist, out string note, LyricsRawTypes? rawType = null)
    {
        note = "解析失败";
        LyricsData? data = rawType is { } type
            ? ParseHelper.ParseLyrics(raw, type)
            : ParseHelper.ParseLyrics(raw);
        if (data?.Lines is not { Count: > 0 })
        {
            note = "解析后没有行";

            // ★ 诊断：把原始文本的头尾打出来 ✗
            //   用户实测「爱，请问怎么走 / A-Lin」从网易云取到了歌词但解析不出行 ✓
            //   只看"解析后没有行"无法判断是格式不认识（如纯逐字 JSON、KRC 变体），
            //   还是内容本身是空的/占位 ✓ 打出来才能对症 ✓
            int length = raw?.Length ?? 0;
            string head = raw is null || raw.Length == 0
                ? "(空)"
                : raw[..Math.Min(160, raw.Length)].Replace("\r", "\\r").Replace("\n", "\\n");
            string tail = raw is null || raw.Length <= 160
                ? ""
                : " …尾:" + raw[^Math.Min(60, raw.Length)..].Replace("\r", "\\r").Replace("\n", "\\n");
            Diagnostics.Log.Write("lyrics", $"解析不出行 —— 原始长度={length} type={rawType} 头:{head}{tail}");
            return null;
        }

        LyricTrack track = ToLyricTrack(data, title, artist);
        note = $"命中（{data.File?.SyncTypes}）行数 {track.Lines.Count}";

        // 记录歌词来源：QRC = QQ音乐，LRC = 网易云，null = 未知（磁盘缓存回放）
        string source = rawType switch
        {
            LyricsRawTypes.Qrc => "QQ音乐",
            LyricsRawTypes.Lrc => "网易云",
            _ => "",
        };
        return track with { Source = source };
    }

    /// <summary>匹配 QRC 逐字元组 `(起始,时长,0)` —— 清理正文里残留的坐标串。</summary>
    private static readonly System.Text.RegularExpressions.Regex TuplePattern =
        new(@"\(\s*-?\d+\s*,\s*-?\d+\s*,\s*-?\d+\s*\)", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static LyricTrack ToLyricTrack(LyricsData data, string title, string artist)
    {
        List<LyricLine> lines = new(data.Lines?.Count ?? 0);
        foreach (ILineInfo line in data.Lines ?? new List<ILineInfo>())
        {
            if (string.IsNullOrWhiteSpace(line.Text) || line.StartTime is not int start)
            {
                continue;
            }

            int end = line.EndTime ?? start;
            List<LyricSyllable>? syllables = null;
            if (line is SyllableLineInfo { Syllables.Count: > 0 } syllableLine)
            {
                syllables = new List<LyricSyllable>(syllableLine.Syllables.Count);
                foreach (ISyllableInfo syllable in syllableLine.Syllables)
                {
                    syllables.Add(new LyricSyllable(syllable.Text ?? "", syllable.StartTime, syllable.EndTime));
                }
            }

            // ★ QRC（逐字格式）的 line.Text 是不可靠的 ✗
            //   它有时是"（起始,时长,0）字（起始,时长,0）字"这种带元组的原始串，
            //   直接当正文会渲染成 `(44880,240,0)连(45120,480,0)相…`（实测 ✓）
            //   有逐字数据时用音节文本重新拼出正文 ✓ 这才是用户该看到的 ✓
            string text = syllables is { Count: > 0 }
                ? string.Concat(syllables.Select(s => s.Text)).Trim()
                : line.Text.Trim();

            // 兜底：没有逐字数据、但正文里仍残留 `(开始,持续,0)` 这类元组时清掉 ✓
            if (text.Contains('('))
            {
                text = TuplePattern.Replace(text, string.Empty).Trim();
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            lines.Add(new LyricLine(text, start, end, syllables));
        }

        lines.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        for (int index = 0; index < lines.Count - 1; index++)
        {
            // 行尾缺失/为 0 时用下一行的起点兜底，否则"当前行"会瞬间跳走
            if (lines[index].EndMs <= lines[index].StartMs)
            {
                lines[index] = lines[index] with { EndMs = lines[index + 1].StartMs };
            }
        }

        return new LyricTrack(title, artist, lines);
    }

    private void Store(string key, LyricTrack? track)
    {
        long bytes = EstimateBytes(track);
        LinkedListNode<string> node = _order.AddLast(key);
        _cache[key] = new CacheEntry(track, bytes, node);

        long total = _cache.Values.Sum(v => v.Bytes);
        while (_cache.Count > CacheCapacity || (total > CacheByteBudget && _order.Count > 1))
        {
            LinkedListNode<string>? oldest = _order.First;
            if (oldest == null)
            {
                break;
            }
            if (_cache.Remove(oldest.Value, out CacheEntry? removed))
            {
                total -= removed.Bytes;
            }
            _order.RemoveFirst();
        }
    }

    private static long EstimateBytes(LyricTrack? track)
    {
        if (track == null)
        {
            return 16;
        }
        long bytes = 64;
        foreach (LyricLine line in track.Lines)
        {
            bytes += 64 + (line.Text.Length * 2);
            if (line.Syllables != null)
            {
                foreach (LyricSyllable syllable in line.Syllables)
                {
                    bytes += 32 + (syllable.Text.Length * 2);
                }
            }
        }
        return bytes;
    }

    /// <summary>把缓存里那一份设为当前（用于换曲后立即可用，不必重新请求）。</summary>
    public bool TryActivate(string title, string artist, string album, int durationMs, string? neteaseSongId)
    {
        string key = string.Join('\u001f', title, artist, album, durationMs.ToString(), neteaseSongId ?? "");
        CacheEntry? entry;
        lock (_gate)
        {
            _cache.TryGetValue(key, out entry);
        }
        if (entry == null)
        {
            return false;
        }
        SetCurrent(entry.Track ?? LyricTrack.Empty, "缓存命中");
        return true;
    }

    private void SetCurrent(LyricTrack track, string status)
    {
        LastStatus = status;
        Current = track;
        Changed?.Invoke(track);
    }
}
