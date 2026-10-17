using NAudio.CoreAudioApi;

namespace RyuBar.Audio;

/// <summary>
/// **单应用音量**（用户要求："音量调节（单应用）"✓）。
///
/// 用法：按进程 id 找到那个应用的音频会话，读写它的 `ISimpleAudioVolume`。
/// ★ NAudio 的 `CoreAudioApi` 已经在依赖里 ✗ 不需要加包 ✓
///
/// 为什么按**进程 id**而不是按窗口/标题：
///   同一个应用可能开多个进程 ✓ 而我们只关心"正在出声的那个"✓
///   调用方（宿主）负责从 SMTC 的 Source 解析出进程名 → 拿到带窗口的 pid ✓
///
/// 注意：会话是**按输出设备**枚举的 ✓ 应用切到别的设备后旧会话会消失 ✓
///       所以每次操作都重新枚举 ✗ 不缓存会话引用 ✓（缓存会拿到失效的 COM 对象 ✗）
/// </summary>
public sealed class AppVolume : IDisposable
{
    private MMDeviceEnumerator? _enumerator;

    /// <summary>
    /// 读某个进程的会话音量（0..1）。
    /// 返回 null 表示：没有该进程的会话 / 设备不可用 / COM 出错 ✓（调用方据此保持界面原值 ✓）
    /// </summary>
    public float? TryGet(int processId)
    {
        try
        {
            foreach (AudioSessionControl session in EnumerateSessions())
            {
                try
                {
                    if (session.GetProcessID != (uint)processId)
                    {
                        continue;
                    }
                    SimpleAudioVolume volume = session.SimpleAudioVolume;
                    return volume.Volume;
                }
                finally
                {
                    session.Dispose();
                }
            }
        }
        catch
        {
            // 设备切换 / 会话销毁期间会抛 ⇒ 当作"读不到"✓ 不影响界面 ✓
        }

        return null;
    }

    /// <summary>设置某个进程的会话音量（0..1 ✓）。返回是否成功 ✓。</summary>
    public bool TrySet(int processId, float volume)
    {
        float clamped = Math.Clamp(volume, 0f, 1f);

        try
        {
            foreach (AudioSessionControl session in EnumerateSessions())
            {
                try
                {
                    if (session.GetProcessID != (uint)processId)
                    {
                        continue;
                    }
                    session.SimpleAudioVolume.Volume = clamped;
                    return true;
                }
                finally
                {
                    session.Dispose();
                }
            }
        }
        catch
        {
            // 同上 ✓
        }

        return false;
    }

    /// <summary>静音 / 取消静音 ✓</summary>
    public bool TrySetMute(int processId, bool muted)
    {
        try
        {
            foreach (AudioSessionControl session in EnumerateSessions())
            {
                try
                {
                    if (session.GetProcessID != (uint)processId)
                    {
                        continue;
                    }
                    session.SimpleAudioVolume.Mute = muted;
                    return true;
                }
                finally
                {
                    session.Dispose();
                }
            }
        }
        catch
        {
            // 同上 ✓
        }

        return false;
    }

    /// <summary>
    /// 枚举当前默认输出设备上的所有音频会话。
    /// ★ 每次调用都新建枚举器/设备 ✗ 不缓存 ✓
    ///   设备切换（用户换耳机）后旧的 `MMDevice` 会失效 ⇒ 缓存它只会拿到异常 ✓
    /// </summary>
    private IEnumerable<AudioSessionControl> EnumerateSessions()
    {
        _enumerator?.Dispose();
        _enumerator = new MMDeviceEnumerator();

        MMDevice device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        SessionCollection sessions = device.AudioSessionManager.Sessions;

        for (int i = 0; i < sessions.Count; i++)
        {
            yield return sessions[i];
        }
    }

    public void Dispose()
    {
        _enumerator?.Dispose();
        _enumerator = null;
    }
}
