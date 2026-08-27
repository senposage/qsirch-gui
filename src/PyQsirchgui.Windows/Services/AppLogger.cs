using System.Diagnostics;
using System.IO;
using System.Collections.Concurrent;

namespace PyQsirchgui.Windows.Services;

public static class AppLogger
{
    private static readonly object Gate = new();
    private static readonly ConcurrentQueue<string> Pending = new();
    private static int _draining;
    private const long MaxBytes = 2 * 1024 * 1024;

    public static string LogPath
    {
        get
        {
            return Path.Combine(ConfigStore.PortableRoot, "logs", "PyQsirchgui.log");
        }
    }

    public static IDisposable Measure(string area, string message)
    {
        Info(area, message + " started");
        return new LogTimer(area, message);
    }

    public static void Info(string area, string message) => Write("INFO", area, message);

    public static void Warn(string area, string message) => Write("WARN", area, message);

    public static void Error(string area, Exception ex, string message)
    {
        Write("ERROR", area, $"{message}: {ex.GetType().Name}: {ex.Message}");
    }

    private static void Write(string level, string area, string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{area}] {Environment.MachineName}\\{Environment.UserName} {message}{Environment.NewLine}";
            Pending.Enqueue(line);
            if (Interlocked.Exchange(ref _draining, 1) == 0)
            {
                _ = Task.Run(DrainAsync);
            }
        }
        catch
        {
        }
    }

    private static void DrainAsync()
    {
        try
        {
            var logPath = LogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? AppContext.BaseDirectory);
            lock (Gate)
            {
                RotateIfNeeded(logPath);
                using var writer = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
                while (Pending.TryDequeue(out var line))
                {
                    writer.Write(line);
                }
            }
        }
        catch
        {
        }
        finally
        {
            Interlocked.Exchange(ref _draining, 0);
            if (!Pending.IsEmpty && Interlocked.Exchange(ref _draining, 1) == 0)
            {
                _ = Task.Run(DrainAsync);
            }
        }
    }

    private static void RotateIfNeeded(string logPath)
    {
        if (!File.Exists(logPath) || new FileInfo(logPath).Length < MaxBytes)
        {
            return;
        }

        var oldPath = logPath + ".old";
        if (File.Exists(oldPath))
        {
            File.Delete(oldPath);
        }
        File.Move(logPath, oldPath);
    }

    private sealed class LogTimer(string area, string message) : IDisposable
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public void Dispose()
        {
            _stopwatch.Stop();
            Info(area, $"{message} finished in {_stopwatch.ElapsedMilliseconds}ms");
        }
    }
}
