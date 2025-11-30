using HomelabMapper.Core.Interfaces;

namespace HomelabMapper.Core.Services;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3
}

public class ConsoleLogger : ILogger
{
    private readonly LogLevel _minLevel;

    public ConsoleLogger(LogLevel minLevel = LogLevel.Info)
    {
        _minLevel = minLevel;
    }

    public void Info(string message)
    {
        Console.WriteLine($"[INFO] {DateTime.Now:HH:mm:ss} {message}");
    }

    public void Warn(string message)
    {
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[WARN] {DateTime.Now:HH:mm:ss} {message}");
        Console.ForegroundColor = originalColor;
    }

    public void Error(string message, Exception? ex = null)
    {
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[ERROR] {DateTime.Now:HH:mm:ss} {message}");
        if (ex != null)
        {
            Console.WriteLine($"  Exception: {ex.Message}");
        }
        Console.ForegroundColor = originalColor;
    }

    public void Debug(string message)
    {
        if (_minLevel > LogLevel.Debug)
            return;

        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine($"[DEBUG] {DateTime.Now:HH:mm:ss} {message}");
        Console.ForegroundColor = originalColor;
    }
}
