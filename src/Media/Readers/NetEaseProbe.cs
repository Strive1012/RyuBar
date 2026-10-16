using System.IO;
using System.Text;
using System.Text.Json;

namespace RyuBar.Media.Readers;

/// <summary>
/// 侦察工具（诊断用）：搞清楚网易云把"进度/时长"放在自己模块的哪里。
///
/// 方法完全不依赖版本相关的固定偏移 —— 自校准：
///   1. 在 cloudmusic.dll 的可读数据里收集所有落在合理区间（30..7200 秒）的 double；
///   2. 等几秒再读一次，**数值增加了约 N 秒**的那个就是播放位置；
///   3. 位置附近**不变、且不小于位置**的那个 double 就是时长。
/// </summary>
internal static class NetEaseProbe
{
    private const double MinPlausible = 30;
    private const double MaxPlausible = 7200;

    public static List<string> Run()
    {
        List<string> lines = new();

        using ModuleMemoryScanner? scanner = ModuleMemoryScanner.TryOpen("cloudmusic", "cloudmusic.dll");
        if (scanner == null)
        {
            lines.Add("打不开 cloudmusic/cloudmusic.dll（可能：客户端没运行 / 它以管理员权限运行 / 模块名变了）");
            return lines;
        }

        lines.Add($"进程 pid={scanner.ProcessId}  模块 {scanner.ModuleName}");
        lines.Add($"模块基址 0x{scanner.ModuleBase:X}  大小 {scanner.ModuleSize / 1024 / 1024} MB  " +
                  $"可读区域 {scanner.Regions.Count} 个，共 {scanner.TotalBytes / 1024 / 1024} MB  本进程 64 位={Environment.Is64BitProcess}");

        lines.Add("");
        lines.Add("本地曲目元数据（客户端自己的 JSON，用来交叉验证时长）:");
        foreach (string metadataLine in ReadLocalMetadata())
        {
            lines.Add("  " + metadataLine);
        }

        // 第一遍：收集候选 double
        List<(ulong Address, double Value)> candidates = new();
        foreach ((ulong start, ulong size) in scanner.Regions)
        {
            byte[]? buffer = scanner.ReadRegion(start, size);
            if (buffer == null)
            {
                continue;
            }
            for (int offset = 0; offset + 8 <= buffer.Length; offset += 8)
            {
                double value = BitConverter.ToDouble(buffer, offset);
                if (value >= MinPlausible && value <= MaxPlausible)
                {
                    candidates.Add((start + (ulong)offset, value));
                }
            }
        }
        lines.Add("");
        lines.Add($"第一次扫描：命中 {candidates.Count} 个处在 {MinPlausible}..{MaxPlausible} 秒的 double");

        if (candidates.Count == 0)
        {
            return lines;
        }

        const int WaitSeconds = 4;
        lines.Add($"等待 {WaitSeconds} 秒后复查（正在播放时，位置那个数应该增加约 {WaitSeconds}）...");
        Thread.Sleep(WaitSeconds * 1000);

        // 第二遍：按区域整块重读，只在区域内部比较（避免逐个地址调 ReadProcessMemory）
        Dictionary<ulong, byte[]> cache = new();
        List<(ulong Address, double Before, double After)> moved = new();
        foreach ((ulong address, double before) in candidates)
        {
            ulong regionStart = address & ~0xFFFUL;
            // 落在同一 4KB 页内的按页读；命中很少时这样足够
            if (!cache.TryGetValue(regionStart, out byte[]? page))
            {
                page = scanner.ReadRegion(regionStart, 4096);
                if (page == null)
                {
                    continue;
                }
                cache[regionStart] = page;
                if (cache.Count > 60000)
                {
                    cache.Clear();   // 别把内存吃爆
                }
            }
            int offsetInPage = (int)(address - regionStart);
            if (offsetInPage + 8 > page.Length)
            {
                continue;
            }
            double after = BitConverter.ToDouble(page, offsetInPage);
            double delta = after - before;
            if (delta >= WaitSeconds * 0.7 && delta <= WaitSeconds * 1.4)
            {
                moved.Add((address, before, after));
            }
        }

        lines.Add($"数值按时间推进的候选（可能是播放位置）：{moved.Count} 个");
        foreach ((ulong address, double before, double after) in moved.Take(12))
        {
            lines.Add($"  0x{address:X}  模块内偏移 +0x{address - scanner.ModuleBase:X}  {before:0.000} -> {after:0.000}");
        }

        // 位置附近找"不变且不小于位置"的 double = 时长
        lines.Add("");
        lines.Add("位置附近的 double 窗口（两次读数：变化的是位置，不变的是候选时长）:");
        if (moved.Count > 0)
        {
            ulong anchor = moved[0].Address;
            List<ulong> addresses = new();
            for (long offset = -0x100; offset <= 0x400; offset += 8)
            {
                ulong address = (ulong)((long)anchor + offset);
                if (address >= scanner.ModuleBase && address + 8 <= scanner.ModuleBase + scanner.ModuleSize)
                {
                    addresses.Add(address);
                }
            }

            // 逐个地址读：整段读一旦跨页/跨区域就会整体失败
            Dictionary<ulong, double?> first = new();
            foreach (ulong address in addresses)
            {
                first[address] = scanner.ReadDouble(address);
            }
            Thread.Sleep(3000);
            foreach (ulong address in addresses)
            {
                double? a = first[address];
                double? b = scanner.ReadDouble(address);
                if (a is not double av || b is not double bv)
                {
                    continue;
                }
                bool changed = Math.Abs(bv - av) > 0.05;
                bool plausible = av >= 0 && av <= MaxPlausible;
                if (!changed && !(plausible && av >= 30))
                {
                    continue;
                }
                lines.Add($"  0x{address:X} (+0x{address - scanner.ModuleBase:X}, 距位置 {(long)address - (long)anchor:+#;-#;0}) " +
                          $"= {av:0.000} -> {bv:0.000}  [{(changed ? "变化" : "不变")}]{(plausible ? "" : "  值不合理")}");
            }
        }

        return lines;
    }

