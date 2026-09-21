using System;
using System.IO;

namespace GoogleDriveWorkSync.Helpers;

public static class DiagnosticLogger
{
    public static Func<bool>? IsUIThreadCheck { get; set; }
    public static Action<Action>? UIThreadDispatcher { get; set; }

    public static void LogTrace(string step)
    {
        try
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartSync", "Logs");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, "startup_diagnostic.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {step}\n");
        }
        catch { }
    }

    public static void LogCrash(string source, Exception? ex, string? message)
    {
        try
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartSync", "Logs");
            Directory.CreateDirectory(logDir);
            var content = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] CRASH in {source}: {message}\nException: {ex}\nStackTrace: {ex?.StackTrace}\nInnerException: {ex?.InnerException}\n\n";
            File.AppendAllText(Path.Combine(logDir, "startup_diagnostic.log"), content);
            File.AppendAllText(Path.Combine(logDir, "crash.log"), content);
        }
        catch { }
    }

    public static void RunOnUIThread(Action action)
    {
        if (UIThreadDispatcher != null && (IsUIThreadCheck == null || !IsUIThreadCheck()))
        {
            UIThreadDispatcher(action);
        }
        else
        {
            action();
        }
    }
}
