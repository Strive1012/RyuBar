using RyuBar.Diagnostics;

namespace RyuBar.Media.Readers;

/// <summary>
/// 网易云的播放位置来源：在 <c>cloudmusic.dll</c> 里自校准地找出"播放位置"那个 double。
///
/// 为什么不照抄固定偏移：偏移跟播放器版本绑定，升级就失效。这里用的是**自校准** ——
///   1. 扫描模块可读数据，收集所有落在合理区间的 double（本机实测约 1.2 万个）；
///   2. 隔几秒再扫一遍，**数值正好增加了约 N 秒**的那个就是播放位置（实测唯一命中）；
///   3. 记住这个地址，之后每帧只读 8 字节 —— 便宜到可以随便读；
///   4. 值变得不合理（跑飞、跳变、超出曲目时长）就丢掉地址重新校准。
///
/// 校准放在后台线程：整个扫描约 1~2 秒，再加一次等待采样，不阻塞界面。
/// 读不到（客户端没开、以管理员运行、模块改名）就安静退回纯 SMTC。
/// </summary>
internal sealed class NetEaseMemoryReader : IDisposable
{
    private const string ProcessName = "cloudmusic";
    private const string ModuleName = "cloudmusic.dll";
    private const double MinPlausible = 1;
    private const double MaxPlausible = 24 * 3600;
    private const int CalibrationWaitSeconds = 3;

    private readonly object _gate = new();
    private readonly Configuration.SettingsStore? _settings;

    private ModuleMemoryScanner? _scanner;
    private ulong _positionAddress;
    private double _lastValue = double.NaN;
    private DateTime _lastValueAt = DateTime.MinValue;
    private Task? _calibrationTask;
    private bool _disposed;

    public NetEaseMemoryReader(Configuration.SettingsStore? settings = null)
    {
        _settings = settings;
    }

    /// <summary>状态描述，写日志与诊断用。</summary>
    public string Status { get; private set; } = "未校准";

    public bool IsReady
    {
        get
        {
            lock (_gate)
            {
                return _positionAddress != 0;
            }
        }
    }

