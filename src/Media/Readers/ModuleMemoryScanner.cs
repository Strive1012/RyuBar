using System.Diagnostics;
using RyuBar.Interop;

namespace RyuBar.Media.Readers;

/// <summary>
/// 从一个进程的某个模块里按值扫描内存。自校准的核心工具：
/// 位置是"3 秒后数值增加约 3 的 double"，不需要知道任何版本相关的固定偏移。
/// </summary>
internal sealed class ModuleMemoryScanner : IDisposable
{
    private readonly IntPtr _handle;
    private readonly List<(ulong Start, ulong Size)> _regions = new();

    public string ModuleName { get; }

    public ulong ModuleBase { get; }

    public ulong ModuleSize { get; }

    public int ProcessId { get; }

    private ModuleMemoryScanner(int processId, IntPtr handle, string moduleName, ulong moduleBase, ulong moduleSize)
    {
        ProcessId = processId;
        _handle = handle;
        ModuleName = moduleName;
        ModuleBase = moduleBase;
        ModuleSize = moduleSize;
        EnumerateRegions();
    }

    /// <summary>找到"拥有指定模块"的进程并打开它。找不到/没权限时返回 null（调用方退回纯 SMTC）。</summary>
    public static ModuleMemoryScanner? TryOpen(string processName, string moduleName)
    {
        foreach (Process process in Process.GetProcessesByName(processName))
        {
            try
            {
                ProcessModule? module = null;
                foreach (ProcessModule candidate in process.Modules)
                {
                    if (candidate.ModuleName.Equals(moduleName, StringComparison.OrdinalIgnoreCase))
                    {
                        module = candidate;
                        break;
                    }
                }
                if (module == null)
                {
                    continue;
                }

                IntPtr handle = MemoryApi.OpenForRead(process.Id);
                if (handle == IntPtr.Zero)
                {
                    return null;
                }
                return new ModuleMemoryScanner(
                    process.Id, handle, module.ModuleName,
                    (ulong)module.BaseAddress.ToInt64(), (ulong)module.ModuleMemorySize);
            }
            catch
            {
                // 目标进程可能在退出、或模块枚举权限不足：换下一个
            }
            finally
            {
                process.Dispose();
            }
        }
        return null;
    }

    private void EnumerateRegions()
    {
        ulong address = ModuleBase;
        ulong end = ModuleBase + ModuleSize;
        int size = System.Runtime.InteropServices.Marshal.SizeOf<MemoryApi.MEMORY_BASIC_INFORMATION64>();

        while (address < end)
        {
            if (MemoryApi.VirtualQueryEx(_handle, (IntPtr)(long)address, out MemoryApi.MEMORY_BASIC_INFORMATION64 info, size) == 0)
            {
                break;
            }
            if (info.RegionSize == 0)
            {
                break;
            }

            if (info.State == MemoryApi.MEM_COMMIT && MemoryApi.IsReadable(info.Protect))
            {
                ulong start = Math.Max(info.BaseAddress, ModuleBase);
                ulong regionEnd = Math.Min(info.BaseAddress + info.RegionSize, end);
                if (regionEnd > start)
                {
                    _regions.Add((start, regionEnd - start));
                }
            }

            address = info.BaseAddress + info.RegionSize;
            if (address <= info.BaseAddress)
            {
                break;
            }
        }
    }

    public IReadOnlyList<(ulong Start, ulong Size)> Regions => _regions;

    public long TotalBytes => _regions.Sum(r => (long)r.Size);

    /// <summary>把一个区域整块读进来；失败返回 null。</summary>
    public byte[]? ReadRegion(ulong start, ulong size)
    {
        if (size == 0 || size > 64UL * 1024 * 1024)
        {
            return null;
        }
        byte[] buffer = new byte[(int)size];
        if (!MemoryApi.ReadProcessMemory(_handle, (IntPtr)(long)start, buffer, buffer.Length, out int read) || read != buffer.Length)
        {
            return null;
        }
        return buffer;
    }

    public double? ReadDouble(ulong address)
    {
        byte[] buffer = new byte[8];
        if (!MemoryApi.ReadProcessMemory(_handle, (IntPtr)(long)address, buffer, 8, out int read) || read != 8)
        {
            return null;
        }
        return BitConverter.ToDouble(buffer, 0);
    }

    public void Dispose() => MemoryApi.CloseHandle(_handle);
}
