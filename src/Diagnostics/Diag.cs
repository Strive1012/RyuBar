using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RyuBar.Diagnostics;

/// <summary>
/// `--diag` 的实现：不依赖运行中的实例，独立把"任务栏、占用区、选点"这条链路跑一遍。
/// 出问题时它是唯一能解释"条为什么放在这儿"的东西。
/// </summary>
public static class Diag
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    public static void Run() => Run(false, false, false, false);

    /// <param name="seekProbe">
    /// 额外做一次"跳转探测"：不管播放器是否声明支持定位，都试着跳到 50%，
    /// 然后再读一次时间轴，看位置是否真的动了。用来判断 SMTC 的能力位是否可信。
    /// </param>
    /// <param name="neteaseProbe">额外做一次网易云内存侦察（自校准扫描，找出进度/时长所在地址）。</param>
    /// <param name="lyricApi">额外反射列出歌词库的公开 API。</param>
    /// <param name="lyricsProbe">额外实测一次歌词拉取链路。</param>
    public static void Run(bool seekProbe, bool neteaseProbe, bool lyricApi, bool lyricsProbe)
    {
        try { AttachConsole(-1); } catch { }

        StringBuilder report = new();
        void Line(string text = "")
        {
            report.AppendLine(text);
        }

        Line($"RyuBar 诊断  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Line($"进程: {Environment.ProcessId}  .NET {Environment.Version}");
        Line();

        Taskbar.TaskbarSample sample = Taskbar.TaskbarSampler.Take();
        if (!sample.Found)
        {
            Line("任务栏: 未找到 Shell_TrayWnd");
        }
        else
        {
            Line($"任务栏: 0x{sample.Handle.ToInt64():X}  class={Taskbar.TaskbarLocator.ClassNameOf(sample.Handle)}  pid={Taskbar.TaskbarLocator.ProcessIdOf(sample.Handle)}");
            Line($"  窗口矩形(屏幕): {sample.WindowRect}");
            Line($"  客户区(屏幕)  : {sample.ClientScreenRect}");
            Line($"  显示器矩形    : {sample.MonitorRect}");
            Line($"  DPI 缩放      : {sample.Scale:0.###}");
            Line($"  是否隐藏      : {sample.Hidden}");
            Line();

            Taskbar.ProbeResult probe = Taskbar.OccupancyProbe.ScanOnce(
                sample.Handle,
                Taskbar.TaskbarLocator.ProcessIdOf(sample.Handle),
                sample.ClientHeight,
                sample.Scale);

            Line($"占用区探测: source={probe.Source}  {probe.Detail}");
            foreach (Taskbar.OccupiedRange range in probe.Ranges)
            {
                int left = range.Left - sample.ClientScreenRect.Left;
                int right = range.Right - sample.ClientScreenRect.Left;
                Line($"  [客户区 {left,5} .. {right,5}] 宽 {right - left,5}  {range.Name}");
            }
            Line();

            double scale = sample.Scale;
            int edge = (int)Math.Round(8 * scale);
            int gap = (int)Math.Round(12 * scale);
            int desired = (int)Math.Round(420 * scale);
            int minimum = (int)Math.Round(150 * scale);

            List<Taskbar.OccupiedRange> local = new();
            foreach (Taskbar.OccupiedRange range in probe.Ranges)
            {
                local.Add(new Taskbar.OccupiedRange(range.Left - sample.ClientScreenRect.Left, range.Right - sample.ClientScreenRect.Left, range.Name));
            }

            Taskbar.RangePolicy.Result choice = Taskbar.RangePolicy.Choose(local, sample.ClientWidth, desired, edge, gap, minimum);
            Line($"选点: found={choice.Found} left={choice.Left} width={choice.Width} reason={choice.Reason}");
            Line($"  参数: clientWidth={sample.ClientWidth} desired={desired} minimum={minimum} edge={edge} gap={gap}");
        }

        Line();
        Line("媒体会话 (SMTC):");
        foreach (string mediaLine in ReadMedia())
        {
            Line("  " + mediaLine);
        }

        if (seekProbe)
        {
            Line();
            Line("跳转探测（不看能力位，直接试）:");
            foreach (string probeLine in SeekProbe())
            {
                Line("  " + probeLine);
            }
        }

        if (neteaseProbe)
        {
            Line();
            Line("网易云内存侦察:");
            foreach (string probeLine in Media.Readers.NetEaseProbe.Run())
            {
                Line("  " + probeLine);
            }
        }

        if (lyricApi)
        {
            Line();
            Line("歌词库 API:");
            foreach (string apiLine in DumpLyricApi())
            {
                Line(apiLine);
            }
        }

        if (lyricsProbe)
        {
            Line();
            Line("歌词拉取实测:");
            foreach (string probeLine in LyricsProbe())
            {
                Line("  " + probeLine);
            }
        }

        Line();
        string settingsPath = Path.Combine(Log.Directory, "..", "settings.json");
        if (File.Exists(settingsPath))
        {
            Line("设置 (settings.json):");
            Line(File.ReadAllText(settingsPath));
        }
        else
        {
            Line("设置: 还没有 settings.json（首次运行会生成默认值）");
        }

        Line();
        string statePath = Path.Combine(Log.Directory, "..", "state.json");
        if (File.Exists(statePath))
        {
            Line("运行中实例的状态 (state.json):");
            Line(File.ReadAllText(statePath));
        }
        else
        {
            Line("运行中实例的状态: 没有 state.json（实例可能没在跑）");
        }

        string text = report.ToString();
        Console.WriteLine(text);
        Console.Out.Flush();

        try
        {
            string output = Path.Combine(Path.GetTempPath(), "RyuBar-diag.txt");
            File.WriteAllText(output, text, Encoding.UTF8);
            Console.WriteLine("已写入: " + output);
        }
        catch
        {
        }
    }

    /// <summary>
    /// 读一次 SMTC。刻意放到后台线程再阻塞等待：WinRT 的异步续体可能回到调用线程，
    /// 在 UI 线程上直接 GetResult() 有死锁风险。
    /// </summary>
    private static IReadOnlyList<string> SeekProbe()
    {
        try
        {
            return Task.Run(SeekProbeCore).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return new[] { "探测失败: " + ex.Message };
        }
    }

    private static List<string> SeekProbeCore()
    {
        List<string> lines = new();
        try
        {
            Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager manager =
                Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().GetAwaiter().GetResult();
            Windows.Media.Control.GlobalSystemMediaTransportControlsSession? session = manager.GetCurrentSession();
            if (session == null)
            {
                lines.Add("当前没有会话");
                return lines;
            }

            Windows.Media.Control.GlobalSystemMediaTransportControlsSessionTimelineProperties before = session.GetTimelineProperties();
            TimeSpan duration = before.EndTime - before.StartTime;
            Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus status =
                session.GetPlaybackInfo().PlaybackStatus;
            lines.Add($"跳转前: 位置 {before.Position:hh\\:mm\\:ss}  时长 {duration:hh\\:mm\\:ss}  能力位={session.GetPlaybackInfo().Controls?.IsPlaybackPositionEnabled}  状态={status}");

            if (duration <= TimeSpan.Zero)
            {
                lines.Add("时长为 0，无法探测");
                return lines;
            }

            // 暂停状态下很多播放器不刷新时间轴，先让它播起来再测
            if (status != Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                bool started = session.TryPlayAsync().AsTask().GetAwaiter().GetResult();
                lines.Add($"先尝试开始播放（暂停态下时间轴可能不刷新）: {started}");
                Thread.Sleep(1500);
            }

            long target = (before.StartTime + TimeSpan.FromSeconds(duration.TotalSeconds * 0.5)).Ticks;
            bool accepted = session.TryChangePlaybackPositionAsync(target).AsTask().GetAwaiter().GetResult();
            lines.Add($"TryChangePlaybackPositionAsync 返回值: {accepted}  目标 {TimeSpan.FromTicks(target):hh\\:mm\\:ss}");

            // 播放器刷新时间轴有延迟，多读几次
            for (int i = 0; i < 6; i++)
            {
                Thread.Sleep(700);
                Windows.Media.Control.GlobalSystemMediaTransportControlsSessionTimelineProperties sample = session.GetTimelineProperties();
                double movedNow = Math.Abs((sample.Position - before.Position).TotalSeconds);
                lines.Add($"  第 {i + 1} 次读（{(i + 1) * 0.7:0.0}s 后）: 位置 {sample.Position:hh\\:mm\\:ss}  变化 {movedNow:0.0} 秒");
                if (movedNow >= 5)
                {
                    break;
                }
            }

            Windows.Media.Control.GlobalSystemMediaTransportControlsSessionTimelineProperties after = session.GetTimelineProperties();
            double moved = Math.Abs((after.Position - before.Position).TotalSeconds);
            lines.Add(moved >= 5
                ? "结论: 跳转生效 —— 进度条点击/拖动在这类播放器上可用"
                : "结论: 位置没有明显移动（可能是播放器不刷新时间轴，或确实不支持）");
        }
        catch (Exception ex)
        {
            lines.Add("探测异常: " + ex.Message);
        }
        return lines;
    }

    /// <summary>反射列出 Lyricify 歌词库的公开 API（写代码前先看清真实接口，不靠猜）。</summary>
    private static IReadOnlyList<string> DumpLyricApi()
    {
        List<string> lines = new();
        System.Reflection.Assembly? target = null;
        foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            string? name = assembly.GetName().Name;
            if (name != null && name.Contains("Lyric", StringComparison.OrdinalIgnoreCase))
            {
                target = assembly;
                break;
            }
        }

        if (target == null)
        {
            try
            {
                target = System.Reflection.Assembly.Load("Lyricify.Lyrics.Helper");
            }
            catch (Exception ex)
            {
                lines.Add("没有加载到歌词库: " + ex.Message);
                return lines;
            }
        }

        lines.Add($"程序集: {target.GetName().Name} {target.GetName().Version}");

        Type[] types;
        try
        {
            types = target.GetExportedTypes();
        }
        catch (Exception ex)
        {
            lines.Add("枚举类型失败: " + ex.Message);
            return lines;
        }

        lines.Add("---- 全部公开类型与枚举取值 ----");
        foreach (Type type in types.OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            if (type.IsEnum)
            {
                lines.Add($"enum {type.FullName} = " + string.Join(" | ", Enum.GetNames(type)));
            }
            else
            {
                lines.Add($"{(type.IsInterface ? "interface" : "class")} {type.FullName}");
            }
        }

        lines.Add("---- 关键类的静态方法 ----");
        foreach (Type type in types
            .Where(t => t.Name is "SearchHelper" or "ProviderHelper" or "ParseHelper" or "GenerateHelper"
                or "LyricsSearchHelper" or "OptimizationHelper")
            .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            lines.Add($"  {type.FullName}");
            foreach (System.Reflection.MethodInfo method in type
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName)
                .OrderBy(m => m.Name))
            {
                string parameterList = string.Join(", ", method.GetParameters()
                    .Select(p => $"{Simple(p.ParameterType)} {p.Name}"));
                lines.Add($"      {Simple(method.ReturnType)} {method.Name}({parameterList})");
            }
        }

        lines.Add("---- 模型类型（Lyricify.Lyrics.Models / Interfaces）的公开成员 ----");
        foreach (Type type in types
            .Where(t => (t.Namespace?.Contains("Models") ?? false) || (t.Namespace?.Contains("Interfaces") ?? false))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .Take(40))
        {
            string members = string.Join(", ",
                type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                    .Select(p => $"{Simple(p.PropertyType)} {p.Name}"));
            lines.Add($"  {type.FullName}: {members}");
        }

        lines.Add("---- ILineInfo 及其实现 / FileInfo / 关键方法 ----");
        foreach (Type type in types
            .Where(t => t.Name is "ILineInfo" or "SyllableLineInfo" or "FileInfo" or "ISearchResult" or "ISearcher")
            .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            lines.Add($"  {(type.IsInterface ? "interface" : "class")} {type.FullName}");
            foreach (System.Reflection.PropertyInfo property in type.GetProperties(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                lines.Add($"      {Simple(property.PropertyType)} {property.Name}");
            }
            foreach (System.Reflection.MethodInfo method in type.GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName))
            {
                lines.Add($"      {Simple(method.ReturnType)} {method.Name}(" +
                          string.Join(", ", method.GetParameters().Select(p => $"{Simple(p.ParameterType)} {p.Name}")) + ")");
            }
        }

        lines.Add("---- 搜索器/提供者的全部公开成员（含继承） ----");
        foreach (Type type in types
            .Where(t => (t.Namespace ?? "").StartsWith("Lyricify.Lyrics.Searchers")
                || (t.Namespace ?? "") == "Lyricify.Lyrics.Providers")
            .Where(t => t.IsInterface || t.Name.Contains("Helper", StringComparison.Ordinal)
                || t.Name.Contains("SearchResult", StringComparison.Ordinal) || t.Name.Contains("Provider", StringComparison.Ordinal))
            .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            lines.Add($"  {(type.IsInterface ? "interface" : "class")} {type.FullName}");
            foreach (System.Reflection.MethodInfo method in type
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.Instance)
                .Where(m => !m.IsSpecialName)
                .OrderBy(m => m.Name))
            {
                lines.Add($"      {Simple(method.ReturnType)} {method.Name}(" +
                          string.Join(", ", method.GetParameters().Select(p => $"{Simple(p.ParameterType)} {p.Name}")) + ")");
            }
            foreach (System.Reflection.PropertyInfo property in type.GetProperties(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                lines.Add($"      prop {Simple(property.PropertyType)} {property.Name}");
            }
            foreach (System.Reflection.FieldInfo field in type.GetFields(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
            {
                lines.Add($"      field {Simple(field.FieldType)} {field.Name} = {TryFieldValue(field, type)}");
            }
        }

        return lines;
    }

    private static string TryFieldValue(System.Reflection.FieldInfo field, Type declaring)
    {
        try
        {
            if (!field.IsStatic)
            {
                return "(实例字段)";
            }
            object? value = field.GetValue(null);
            return value switch
            {
                null => "null",
                string text => $"\"{text}\"",
                _ => value.GetType().Name,
            };
        }
        catch
        {
            return "?";
        }
    }

    private static string Simple(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }
        string name = type.Name[..type.Name.IndexOf('`')];
        return name + "<" + string.Join(",", type.GetGenericArguments().Select(Simple)) + ">";
    }

    /// <summary>实测歌词链路：当前曲目 → 网易云 id → 取词 → 解析（含逐字能力判断）。</summary>
    private static List<string> LyricsProbe()
    {
        List<string> lines = new();
        try
        {
            Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager manager =
                Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().GetAwaiter().GetResult();
            Windows.Media.Control.GlobalSystemMediaTransportControlsSession? session = manager.GetCurrentSession();
            if (session == null)
            {
                lines.Add("当前没有媒体会话");
                return lines;
            }

            Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties properties =
                session.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
            Windows.Media.Control.GlobalSystemMediaTransportControlsSessionTimelineProperties timeline = session.GetTimelineProperties();

            string title = properties.Title ?? "";
            string artist = properties.Artist ?? "";
            string album = properties.AlbumTitle ?? "";
            int durationMs = (int)(timeline.EndTime - timeline.StartTime).TotalMilliseconds;
            lines.Add($"当前曲目: {title} / {artist} / {album}  时长 {durationMs} ms");

            Media.Readers.NetEaseMetadataIndex index = new();
            index.Refresh();
            string songId = "";
            if (index.Lookup(title, artist) is { } match)
            {
                songId = match.Id;
                lines.Add($"本地 JSON 命中: 曲目 id={songId}  时长={match.Duration.TotalSeconds:0.0}s  索引共 {index.Count} 条");
            }
            else
            {
                lines.Add($"本地 JSON 没命中（当前来源不是网易云时属正常）  索引共 {index.Count} 条");
            }

            if (string.IsNullOrEmpty(songId))
            {
                lines.Add("本地 JSON 没命中 → 走搜索兜底（下面看搜索结果里有什么可用的 id）");
                try
                {
                    Lyricify.Lyrics.Models.TrackMetadata metadata = new()
                    {
                        Title = title,
                        Artist = artist,
                        Album = album,
                        DurationMs = durationMs > 0 ? durationMs : null,
                    };
                    Lyricify.Lyrics.Searchers.ISearchResult? found =
                        Task.Run(() => Lyricify.Lyrics.Helpers.SearchHelper.Search(
                                metadata,
                                Lyricify.Lyrics.Searchers.Searchers.Netease,
                                Lyricify.Lyrics.Searchers.Helpers.CompareHelper.MatchType.Medium))
                            .GetAwaiter().GetResult();

                    if (found == null)
                    {
                        lines.Add("搜索没有结果");
                    }
                    else
                    {
                        lines.Add($"搜索命中类型: {found.GetType().FullName}");
                        foreach (System.Reflection.PropertyInfo property in found.GetType().GetProperties(
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                        {
                            string value;
                            try
                            {
                                value = property.GetValue(found)?.ToString() ?? "null";
                            }
                            catch
                            {
                                value = "?";
                            }
                            if (value.Length > 60)
                            {
                                value = value[..60] + "…";
                            }
                            lines.Add($"    prop {property.Name} = {value}");
                        }
                        foreach (System.Reflection.FieldInfo field in found.GetType().GetFields(
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                        {
                            string value;
                            try
                            {
                                value = field.GetValue(found)?.ToString() ?? "null";
                            }
                            catch
                            {
                                value = "?";
                            }
                            if (value.Length > 60)
                            {
                                value = value[..60] + "…";
                            }
                            lines.Add($"    field {field.Name} = {value}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    lines.Add("搜索兜底失败: " + ex.Message);
                }

                return lines;
            }

            string? raw = Lyrics.LyricsService.FetchNetEaseAsync(songId).GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(raw))
            {
                lines.Add("取词失败：接口没返回歌词");
                return lines;
            }
            lines.Add($"取到原始歌词 {raw.Length} 字符");

            Lyrics.LyricTrack? track = Lyrics.LyricsService.Build(raw, title, artist, out string note);
            lines.Add("解析: " + note);
            if (track == null || !track.HasLines)
            {
                return lines;
            }

            int withSyllables = track.Lines.Count(l => l.HasSyllables);
            lines.Add($"逐字行数: {withSyllables} / {track.Lines.Count}" + (withSyllables > 0 ? "  → 可以逐字高亮" : "  → 只有行级，按设计不做逐字"));

            foreach (Lyrics.LyricLine line in track.Lines.Take(6))
            {
                string syllables = line.HasSyllables ? $"  音节 {line.Syllables!.Count}" : "";
                lines.Add($"  [{line.StartMs / 1000.0,7:0.00}s - {line.EndMs / 1000.0,7:0.00}s] {line.Text}{syllables}");
            }

            // ① 逐字时间轴到底是"绝对时间"还是"行内相对时间"（决定逐字高亮同步与否）
            Lyrics.LyricLine? withSyllableLine = track.Lines.FirstOrDefault(l => l.HasSyllables);
            if (withSyllableLine?.Syllables is { Count: > 0 } syllablesSample)
            {
                lines.Add($"逐字时间轴抽样（行起点 {withSyllableLine.StartMs}ms，行终点 {withSyllableLine.EndMs}ms）:");
                int syllableIndex = 0;
                foreach (Lyrics.LyricSyllable syllable in syllablesSample)
                {
                    lines.Add($"    #{syllableIndex} 「{syllable.Text}」 {syllable.StartMs}ms - {syllable.EndMs}ms");
                    syllableIndex++;
                    if (syllableIndex >= 12)
                    {
                        break;
                    }
                }
                int lastStart = syllablesSample[^1].StartMs;
                lines.Add($"    末字起点 {lastStart}ms " + (lastStart <= withSyllableLine.EndMs + 2000 ? "（落在行内 → 绝对时间 ✓）" : "（远超行尾 → 疑似行内相对时间 ✗）"));
            }

            // ② 歌词文本里自带换行的行（源里一行写了两句，必须当成两行显示）
            int embedded = track.Lines.Count(l => l.Text.Contains('\n'));
            lines.Add($"文本内含换行的行数: {embedded} / {track.Lines.Count}");
            foreach (Lyrics.LyricLine line in track.Lines.Where(l => l.Text.Contains('\n')).Take(3))
            {
                lines.Add($"    行起点 {line.StartMs}ms 内容: " + line.Text.Replace("\n", " ⏎ ").Replace("\r", ""));
            }

            // ③ 同一时刻/近似时刻开头的多行（行结构可能被合并或漏掉）
            int sameTime = 0;
            for (int i = 1; i < track.Lines.Count; i++)
            {
                if (Math.Abs(track.Lines[i].StartMs - track.Lines[i - 1].StartMs) <= 1)
                {
                    sameTime++;
                    if (sameTime <= 3)
                    {
                        lines.Add($"    同一时刻 {track.Lines[i].StartMs}ms: 「{track.Lines[i - 1].Text}」 / 「{track.Lines[i].Text}」");
                    }
                }
            }
            lines.Add($"同一时刻并列的行数: {sameTime}");
        }
        catch (Exception ex)
        {
            lines.Add("异常: " + ex.Message);
        }
        return lines;
    }

    private static IReadOnlyList<string> ReadMedia()
    {
        try
        {
            return Task.Run(ReadMediaCore).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return new[] { "读取失败: " + ex.Message };
        }
    }

    private static IReadOnlyList<string> ReadMediaCore()
    {
        List<string> lines = new();
        try
        {
            Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager manager =
                Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().GetAwaiter().GetResult();

            lines.Add($"会话数: {manager.GetSessions().Count}");

            // 所有来源（切换手势就是在这份列表里循环）
            Windows.Media.Control.GlobalSystemMediaTransportControlsSession? current = manager.GetCurrentSession();
            string currentKey = current?.SourceAppUserModelId ?? "";
            foreach (Windows.Media.Control.GlobalSystemMediaTransportControlsSession option in manager.GetSessions())
            {
                string key = option.SourceAppUserModelId ?? "";
                string status = "?";
                string title = "";
                try
                {
                    status = option.GetPlaybackInfo().PlaybackStatus.ToString();
                    title = option.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult().Title ?? "";
                }
                catch
                {
                }
                lines.Add($"  来源: {Media.SourceNames.Friendly(key),-12} [{status,-8}] {(key == currentKey ? "★当前" : "      ")}  {title}");
            }

            Windows.Media.Control.GlobalSystemMediaTransportControlsSession? session = current;
            if (session == null)
            {
                lines.Add("当前没有会话（播放器未注册 SMTC 时属正常）");
                return lines;
            }

            Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties properties =
                session.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
            Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackInfo playback = session.GetPlaybackInfo();
            Windows.Media.Control.GlobalSystemMediaTransportControlsSessionTimelineProperties timeline = session.GetTimelineProperties();

            lines.Add($"来源: {session.SourceAppUserModelId}");
            lines.Add($"标题: {properties.Title}");
            lines.Add($"歌手: {properties.Artist}    专辑: {properties.AlbumTitle}");
            lines.Add($"封面: {(properties.Thumbnail != null ? "有" : "无")}");
            lines.Add($"状态: {playback.PlaybackStatus}   速率: {playback.PlaybackRate:0.###}");
            lines.Add($"时间轴: 位置 {timeline.Position:hh\\:mm\\:ss} / 时长 {(timeline.EndTime - timeline.StartTime):hh\\:mm\\:ss}");
            try
            {
                DateTimeOffset updated = timeline.LastUpdatedTime;
                double age = updated == default ? -1 : (DateTimeOffset.Now - updated).TotalSeconds;
                lines.Add($"上报时刻: {updated:HH:mm:ss.fff}（距今 {age:0.00}s）  ← 插值锚点用这个，不是我们的采样时刻");
            }
            catch (Exception ex)
            {
                lines.Add("上报时刻: 读取失败 " + ex.Message);
            }
            lines.Add($"能力: 播放暂停={playback.Controls?.IsPlayPauseToggleEnabled} 上一首={playback.Controls?.IsPreviousEnabled} " +
                      $"下一首={playback.Controls?.IsNextEnabled} 跳转={playback.Controls?.IsPlaybackPositionEnabled}");
        }
        catch (Exception ex)
        {
            lines.Add("读取失败: " + ex.Message);
        }
        return lines;
    }
}
