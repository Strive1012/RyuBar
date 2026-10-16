using System.IO;
using System.Text;

namespace RyuBar.Diagnostics;

/// <summary>
/// 单文件日志：用户出问题时只需要发一份文件。
/// 按大小轮转（保留一份 .1），写入走锁 + 立即 flush，异常不会吃掉日志。
/// </summary>
public static class Log
{
    private const long MaxBytes = 512 * 1024;

    private static readonly object Gate = new();
    private static string _file = "";

    public static string FilePath
    {
        get { lock (Gate) return _file; }
    }

    public static string Directory
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RyuBar",
                "logs");
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static void Init()
    {
        lock (Gate)
        {
            _file = Path.Combine(Directory, "ryubar.log");
            RotateLocked();
        }
    }

    public static void Write(string category, string message)
    {
        Write(category, message, null);
    }

    public static void Write(string category, string message, Exception? error)
    {
        try
        {
            lock (Gate)
            {
                if (_file.Length == 0)
                {
                    Init();
                }
                RotateLocked();

                StringBuilder sb = new();
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                sb.Append("  [").Append(category).Append("] ").Append(message);
                if (error != null)
                {
                    sb.AppendLine();
                    sb.Append("      ").Append(error.GetType().Name).Append(": ").Append(error.Message);
                    if (error.StackTrace != null)
                    {
                        foreach (string line in error.StackTrace.Split('\n'))
                        {
                            sb.AppendLine();
                            sb.Append("      ").Append(line.Trim());
                        }
                    }
                }
                sb.AppendLine();

                File.AppendAllText(_file, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败不能影响主流程
        }
    }

    private static void RotateLocked()
    {
        if (_file.Length == 0 || !File.Exists(_file))
        {
            return;
        }
        FileInfo info = new(_file);
        if (info.Length <= MaxBytes)
        {
            return;
        }
        string previous = _file + ".1";
        try
        {
            File.Move(_file, previous, overwrite: true);
        }
        catch
        {
            try { File.Delete(_file); } catch { }
        }
    }
}
