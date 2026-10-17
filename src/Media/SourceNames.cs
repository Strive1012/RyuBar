namespace RyuBar.Media;

/// <summary>
/// 把播放器的进程/AUMID 名字换成人能看懂的来源名。
///
/// SMTC 只给 `SourceAppUserModelId`（例如 `cloudmusic.exe`、
/// `Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic`），直接显示会很难看。
/// </summary>
public static class SourceNames
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["QQMusic.exe"] = "QQ音乐",
        ["QQMusic"] = "QQ音乐",
        ["cloudmusic.exe"] = "网易云音乐",
        ["cloudmusic"] = "网易云音乐",
        ["KuGou.exe"] = "酷狗音乐",
        ["kugou"] = "酷狗音乐",
        ["chrome.exe"] = "Chrome",
        ["chrome"] = "Chrome",
        ["msedge.exe"] = "Edge",
        ["msedge"] = "Edge",
        ["firefox.exe"] = "Firefox",
        ["Spotify.exe"] = "Spotify",
        ["PotPlayerMini64.exe"] = "PotPlayer",
        ["vlc.exe"] = "VLC",
        ["ApplicationFrameHost.exe"] = "系统应用",
        ["Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic"] = "媒体播放器",
        ["Microsoft.ZuneMusic"] = "媒体播放器",
        ["Microsoft.Media.Player.exe"] = "媒体播放器",
    };

    /// <summary>给出友好名字；没登记过就退回去掉扩展名的原名。</summary>
    public static string Friendly(string sourceId, string fallbackTitle = "")
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return string.IsNullOrWhiteSpace(fallbackTitle) ? "未知来源" : fallbackTitle;
        }

        if (Known.TryGetValue(sourceId, out string? known))
        {
            return known;
        }

        // AUMID 形如 `Foo.Bar_hash!Foo.Bar`：先看整体，再取 '!' 后面的部分
        if (Known.TryGetValue(sourceId, out known))
        {
            return known;
        }
        int bang = sourceId.IndexOf('!');
        if (bang >= 0 && bang + 1 < sourceId.Length)
        {
            string tail = sourceId[(bang + 1)..];
            if (Known.TryGetValue(tail, out known))
            {
                return known;
            }
            return Trim(tail);
        }

        return Trim(sourceId);
    }

    /// <summary>
    /// 从 SMTC 的 `SourceAppUserModelId` 取出**可执行文件名**（不带 `.exe` ✓，给 `Process.GetProcessesByName` 用 ✓）。
    /// 例：`cloudmusic.exe` → `cloudmusic`；`QQMusic.exe` → `QQMusic`
    /// UWP 包（`Foo.Bar_hash!Foo.Bar`）没有可激活的独立 exe ⇒ 返回 null ✓
    /// 解析不出来也返回 null ✓ —— 调用方据此**跳过**（用户要求"打开已运行的，不是新开"✓ 所以绝不启动新实例 ✗）
    /// </summary>
    public static string? ExecutableName(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return null;
        }

        string id = sourceId.Trim();

        // UWP 包：`Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic`
        // 它跑在 ApplicationFrameHost 里，没有"自己的窗口"可激活 ⇒ 交给调用方跳过 ✓
        if (id.Contains('!'))
        {
            return null;
        }

        int underscore = id.IndexOf('_');
        if (underscore > 0)
        {
            id = id[..underscore];     // 去掉 AUMID 的哈希后缀
        }

        if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            id = id[..^4];
        }

        return id.Length == 0 ? null : id;
    }

    private static string Trim(string value)
    {
        string result = value;
        int underscore = result.IndexOf('_');
        if (underscore > 0)
        {
            result = result[..underscore];     // 去掉 AUMID 的哈希后缀
        }
        if (result.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            result = result[..^4];
        }
        int dot = result.LastIndexOf('.');
        if (dot > 0 && dot < result.Length - 1)
        {
            result = result[(dot + 1)..];       // 包名取最后一段
        }
        return string.IsNullOrWhiteSpace(result) ? "未知来源" : result;
    }
}
