using System.Diagnostics;
using System.IO;
using System.Text.Json;
using RyuBar.Diagnostics;

namespace RyuBar.Media.Readers;

/// <summary>
/// 网易云的时长来源：读客户端自己写的播放列表 JSON。
///
/// 实测结构（本机云音乐版本）：
///   playingList: { "list":  [ { "track": { "name", "artists":[{"name"}], "duration": 毫秒 } } ] }
///   fmPlay:      { "currentIndex": n, "queue": [ { "name", "artists":[{"name"}], "duration": 毫秒 } ] }
///
/// 时长的真值在这里比在内存里可靠：内存里位置附近并没有时长常数（实测附近只有两个无关常数）。
/// 匹配策略照参照实现的做法：先按"曲名 + 歌手"精确匹配，再退回"曲名互为子串"，
/// 都匹配不上就**不给时长**（宁可没有，也不给错的）。
/// </summary>
internal sealed class NetEaseMetadataIndex
{
    private readonly List<Entry> _entries = new();
    private DateTime _loadedAt = DateTime.MinValue;

    private sealed record Entry(string Title, string Artist, TimeSpan Duration, string Id);

    /// <summary>查到的曲目信息（时长 + 曲目 id，id 用于按 id 精确取歌词）。</summary>
    public sealed record Match(TimeSpan Duration, string Id);

    public int Count => _entries.Count;

    public DateTime LoadedAt => _loadedAt;

    private static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetEase", "CloudMusic", "WebData", "file");

    /// <summary>重新读两个 JSON。文件很小，按需读即可。</summary>
    public void Refresh()
    {
        _entries.Clear();
        foreach (string name in new[] { "playingList", "fmPlay" })
        {
            string path = Path.Combine(Root, name);
            if (!File.Exists(path))
            {
                continue;
            }
            try
            {
                using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using JsonDocument document = JsonDocument.Parse(stream);
                JsonElement root = document.RootElement;

                if (root.TryGetProperty("list", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in list.EnumerateArray())
                    {
                        JsonElement track = item.TryGetProperty("track", out JsonElement t) ? t : item;
                        Add(track);
                    }
                }

                if (root.TryGetProperty("queue", out JsonElement queue) && queue.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in queue.EnumerateArray())
                    {
                        Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("netease", $"读 {name} 失败", ex);
            }
        }
        _loadedAt = DateTime.Now;
        Log.Write("netease", $"本地曲目索引: {_entries.Count} 条（playingList/fmPlay）");
    }

    private void Add(JsonElement track)
    {
        if (track.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        string title = track.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        string artist = "";
        if (track.TryGetProperty("artists", out JsonElement artists) && artists.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement a in artists.EnumerateArray())
            {
                artist = a.TryGetProperty("name", out JsonElement an) ? an.GetString() ?? "" : "";
                break;
            }
        }

        long durationMs = 0;
        if (track.TryGetProperty("duration", out JsonElement d))
        {
            durationMs = d.ValueKind switch
            {
                JsonValueKind.Number => d.GetInt64(),
                JsonValueKind.String => long.TryParse(d.GetString(), out long parsed) ? parsed : 0,
                _ => 0,
            };
        }

        if (durationMs < 1000 || durationMs > 24 * 3600 * 1000)
        {
            return;
        }

        // 曲目 id：本地 JSON 里是字符串（形如 "535056564"），取歌词时按它精确命中
        string id = "";
        if (track.TryGetProperty("id", out JsonElement idElement))
        {
            id = idElement.ValueKind switch
            {
                JsonValueKind.String => idElement.GetString() ?? "",
                JsonValueKind.Number => idElement.GetRawText(),
                _ => "",
            };
        }

        _entries.Add(new Entry(title, artist, TimeSpan.FromMilliseconds(durationMs), id));
    }

    /// <summary>按 SMTC 给的曲名/歌手查（时长 + 曲目 id）。查不到返回 null。</summary>
    public Match? Lookup(string title, string artist)
    {
        if (_entries.Count == 0 || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        string wantedTitle = Normalize(title);
        string wantedArtist = Normalize(artist);

        // 1) 曲名 + 歌手都命中
        foreach (Entry entry in _entries)
        {
            if (Normalize(entry.Title) == wantedTitle
                && (wantedArtist.Length == 0 || Normalize(entry.Artist) == wantedArtist))
            {
                return new Match(entry.Duration, entry.Id);
            }
        }

        // 2) 只按曲名命中（歌手上报不一致的播放器不少）
        foreach (Entry entry in _entries)
        {
            if (Normalize(entry.Title) == wantedTitle)
            {
                return new Match(entry.Duration, entry.Id);
            }
        }

        // 3) 互为子串（SMTC 有时带后缀，如 "(Live)" / "- 现场版"）
        if (wantedTitle.Length >= 4)
        {
            foreach (Entry entry in _entries)
            {
                string candidate = Normalize(entry.Title);
                if (candidate.Length >= 4
                    && (candidate.Contains(wantedTitle, StringComparison.Ordinal)
                        || wantedTitle.Contains(candidate, StringComparison.Ordinal)))
                {
                    return new Match(entry.Duration, entry.Id);
                }
            }
        }

        return null;
    }

    private static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }
        return new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
    }
}
