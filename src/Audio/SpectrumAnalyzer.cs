using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RyuBar.Audio;

/// <summary>
/// 系统输出频谱分析。
///
/// ★ 分工（用户批准的唯一第三方依赖，只用在**采集层** ✓）：
///     · **采集**：NAudio 的 <see cref="WasapiLoopbackCapture"/>（WASAPI 环回 = 系统正在播放的声音 ✓）
///     · **分析**：自己写的 512 点 FFT + 对数频段归属 + 快起慢落平滑 ✓（一行都没有改动 ✓）
///
/// 为什么换成 NAudio 采集（这段经历值得留档）：
///   同一台机器上用自研 interop 调 `IAudioClient::Initialize(LOOPBACK)`，
///   7 个渲染设备**全部**返回 0x80070057 ✗；而 NAudio 在同一时刻采集成功（3 秒 1144320 字节 ✓）。
///   已穷举并排除：格式（逐字节比对 ✓）、参数组合（30+ 种）、[PreserveSig]、STA/MTA、
///   [ComImport]、LPStruct、WAVEFORMATEXTENSIBLE、WinRT ActivateAudioInterfaceAsync、
///   进程级 loopback、采集设备混音输入 —— 差异始终无法从外部观察到 ⇒ 改用成熟实现 ✓
///
/// 失败时不抛异常：写进 <see cref="LastError"/> ✓ 视图用"贴底平柱"表示（不假装有数据 ✓）。
/// </summary>
public sealed class SpectrumAnalyzer : IDisposable
{
    /// <summary>对外固定 16 个频段（视图按自己的柱数再采样一次 ✓ 这样柱数改了不用重建采集 ✓）。</summary>
    public const int BandCount = 16;

    private const int FftSize = 512;

    private readonly float[] _bands = new float[BandCount];
    private readonly float[] _window = new float[FftSize];
    private readonly float[] _samples = new float[FftSize];
    private readonly float[] _bins = new float[FftSize / 2];
    private readonly float[] _scratch = new float[FftSize * 2];
    private readonly float[] _target = new float[BandCount];

    private volatile bool _running;
    private Thread? _thread;
    private WasapiLoopbackCapture? _capture;
    private int _sampleRate = 48000;
    private int _channels = 2;
    private bool _isFloat = true;
    private string _deviceName = "";
    private int _filled;

    /// <summary>最近一次错误（空 = 正常）。</summary>
    public string LastError { get; private set; } = "";

    /// <summary>实际使用的采集设备名（诊断/展示用）。</summary>
    public string DeviceName => _deviceName;

    public bool IsRunning => _running;

    public SpectrumAnalyzer()
    {
        for (int i = 0; i < FftSize; i++)
        {
            _window[i] = 0.5f - (0.5f * (float)Math.Cos(2 * Math.PI * i / (FftSize - 1)));   // Hann
        }
    }

    /// <summary>把当前 16 个频段能量拷进目标数组（UI 线程调用 ✓ 无锁 ✓）。</summary>
    public void CopyBands(float[] destination)
    {
        int n = Math.Min(destination.Length, BandCount);
        for (int i = 0; i < n; i++)
        {
            destination[i] = _bands[i];
        }
    }

    /// <summary>
    /// 开始采集。全部动作放后台线程 ✓
    /// （打开设备会阻塞 ✓ 且 NAudio 的采集回调也在自己的线程上 ✓ 不占 UI 线程 ✓）
    /// </summary>
    public void Start()
    {
        if (_thread is not null)
        {
            return;   // 已经启动过（含"正在启动"中）✓
        }

        _thread = new Thread(() =>
        {
            try
            {
                if (!OpenCapture())
                {
                    Diagnostics.Log.Write("spectrum", $"启动采集：running=False 错误=\"{LastError}\"");
                    return;
                }

                _running = true;
                Diagnostics.Log.Write("spectrum",
                    $"采集已启动 ✓ 设备={_deviceName} 采样率={_sampleRate} 声道={_channels} 浮点={_isFloat}");
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Diagnostics.Log.Write("spectrum", $"采集线程异常 ✗ {ex.GetType().Name}: {ex.Message}");
            }
        })
        {
            IsBackground = true,
            Name = "RyuBar.Spectrum",
        };
        _thread.Start();
    }

