using System.Threading;
using System.Windows;
using System.Windows.Interop;
using RyuBar.Diagnostics;
using RyuBar.Taskbar;

namespace RyuBar;

public partial class App : Application
{
    private const string MutexName = "RyuBar.SingleInstance.v1";
    private const string ExitEventName = "RyuBar.Exit.v1";
    private const string Version = "0.8.41";

    private Mutex? _mutex;
    private EventWaitHandle? _exitSignal;
    private RegisteredWaitHandle? _exitRegistration;
    private TaskbarBarWindow? _bar;
    private Configuration.SettingsStore? _settings;
    private Media.MediaSessionWatcher? _media;
    private bool _recreateQueued;
    private bool _shuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string[] args = Environment.GetCommandLineArgs();
        string command = args.Length > 1 ? args[1].Trim().ToLowerInvariant() : "";

        switch (command)
        {
            case "--help":
            case "-h":
            case "/?":
                PrintHelp();
                Shutdown();
                return;
            case "--diag":
                string diagMode = args.Length > 2 ? args[2] : "";
                Diag.Run(
                    diagMode.Equals("seek", StringComparison.OrdinalIgnoreCase),
                    diagMode.Equals("netease", StringComparison.OrdinalIgnoreCase),
                    diagMode.Equals("lyricapi", StringComparison.OrdinalIgnoreCase),
                    diagMode.Equals("lyrics", StringComparison.OrdinalIgnoreCase));
                Shutdown();
                return;
            case "--exit":
                SignalExit();
                Shutdown();
                return;
            case "--settings":
                // 只记下请求，等宿主起来后再开（宿主持有 SettingsStore 与所有设置项）
                SettingsRequest = args.Length > 2 ? args[2].Trim().ToLowerInvariant() : "glass";
                break;
        }

        Log.Init();
        Log.Write("app", $"===== RyuBar {Version} 启动 (argv: {string.Join(' ', args.Skip(1))}) =====");

        _settings = new Configuration.SettingsStore();
        _settings.Load();
        Configuration.FadeSettings fade = _settings.Current.Fade;
        Log.Write("settings", $"设置: {_settings.FilePath}  淡入={fade.FadeInEnabled}({fade.FadeInMilliseconds}ms, 延后 {fade.FadeInDelayMilliseconds}ms) " +
                              $"淡出={fade.FadeOutEnabled}({fade.FadeOutMilliseconds}ms)");

        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool created);
        if (!created)
        {
            Log.Write("app", "已有实例在运行，本次启动退出");
            Shutdown();
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            Log.Write("app", "未处理异常", eventArgs.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, eventArgs) =>
        {
            Log.Write("app", "UI 线程异常", eventArgs.Exception);
            eventArgs.Handled = true;
        };

        // 退出走命名事件：不依赖窗口消息，命令转发也不受广播限制
        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        _exitRegistration = ThreadPool.RegisterWaitForSingleObject(
            _exitSignal,
            (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                Log.Write("app", "收到退出信号");
                Shutdown();
            })),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        _media = new Media.MediaSessionWatcher(Dispatcher, _settings);
        _media.Start();

        CreateBar();

        Log.Write("app", "启动完成");
    }

    /// <summary>命令行请求打开设置窗口：""（不开）/ self / stock / both。</summary>
    public static string SettingsRequest { get; private set; } = "";

    /// <summary>创建并显示宿主窗口；Explorer 重启后也走这条路径重建。</summary>
    private void CreateBar()
    {
        TaskbarBarWindow bar = new(_settings!, _media!);
        bar.HostInvalidated += OnHostInvalidated;
        _bar = bar;
        // 先建 HWND（此时 SourceInitialized 会把宿主挂进任务栏），再显示，避免以顶层窗口闪一下
        _ = new WindowInteropHelper(bar).EnsureHandle();
        bar.Show();
    }

    /// <summary>
    /// 宿主窗口被销毁（Explorer 重启会连带销毁我们的子窗口）后重建。
    /// 在下一个 Dispatcher 帧执行：WM_NCDESTROY 期间动可视树不安全。
    /// </summary>
    private void OnHostInvalidated()
    {
        if (_recreateQueued || _shuttingDown)
        {
            return;
        }
        _recreateQueued = true;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            _recreateQueued = false;
            if (_shuttingDown)
            {
                return;
            }

            Log.Write("app", "重建宿主窗口");
            try
            {
                _bar?.Close();
            }
            catch
            {
            }
            _bar = null;

            try
            {
                CreateBar();
            }
            catch (Exception ex)
            {
                Log.Write("app", "重建宿主失败", ex);
            }
        }));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shuttingDown = true;
        _media?.Dispose();
        Log.Write("app", "退出");
        _exitRegistration?.Unregister(null);
        _exitSignal?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private static void SignalExit()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ExitEventName, out EventWaitHandle? handle))
            {
                using (handle)
                {
                    handle.Set();
                }
                Print("已通知运行中的实例退出");
            }
            else
            {
                Print("没有正在运行的实例");
            }
        }
        catch (Exception ex)
        {
            Print("发送退出信号失败: " + ex.Message);
        }
    }

    private static void PrintHelp()
    {
        Print(
            """
            RyuBar 0.8.41 — Windows 任务栏媒体条（自研）

            用法:
              RyuBar.exe            启动（单实例）
              RyuBar.exe --diag     诊断：任务栏几何 / 占用区 / 选点 / 运行中实例状态
              RyuBar.exe --exit     让运行中的实例退出
              RyuBar.exe --help     显示本帮助

            日志: %LOCALAPPDATA%\RyuBar\logs\ryubar.log
            状态: %LOCALAPPDATA%\RyuBar\state.json
            """);
    }

    private static void Print(string text)
    {
        try
        {
            Console.WriteLine(text);
            Console.Out.Flush();
        }
        catch
        {
        }
    }
}