    /// <summary>曲目/会话变了：丢掉当前地址，下次重新校准。</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _positionAddress = 0;
            _lastValue = double.NaN;
            Status = "会话变化，待重新校准";
        }
    }

    /// <summary>确保正在校准（幂等）。校准在后台线程进行。</summary>
    public void EnsureCalibrating()
    {
        if (_disposed)
        {
            return;
        }
        lock (_gate)
        {
            if (_positionAddress != 0 || _calibrationTask is { IsCompleted: false })
            {
                return;
            }
            _calibrationTask = Task.Run(Calibrate);
        }
    }

    private void Calibrate()
    {
        try
        {
            _scanner?.Dispose();
            _scanner = ModuleMemoryScanner.TryOpen(ProcessName, ModuleName);
            if (_scanner == null)
            {
                SetStatus("打不开 cloudmusic.dll（客户端未运行/以管理员运行/模块名不同）");
                return;
            }

            // 先用上次存下来的偏移：暂停状态下测不出"数值在动"，但存下来的偏移照样能读到位置
            if (TrySavedOffset())
            {
                return;
            }

            List<(ulong Address, double Value)> candidates = new();
            foreach ((ulong start, ulong size) in _scanner.Regions)
            {
                byte[]? buffer = _scanner.ReadRegion(start, size);
                if (buffer == null)
                {
                    continue;
                }
                for (int offset = 0; offset + 8 <= buffer.Length; offset += 8)
                {
                    double value = BitConverter.ToDouble(buffer, offset);
                    if (value >= 30 && value <= 24 * 3600)
                    {
                        candidates.Add((start + (ulong)offset, value));
                    }
                }
            }

            if (candidates.Count == 0)
            {
                SetStatus("扫描没有候选值");
                return;
            }

            Thread.Sleep(CalibrationWaitSeconds * 1000);

            Dictionary<ulong, byte[]> pages = new();
            (ulong Address, double Before, double After)? found = null;
            int matches = 0;
            foreach ((ulong address, double before) in candidates)
            {
                ulong pageStart = address & ~0xFFFUL;
                if (!pages.TryGetValue(pageStart, out byte[]? page))
                {
                    page = _scanner.ReadRegion(pageStart, 4096);
                    if (page == null)
                    {
                        continue;
                    }
                    if (pages.Count > 40000)
                    {
                        pages.Clear();
                    }
                    pages[pageStart] = page;
                }
                int offsetInPage = (int)(address - pageStart);
                if (offsetInPage + 8 > page.Length)
                {
                    continue;
                }
                double after = BitConverter.ToDouble(page, offsetInPage);
                double delta = after - before;
                if (delta >= CalibrationWaitSeconds * 0.7 && delta <= CalibrationWaitSeconds * 1.4)
                {
                    matches++;
                    found ??= (address, before, after);
                }
            }

            if (found is not (ulong address2, double before2, double after2))
            {
                SetStatus($"等待 {CalibrationWaitSeconds} 秒后没有数值按时间推进（可能处于暂停）");
                return;
            }

            lock (_gate)
            {
                _positionAddress = address2;
                _lastValue = after2;
                _lastValueAt = DateTime.Now;
                Status = $"已校准 偏移 +0x{address2 - _scanner.ModuleBase:X}" +
                         (matches > 1 ? $"（命中 {matches} 个，取第一个）" : "") +
                         $"  {before2:0.0}s -> {after2:0.0}s";
            }
            SaveOffset(address2 - _scanner.ModuleBase);
            Log.Write("netease", "位置已校准: " + Status);
        }
        catch (Exception ex)
        {
            SetStatus("校准异常: " + ex.Message);
            Log.Write("netease", "校准失败", ex);
        }
    }

    /// <summary>读当前位置（秒）。没校准好或值不合理时返回 null。</summary>
    public double? TryReadPositionSeconds()
    {
        ModuleMemoryScanner? scanner;
        ulong address;
        lock (_gate)
        {
            scanner = _scanner;
            address = _positionAddress;
        }
        if (scanner == null || address == 0)
        {
            return null;
        }

        double? value = scanner.ReadDouble(address);
        if (value is not double v || v < 0 || v > MaxPlausible || double.IsNaN(v))
        {
            Invalidate();
            return null;
        }

        lock (_gate)
        {
            if (_lastValue is double previous && !double.IsNaN(previous))
            {
                double delta = v - previous;
                double elapsed = (DateTime.Now - _lastValueAt).TotalSeconds;
                bool wentBackwards = delta < -1.0;
                bool jumped = delta > Math.Max(10, elapsed * 5 + 5);

                // 回到接近 0 = 换了一首（同一个结构体，地址仍然有效），只需重设基准，不要丢掉地址
                bool trackRestarted = wentBackwards && v <= 5;

                if ((wentBackwards && !trackRestarted) || jumped)
                {
                    Log.Write("netease", $"位置值不可信（{previous:0.0} -> {v:0.0}，间隔 {elapsed:0.0}s），重新校准");
                    _positionAddress = 0;
                    _lastValue = double.NaN;
                    Status = "值不可信，已重置";
                    return null;
                }
                if (trackRestarted && previous > 5)
                {
                    Log.Write("netease", $"换曲（{previous:0.0}s -> {v:0.0}s），基准重设");
                }
            }
            _lastValue = v;
            _lastValueAt = DateTime.Now;
        }
        return v;
    }

    /// <summary>用上次存下来的模块内偏移直接读一次；值合理就采用。</summary>
    private bool TrySavedOffset()
    {
        string saved = _settings?.Current.Readers.NetEasePositionOffset ?? "";
        if (string.IsNullOrWhiteSpace(saved) || _scanner == null)
        {
            return false;
        }
        if (!ulong.TryParse(saved, System.Globalization.NumberStyles.HexNumber, null, out ulong offset)
            || offset == 0
            || offset >= _scanner.ModuleSize)
        {
            return false;
        }

        ulong address = _scanner.ModuleBase + offset;
        if (_scanner.ReadDouble(address) is not double value || value < 0 || value > MaxPlausible || double.IsNaN(value))
        {
            Log.Write("netease", $"存下来的偏移 +0x{offset:X} 这次读到的值不合理，改走自校准");
            return false;
        }

        lock (_gate)
        {
            _positionAddress = address;
            _lastValue = value;
            _lastValueAt = DateTime.Now;
            Status = $"用上次的偏移 +0x{offset:X}（当前 {value:0.0}s）";
        }
        Log.Write("netease", Status);
        return true;
    }

    private void SaveOffset(ulong offset)
    {
        if (_settings == null)
        {
            return;
        }
        string text = offset.ToString("X");
        if (_settings.Current.Readers.NetEasePositionOffset == text)
        {
            return;
        }
        _settings.Current.Readers.NetEasePositionOffset = text;
        _settings.Save();
    }

    private void SetStatus(string status)
    {
        lock (_gate)
        {
            Status = status;
        }
        Log.Write("netease", status);
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_gate)
        {
            _positionAddress = 0;
        }
        _scanner?.Dispose();
        _scanner = null;
    }
}