    /// <summary>
    /// 读客户端自己写的播放列表 JSON（AF 的元数据也是从这里来的）。
    /// 只读文件，不写任何东西。
    /// </summary>
    private static List<string> ReadLocalMetadata()
    {
        List<string> lines = new();
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetEase", "CloudMusic", "WebData", "file");

        if (!Directory.Exists(root))
        {
            lines.Add($"目录不存在: {root}");
            return lines;
        }

        foreach (string name in new[] { "playingList", "fmPlay" })
        {
            string path = Path.Combine(root, name);
            if (!File.Exists(path))
            {
                lines.Add($"{name}: 不存在");
                continue;
            }
            try
            {
                using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using JsonDocument document = JsonDocument.Parse(stream);
                lines.Add($"{name}: {DescribeQueue(document.RootElement)}");
                lines.Add($"  原始片段: {RawSnippet(document.RootElement)}");
            }
            catch (Exception ex)
            {
                lines.Add($"{name}: 解析失败 {ex.Message}");
            }
        }
        return lines;
    }

    private static string RawSnippet(JsonElement root, int maxLength = 600)
    {
        string raw = root.GetRawText();
        return raw.Length <= maxLength ? raw : raw[..maxLength] + "...";
    }

    private static string DescribeQueue(JsonElement root)
    {
        StringBuilder text = new();
        // playingList: { "list": [ { "track": { "name": ..., "duration": ms, "artists": [ {name} ] } } ] }
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("list", out JsonElement list)
            && list.ValueKind == JsonValueKind.Array)
        {
            text.Append($"list 共 {list.GetArrayLength()} 首; ");
            foreach (JsonElement item in list.EnumerateArray().Take(3))
            {
                JsonElement track = item.TryGetProperty("track", out JsonElement t) ? t : item;
                text.Append(DescribeTrack(track));
            }
            return text.ToString();
        }

        // fmPlay: { "currentIndex": n, "queue": [ { "name": ..., "duration": ms } ] }
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("queue", out JsonElement queue)
            && queue.ValueKind == JsonValueKind.Array)
        {
            text.Append($"queue 共 {queue.GetArrayLength()} 首; ");
            foreach (JsonElement item in queue.EnumerateArray().Take(3))
            {
                text.Append(DescribeTrack(item));
            }
            return text.ToString();
        }

        text.Append("结构不认识");
        return text.ToString();
    }

    private static string DescribeTrack(JsonElement track)
    {
        string name = track.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "?" : "?";
        string artist = "?";
        if (track.TryGetProperty("artists", out JsonElement artists) && artists.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement a in artists.EnumerateArray())
            {
                artist = a.TryGetProperty("name", out JsonElement an) ? an.GetString() ?? "?" : "?";
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
        return $"[{name} / {artist} {durationMs / 1000.0:0.0}s] ";
    }
}
