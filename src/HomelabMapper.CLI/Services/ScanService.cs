using System.Collections.Concurrent;
using System.Text;
using HomelabMapper.CLI.Configuration;
using HomelabMapper.Core.Models;

namespace HomelabMapper.CLI.Services;

public class ScanService
{
    private readonly ConcurrentDictionary<string, ScanJob> _jobs = new();
    private ScanJob? _currentJob;
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    public Task<ScanJob> TriggerScanAsync(string configPath)
    {
        // Check if a scan is already running
        if (_currentJob?.Status == ScanStatus.Running)
        {
            return Task.FromResult(_currentJob);
        }

        var jobId = $"scan-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        var job = new ScanJob
        {
            Id = jobId,
            Status = ScanStatus.Running,
            StartTime = DateTime.UtcNow,
            Logs = new List<string>()
        };

        _jobs[jobId] = job;
        _currentJob = job;

        // Run scan in background
        _ = Task.Run(async () => await ExecuteScanAsync(job, configPath));

        return Task.FromResult(job);
    }

    private async Task ExecuteScanAsync(ScanJob job, string configPath)
    {
        await _scanLock.WaitAsync();
        
        try
        {
            var logCapture = new StringWriter();
            var originalOut = Console.Out;
            var originalError = Console.Error;

            // Capture console output
            var multiWriter = new MultiTextWriter(originalOut, logCapture);
            Console.SetOut(multiWriter);
            Console.SetError(multiWriter);

            try
            {
                job.AddLog("Starting scan...");
                
                // Run the scan (call the main program logic)
                await Program.RunScanAsync(configPath, job);
                
                job.Status = ScanStatus.Completed;
                job.EndTime = DateTime.UtcNow;
                job.AddLog($"Scan completed in {(job.EndTime.Value - job.StartTime).TotalSeconds:F1} seconds");
            }
            catch (Exception ex)
            {
                job.Status = ScanStatus.Failed;
                job.EndTime = DateTime.UtcNow;
                job.Error = ex.Message;
                job.AddLog($"ERROR: {ex.Message}");
                job.AddLog(ex.StackTrace ?? "");
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
                
                // Store captured logs
                job.CapturedOutput = logCapture.ToString();
            }
        }
        finally
        {
            _scanLock.Release();
            _currentJob = null;
        }
    }

    public ScanJob? GetJob(string jobId)
    {
        return _jobs.TryGetValue(jobId, out var job) ? job : null;
    }

    public ScanJob? GetCurrentJob()
    {
        return _currentJob;
    }

    public List<ScanJob> GetRecentJobs(int count = 10)
    {
        return _jobs.Values
            .OrderByDescending(j => j.StartTime)
            .Take(count)
            .ToList();
    }
}

public class ScanJob
{
    public string Id { get; set; } = "";
    public ScanStatus Status { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? Error { get; set; }
    public List<string> Logs { get; set; } = new();
    public string? CapturedOutput { get; set; }
    public TopologyReport? Report { get; set; }

    public void AddLog(string message)
    {
        var timestamp = DateTime.UtcNow.ToString("HH:mm:ss");
        Logs.Add($"[{timestamp}] {message}");
    }
}

public enum ScanStatus
{
    Running,
    Completed,
    Failed
}

// Helper class to write to multiple TextWriters
public class MultiTextWriter : TextWriter
{
    private readonly TextWriter[] _writers;

    public MultiTextWriter(params TextWriter[] writers)
    {
        _writers = writers;
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        foreach (var writer in _writers)
        {
            writer.Write(value);
        }
    }

    public override void WriteLine(string? value)
    {
        foreach (var writer in _writers)
        {
            writer.WriteLine(value);
        }
    }

    public override void Flush()
    {
        foreach (var writer in _writers)
        {
            writer.Flush();
        }
    }
}
