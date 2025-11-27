using HomelabMapper.Core.Interfaces;

namespace HomelabMapper.Core.Services;

public class ConsoleLogger : ILogger
{
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
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine($"[DEBUG] {DateTime.Now:HH:mm:ss} {message}");
        Console.ForegroundColor = originalColor;
    }
}
