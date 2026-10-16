using System.IO;
using System.Text.Json;
using RyuBar.Diagnostics;

namespace RyuBar.Lyrics;

/// <summary>
/// 歌词的磁盘缓存：跨进程保留"曲目 id"与"原始歌词文本"。
///
/// 为什么需要：一首歌的第一次加载必须走网络（搜索 ≈100ms + 取词 ≈100ms，实测热连接下限），
/// 但只要缓存住，"同一首歌再放一次"就能 **0 网络** 直接出词（用户感知的"慢"主要来自重复加载）。
/// 单文件 JSON，容量上限防止无限增长；读写都在后台线程，出错只记日志、绝不影响播放。
/// </summary>
internal static class LyricsDiskCache
{
    private const int MaxEntries = 300;

    private static readonly object Gate = new();
    private static Dictionary<string, string>? _ids;
    private static Dictionary<string, string>? _raw;
    private static List<string> _order = new();

    private sealed class Store
    {
        public Dictionary<string, string> Ids { get; set; } = new();

        public Dictionary<string, string> Raw { get; set; } = new();

        public List<string> RawOrder { get; set; } = new();
    }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RyuBar",
        "lyrics-cache.json");

    private static void EnsureLoaded()
    {
        if (_ids != null && _raw != null)
        {
            return;
        }

        try
        {
            if (File.Exists(FilePath))
            {
                Store? store = JsonSerializer.Deserialize<Store>(File.ReadAllText(FilePath));
                if (store != null)
                {
                    _ids = store.Ids;
                    _raw = store.Raw;
                    _order = store.RawOrder;
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("lyrics", "读歌词缓存失败（忽略）: " + ex.Message);
        }

        _ids = new Dictionary<string, string>(StringComparer.Ordinal);
        _raw = new Dictionary<string, string>(StringComparer.Ordinal);
        _order = new List<string>();
    }

    public static bool TryGetId(string key, out string value)
    {
        lock (Gate)
        {
            EnsureLoaded();
            return _ids!.TryGetValue(key, out value!);
        }
    }

    public static void SaveId(string key, string value)
    {
        lock (Gate)
        {
            EnsureLoaded();
            _ids![key] = value;
            Save();
        }
    }

    public static bool TryGetRaw(string key, out string value)
    {
        lock (Gate)
        {
            EnsureLoaded();
            return _raw!.TryGetValue(key, out value!);
        }
    }

    /// <summary>丢弃一条歌词原文缓存（用于"解析不出行"的废数据）。</summary>
    public static void RemoveRaw(string key)
    {
        lock (Gate)
        {
            EnsureLoaded();
            if (_raw!.Remove(key))
            {
                _order.Remove(key);
                Save();
            }
        }
    }

    public static void SaveRaw(string key, string value)
    {
        lock (Gate)
        {
            EnsureLoaded();
            if (!_raw!.ContainsKey(key))
            {
                _order.Add(key);
            }
            _raw[key] = value;

            // 超容量就按写入顺序丢最早的
            while (_order.Count > MaxEntries)
            {
                _raw.Remove(_order[0]);
                _order.RemoveAt(0);
            }
            Save();
        }
    }

    private static void Save()
    {
        try
        {
            string directory = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(directory);
            Store store = new()
            {
                Ids = _ids!,
                Raw = _raw!,
                RawOrder = _order,
            };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(store));
        }
        catch (Exception ex)
        {
            Log.Write("lyrics", "写歌词缓存失败（忽略）: " + ex.Message);
        }
    }
}
