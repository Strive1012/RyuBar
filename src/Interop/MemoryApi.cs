using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RyuBar.Interop;

/// <summary>
/// 读别的进程内存所需的最小 P/Invoke 集合。
/// 用途：SMTC 拿不到时间轴的播放器（实测网易云），从它的模块数据段里读真实进度/时长。
/// </summary>
internal static class MemoryApi
{
    public const int PROCESS_QUERY_INFORMATION = 0x0400;
    public const int PROCESS_VM_READ = 0x0010;

    public const int MEM_COMMIT = 0x1000;
    public const int PAGE_READONLY = 0x02;
    public const int PAGE_READWRITE = 0x04;
    public const int PAGE_WRITECOPY = 0x08;
    public const int PAGE_EXECUTE_READ = 0x20;
    public const int PAGE_EXECUTE_READWRITE = 0x40;
    public const int PAGE_EXECUTE_WRITECOPY = 0x80;
    public const int PAGE_GUARD = 0x100;
    public const int PAGE_NOACCESS = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_BASIC_INFORMATION64
    {
        public ulong BaseAddress;
        public ulong AllocationBase;
        public int AllocationProtect;
        public int __alignment1;
        public ulong RegionSize;
        public int State;
        public int Protect;
        public int Type;
        public int __alignment2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int VirtualQueryEx(IntPtr process, IntPtr address, out MEMORY_BASIC_INFORMATION64 info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out int read);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool IsWow64Process(IntPtr process, out bool isWow64);

    /// <summary>以"查信息 + 读内存"的权限打开进程；失败返回 IntPtr.Zero（常见原因：目标以管理员运行）。</summary>
    public static IntPtr OpenForRead(int processId)
    {
        return OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, processId);
    }

    public static bool IsReadable(int protect)
    {
        if ((protect & PAGE_GUARD) != 0 || (protect & PAGE_NOACCESS) != 0)
        {
            return false;
        }
        int baseProtect = protect & 0xFF;
        return baseProtect is PAGE_READONLY or PAGE_READWRITE or PAGE_WRITECOPY
            or PAGE_EXECUTE_READ or PAGE_EXECUTE_READWRITE or PAGE_EXECUTE_WRITECOPY;
    }

    /// <summary>模块是否 64 位（用来判断我们和它是否同架构，决定指针宽度）。</summary>
    public static bool IsTarget64Bit(Process process)
    {
        try
        {
            if (Environment.Is64BitProcess)
            {
                return !IsWow64Process(process.Handle, out bool wow64) || !wow64;
            }
            return false;
        }
        catch
        {
            return Environment.Is64BitProcess;
        }
    }
}
