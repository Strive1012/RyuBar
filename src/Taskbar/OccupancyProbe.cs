using System.Windows.Automation;
using RyuBar.Diagnostics;

namespace RyuBar.Taskbar;

/// <summary>一段被占用的横向区间（屏幕坐标，物理像素）。</summary>
public readonly record struct OccupiedRange(int Left, int Right, string Name);

public readonly record struct ProbeResult(
    IntPtr Taskbar,
    IReadOnlyList<OccupiedRange> Ranges,
    string Source,
    string Detail);

/// <summary>
/// 任务栏占用区探测：用 UI Automation 把"任务栏上已经有东西的地方"量出来，
/// 再交给纯策略去挑空闲区。
///
/// 三条硬约束（都是踩过的坑）：
///  1. UIA 可能被 Explorer 卡住 → 跑在专用 MTA 线程，队列只保留最新请求，Dispose 绝不等待 UIA。
///  2. 必须过滤本进程窗口，否则会把自己的条算成"已占用"，把位置越推越远。
///  3. 菜单打开时 FromHandle 只返回菜单子树 → 整次结果作废，让调用方保留上次布局。
/// </summary>
public sealed class OccupancyProbe : IDisposable
{
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _worker;
    private ProbeRequest? _pending;    private bool _disposed;

    public OccupancyProbe()
    {
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "RyuBar.UiaProbe",
        };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
    }

    /// <summary>探测完成（在 MTA 工作线程上触发，调用方负责切回 UI 线程）。</summary>
    public event Action<ProbeResult>? Completed;

    private sealed record ProbeRequest(IntPtr Taskbar, uint TaskbarProcessId, int TaskbarHeight, double Scale);

    public void Request(IntPtr taskbar, uint taskbarProcessId, int taskbarHeight, double scale)
    {
        if (_disposed || taskbar == IntPtr.Zero)
        {
            return;
        }
        lock (_gate)
        {
            _pending = new ProbeRequest(taskbar, taskbarProcessId, taskbarHeight, scale);
        }
        _wake.Set();
    }

    private void WorkerLoop()
    {
        while (!_disposed)
        {
            _wake.WaitOne();
            ProbeRequest? request;
            lock (_gate)
            {
                request = _pending;
                _pending = null;
            }
            if (request == null || _disposed)
            {
                continue;
            }

            ProbeResult result;
            try
            {
                result = Scan(request.Taskbar, request.TaskbarProcessId, request.TaskbarHeight, request.Scale);
            }
            catch (Exception ex)
            {
                Log.Write("probe", "UIA 探测异常", ex);
                result = new ProbeResult(request.Taskbar, Array.Empty<OccupiedRange>(), "error", ex.Message);
            }

            if (!_disposed)
            {
                Completed?.Invoke(result);
            }
        }
    }

    /// <summary>同步探测一次（供 --diag 使用），不经过队列。</summary>
    public static ProbeResult ScanOnce(IntPtr taskbar, uint taskbarProcessId, int taskbarHeight, double scale)
    {
        return Scan(taskbar, taskbarProcessId, taskbarHeight, scale);
    }

    private static ProbeResult Scan(IntPtr taskbar, uint taskbarProcessId, int taskbarHeight, double scale)
    {
        AutomationElement? root = AutomationElement.FromHandle(taskbar);
        if (root == null)
        {
            return new ProbeResult(taskbar, Array.Empty<OccupiedRange>(), "none", "FromHandle 返回 null");
        }

        Condition wanted = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.SplitButton),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));

        AutomationElementCollection found = root.FindAll(TreeScope.Descendants, wanted);

        double minPrimary = 8 * scale;
        double maxPrimary = 260 * scale;
        double minHeight = taskbarHeight * 0.45;

        List<OccupiedRange> ranges = new();
        int menuItems = 0;
        int skippedSelf = 0;

        foreach (AutomationElement element in found)
        {
            try
            {
                AutomationElement.AutomationElementInformation info = element.Current;
                if (info.ControlType == ControlType.MenuItem)
                {
                    // 菜单打开时整棵树都会被换成菜单子树，用它算布局会把条挤走
                    menuItems++;
                    continue;
                }
                if (info.IsOffscreen)
                {
                    continue;
                }
                if ((uint)info.ProcessId == Environment.ProcessId)
                {
                    skippedSelf++;
                    continue;
                }
                if (taskbarProcessId != 0 && (uint)info.ProcessId != taskbarProcessId)
                {
                    // 只认任务栏所在进程的窗口部件，第三方覆盖窗口交给后续阶段处理
                    continue;
                }

                System.Windows.Rect rect = info.BoundingRectangle;
                if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
                {
                    continue;
                }
                if (rect.Width < minPrimary || rect.Width > maxPrimary)
                {
                    continue;
                }
                if (rect.Height < minHeight)
                {
                    continue;
                }

                string name = string.IsNullOrWhiteSpace(info.Name) ? info.ControlType.ProgrammaticName : info.Name;
                ranges.Add(new OccupiedRange((int)Math.Round(rect.Left), (int)Math.Round(rect.Right), name));
            }
            catch (ElementNotAvailableException)
            {
                // 元素在枚举过程中消失是常态，跳过
            }
            catch (InvalidOperationException)
            {
            }
        }

        if (menuItems > 0)
        {
            return new ProbeResult(taskbar, Array.Empty<OccupiedRange>(), "menu-open",
                $"检测到 {menuItems} 个 MenuItem，本次作废");
        }

        string detail = $"elements={found.Count} kept={ranges.Count} skippedSelf={skippedSelf}";
        return new ProbeResult(taskbar, ranges, "uia", detail);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        lock (_gate)
        {
            _pending = null;
        }
        _wake.Set();
        _wake.Dispose();
    }
}