    // ---------------------------------------------------------------- 采集

    private bool OpenCapture()
    {
        // 候选设备：默认渲染设备优先 ✓ 然后是所有活动渲染设备 ✓
        // （为什么还要遍历：默认设备可能是虚拟声卡，某些机器上它的 loopback 起不来）
        List<(MMDevice Device, string Name)> devices = new();
        try
        {
            using MMDeviceEnumerator enumerator = new();
            try
            {
                devices.Add((enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console), "★默认"));
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Write("spectrum", $"取默认渲染设备失败 ✗ {ex.Message}");
            }

            try
            {
                foreach (MMDevice one in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    devices.Add((one, ""));
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Write("spectrum", $"枚举渲染设备失败 ✗ {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            LastError = $"取音频设备失败：{ex.Message}";
            return false;
        }

        Diagnostics.Log.Write("spectrum", $"渲染设备候选 {devices.Count} 个");

        foreach ((MMDevice device, string tag) in devices)
        {
            string name;
            try
            {
                name = device.FriendlyName + (tag.Length > 0 ? tag : "");
            }
            catch
            {
                name = "(未命名设备)";
            }

            try
            {
                WasapiLoopbackCapture capture = new(device);
                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += OnRecordingStopped;
                capture.StartRecording();   // 起不来会在这里抛异常 ✓

                WaveFormat format = capture.WaveFormat;
                _sampleRate = format.SampleRate;
                _channels = Math.Max(1, format.Channels);
                _isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat || format.BitsPerSample == 32;
                _capture = capture;
                _deviceName = name;
                Diagnostics.Log.Write("spectrum",
                    $"  · {name} → ✓ {format.Encoding} {format.SampleRate}Hz {format.Channels}ch {format.BitsPerSample}bit");
                return true;
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Write("spectrum", $"  · {name} → 起不来 ✗ {ex.GetType().Name}: {ex.Message}");
            }
        }

        LastError = $"试了 {devices.Count} 个渲染设备，环回采集都起不来";
        return false;
    }

    /// <summary>NAudio 送数据进来（它自己的线程 ✓ 每包几毫秒 ✓）。</summary>
    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        int bytesPerSample = _isFloat ? 4 : 2;
        int frameSize = bytesPerSample * _channels;
        if (frameSize <= 0 || e.Buffer is null)
        {
            return;
        }

        int frames = e.BytesRecorded / frameSize;
        for (int f = 0; f < frames; f++)
        {
            int offset = f * frameSize;
            float mono = 0f;
            for (int ch = 0; ch < _channels; ch++)
            {
                int at = offset + (ch * bytesPerSample);
                if (at + bytesPerSample > e.BytesRecorded)
                {
                    break;
                }

                mono += _isFloat
                    ? BitConverter.ToSingle(e.Buffer, at)
                    : BitConverter.ToInt16(e.Buffer, at) / 32768f;
            }

            _samples[_filled++] = mono / _channels;
            if (_filled == FftSize)
            {
                _filled = 0;
                ComputeBands();
                Publish();
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            LastError = e.Exception.Message;
            Diagnostics.Log.Write("spectrum", $"采集被中断 ✗ {e.Exception.GetType().Name}: {e.Exception.Message}");
        }
    }

    // ---------------------------------------------------------------- 分析（自己写的 ✓）

    /// <summary>加窗 → FFT → 对数归并到 16 段 → 快起慢落平滑。</summary>
    private void ComputeBands()
    {
        int n = FftSize;
        for (int i = 0; i < n; i++)
        {
            _scratch[i * 2] = _samples[i] * _window[i];
            _scratch[(i * 2) + 1] = 0;
        }

        Fft(_scratch, n, false);

        int binCount = n / 2;
        for (int i = 0; i < binCount; i++)
        {
            float re = _scratch[i * 2];
            float im = _scratch[(i * 2) + 1];
            _bins[i] = (float)Math.Sqrt((re * re) + (im * im));
        }

        // 对数频率归并（低频细、高频粗 ✓ 视觉上更像真实频谱 ✓）
        const double LowHz = 40;
        double nyquist = _sampleRate / 2.0;
        double maxHz = Math.Min(nyquist, 16000);
        int binHz = Math.Max(1, _sampleRate / n);
        for (int band = 0; band < BandCount; band++)
        {
            double f0 = LowHz * Math.Pow(maxHz / LowHz, band / (double)BandCount);
            double f1 = LowHz * Math.Pow(maxHz / LowHz, (band + 1) / (double)BandCount);
            int b0 = Math.Clamp((int)(f0 / binHz), 1, binCount - 1);
            int b1 = Math.Clamp((int)(f1 / binHz), b0, binCount - 1);

            float sum = 0;
            int count = 0;
            for (int b = b0; b <= b1; b++)
            {
                sum += _bins[b];
                count++;
            }
            float average = count == 0 ? 0 : sum / count;

            // → 0..1：对数压缩（增益 12 倍是实测手感 ✓）
            double level = Math.Log10(1 + (average * 12)) / 2.2;
            float value = (float)Math.Clamp(level, 0, 1);

            // 快起慢落：升起立刻、落下缓慢 ✓
            float previous = _bands[band];
            _target[band] = value > previous ? value : previous + ((value - previous) * 0.18f);
        }
    }

    private void Publish()
    {
        for (int i = 0; i < BandCount; i++)
        {
            _bands[i] = _target[i];
        }
    }

    /// <summary>原地基-2 FFT（n 必须是 2 的幂 ✓）。</summary>
    private static void Fft(float[] data, int n, bool inverse)
    {
        // 位反转置换
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }
            j ^= bit;
            if (i < j)
            {
                (data[i * 2], data[j * 2]) = (data[j * 2], data[i * 2]);
                (data[(i * 2) + 1], data[(j * 2) + 1]) = (data[(j * 2) + 1], data[(i * 2) + 1]);
            }
        }

        // 蝶形
        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = (inverse ? 2 : -2) * Math.PI / len;
            float wr = (float)Math.Cos(angle);
            float wi = (float)Math.Sin(angle);
            for (int i = 0; i < n; i += len)
            {
                float cwr = 1f;
                float cwi = 0f;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = (i + k) * 2;
                    int b = (i + k + (len / 2)) * 2;
                    float xr = (data[b] * cwr) - (data[b + 1] * cwi);
                    float xi = (data[b] * cwi) + (data[b + 1] * cwr);
                    data[b] = data[a] - xr;
                    data[b + 1] = data[a + 1] - xi;
                    data[a] += xr;
                    data[a + 1] += xi;

                    float nwr = (cwr * wr) - (cwi * wi);
                    cwi = (cwr * wi) + (cwi * wr);
                    cwr = nwr;
                }
            }
        }

        if (inverse)
        {
            for (int i = 0; i < n * 2; i++)
            {
                data[i] /= n;
            }
        }
    }

    // ---------------------------------------------------------------- 清理

    public void Dispose()
    {
        _running = false;
        try
        {
            _capture?.StopRecording();
        }
        catch
        {
            // 退出时忽略
        }

        try
        {
            _capture?.Dispose();
        }
        catch
        {
            // 退出时忽略
        }

        _capture = null;
        try
        {
            _thread?.Join(500);
        }
        catch
        {
            // 退出时不等也无妨
        }

        _thread = null;
    }
}
