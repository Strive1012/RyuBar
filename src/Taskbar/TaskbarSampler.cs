using RyuBar.Interop;

namespace RyuBar.Taskbar;

/// <summary>
/// 任务栏采样结果。所有坐标都是物理像素、屏幕坐标系。
/// </summary>
public readonly record struct TaskbarSample(
    bool Found,
    IntPtr Handle,
    RECT WindowRect,
    RECT ClientScreenRect,
    RECT MonitorRect,
    double Scale,
    bool Hidden)
{
    public static readonly TaskbarSample Missing = new(false, IntPtr.Zero, default, default, default, 1.0, true);

    public int ClientWidth => ClientScreenRect.Width;

    public int ClientHeight => ClientScreenRect.Height;
}

public static class TaskbarSampler
{
    public static TaskbarSample Take()
    {
        IntPtr hwnd = TaskbarLocator.FindPrimary();
        if (hwnd == IntPtr.Zero)
        {
            return TaskbarSample.Missing;
        }
        if (!NativeMethods.GetWindowRect(hwnd, out RECT windowRect))
        {
            return TaskbarSample.Missing;
        }
        if (!TaskbarLocator.TryGetClientScreenRect(hwnd, out RECT clientRect))
        {
            return TaskbarSample.Missing;
        }
        RECT monitor = TaskbarLocator.MonitorRectOf(hwnd);
        bool hidden = TaskbarLocator.IsHidden(windowRect, monitor);
        return new TaskbarSample(true, hwnd, windowRect, clientRect, monitor, TaskbarLocator.ScaleOf(hwnd), hidden);
    }
}
