using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace RyuBar.Audio;

/// <summary>一个输出设备（id 用于切换 ✓ 名字用于显示 ✓）。</summary>
public sealed record AudioDevice(string Id, string Name, bool IsDefault)
{
    /// <summary>ComboBox 直接显示这个（不写 DataTemplate ✓ 少一层模板 ✓）。</summary>
    public override string ToString() => Name;
}

/// <summary>
/// 输出设备枚举 + 切换默认设备（用户要求："增加输出设备功能，可以切换当前的输出设备"✓）。
///
/// ★ 切换默认设备**没有公开 API** ✗
///   Windows 只在未文档化的 `IPolicyConfig::SetDefaultEndpoint` 里提供它 ✓
///   所有做这事儿的程序（AFM / EarTrumpet / Nircmd）走的都是同一个 COM 接口 ✓
///   接口 GUID 和"前面 12 个占位方法"都必须严格对齐 ✗ 少一个就会调到错误的方法上 ✓
/// </summary>
public sealed class AudioDevices
{
    /// <summary>枚举当前**可用**的输出设备（DeviceState.Active ✓ 已禁用的不列 ✓）。</summary>
    public IReadOnlyList<AudioDevice> Enumerate()
    {
        List<AudioDevice> result = new();

        try
        {
            using MMDeviceEnumerator enumerator = new();
            string defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;

            foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    result.Add(new AudioDevice(
                        device.ID,
                        device.FriendlyName,
                        string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase)));
                }
                finally
                {
                    device.Dispose();
                }
            }
        }
        catch
        {
            // 设备服务异常时返回已收集到的部分 ✓ 不抛给界面 ✓
        }

        return result;
    }

    /// <summary>把指定设备设为**所有角色**的默认输出 ✓。返回是否成功。</summary>
    public bool SetDefault(string deviceId)
    {
        try
        {
            IPolicyConfig policy = (IPolicyConfig)new CPolicyConfigClient();

            // 三个角色都要设 ✗ 只设 eMultimedia 的话"通信设备"还是旧的 ✓
            // 用户换耳机后会发现语音软件还在用旧设备 ✓
            bool a = policy.SetDefaultEndpoint(deviceId, (int)ERole.eConsole) == 0;
            bool b = policy.SetDefaultEndpoint(deviceId, (int)ERole.eMultimedia) == 0;
            bool c = policy.SetDefaultEndpoint(deviceId, (int)ERole.eCommunications) == 0;

            Marshal.ReleaseComObject(policy);
            return a || b || c;
        }
        catch
        {
            return false;
        }
    }

    private enum ERole
    {
        eConsole = 0,
        eMultimedia = 1,
        eCommunications = 2,
    }

    /// <summary>
    /// 未文档化的策略配置接口。
    /// ★ 方法顺序和签名必须与 vtable 完全一致 ✗
    ///   前面那 12 个方法我们根本不用 ✓ 但必须声明出来占位 ✓ 否则 SetDefaultEndpoint 会跳到错误位置 ✓
    /// </summary>
    [ComImport]
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string deviceName, IntPtr format);
        [PreserveSig] int GetDeviceFormat(string deviceName, bool isDefault, IntPtr format);
        [PreserveSig] int ResetDeviceFormat(string deviceName);
        [PreserveSig] int SetDeviceFormat(string deviceName, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(string deviceName, bool isDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod(string deviceName, IntPtr period);
        [PreserveSig] int GetShareMode(string deviceName, IntPtr mode);
        [PreserveSig] int SetShareMode(string deviceName, IntPtr mode);
        [PreserveSig] int GetPropertyValue(string deviceName, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue(string deviceName, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint(string deviceName, int role);
        [PreserveSig] int SetEndpointVisibility(string deviceName, int visible);
    }

    [ComImport]
    [Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class CPolicyConfigClient
    {
    }
}
