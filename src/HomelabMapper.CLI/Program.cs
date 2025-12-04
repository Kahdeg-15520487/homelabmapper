using HomelabMapper.Core.Services;
using HomelabMapper.Core.Interfaces;
using HomelabMapper.Core.Models;
using HomelabMapper.Discovery;
using HomelabMapper.Detectors;
using HomelabMapper.Reporting;
using HomelabMapper.Correlation;
using HomelabMapper.CLI.Configuration;
using HomelabMapper.CLI.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;

public partial class Program
{
    public static async Task Main(string[] args)
    {
        // Check if running in server mode
        if (args.Contains("--server"))
        {
            await RunServerAsync(args);
        }
        else
        {
            await RunCliAsync(args);
        }
    }

    private static async Task RunCliAsync(string[] args)
    {
        Console.WriteLine("=== Homelab Network Mapper ===\n");

        var configPath = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "config.yaml";
        
        await RunScanAsync(configPath, null);
        
        Console.WriteLine("\n✅ Scan complete!");
    }

    private static async Task RunServerAsync(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        
        // Configure services
        builder.Services.AddSingleton<ScanService>();
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.AllowAnyOrigin()
                      .AllowAnyMethod()
                      .AllowAnyHeader();
            });
        });

        var app = builder.Build();
        app.UseCors();

        var configPath = Environment.GetEnvironmentVariable("CONFIG_PATH") ?? "config.yaml";

        // API Endpoints
        app.MapPost("/api/scan", async (ScanService scanService) =>
        {
            var job = await scanService.TriggerScanAsync(configPath);
            return Results.Ok(new
            {
                jobId = job.Id,
                status = job.Status.ToString().ToLower(),
                startTime = job.StartTime,
                message = job.Status == ScanStatus.Running && job.Id != $"scan-{DateTime.UtcNow:yyyyMMdd-HHmmss}"
                    ? "Scan already in progress"
                    : "Scan started"
            });
        });

        app.MapGet("/api/scan/current", (ScanService scanService) =>
        {
            var job = scanService.GetCurrentJob();
            if (job == null)
            {
                return Results.Ok(new { status = "idle", message = "No scan in progress" });
            }

            return Results.Ok(new
            {
                jobId = job.Id,
                status = job.Status.ToString().ToLower(),
                startTime = job.StartTime,
                endTime = job.EndTime,
                duration = job.EndTime.HasValue
                    ? (job.EndTime.Value - job.StartTime).TotalSeconds
                    : (DateTime.UtcNow - job.StartTime).TotalSeconds,
                logs = job.Logs,
                error = job.Error
            });
        });

        app.MapGet("/api/scan/{jobId}", (string jobId, ScanService scanService) =>
        {
            var job = scanService.GetJob(jobId);
            if (job == null)
            {
                return Results.NotFound(new { error = "Job not found" });
            }

            return Results.Ok(new
            {
                jobId = job.Id,
                status = job.Status.ToString().ToLower(),
                startTime = job.StartTime,
                endTime = job.EndTime,
                duration = job.EndTime.HasValue
                    ? (job.EndTime.Value - job.StartTime).TotalSeconds
                    : (double?)null,
                logs = job.Logs,
                output = job.CapturedOutput,
                error = job.Error,
                report = job.Report != null ? new
                {
                    scanId = job.Report.ScanId,
                    timestamp = job.Report.Timestamp,
                    totalEntities = job.Report.Summary.TotalEntities,
                    conflicts = job.Report.Conflicts.Count
                } : null
            });
        });

        app.MapGet("/api/scans", (ScanService scanService, int count = 10) =>
        {
            var jobs = scanService.GetRecentJobs(count);
            return Results.Ok(jobs.Select(j => new
            {
                jobId = j.Id,
                status = j.Status.ToString().ToLower(),
                startTime = j.StartTime,
                endTime = j.EndTime,
                duration = j.EndTime.HasValue
                    ? (j.EndTime.Value - j.StartTime).TotalSeconds
                    : (double?)null,
                error = j.Error
            }));
        });

        app.MapGet("/api/reports/latest", async () =>
        {
            var config = File.Exists(configPath)
                ? ConfigurationLoader.Load(configPath)
                : new ScanConfiguration();

            if (File.Exists(config.Output.Markdown))
            {
                var content = await File.ReadAllTextAsync(config.Output.Markdown);
                return Results.Content(content, "text/markdown");
            }

            return Results.NotFound(new { error = "No report available" });
        });

        app.MapGet("/api/reports/latest.json", async () =>
        {
            var config = File.Exists(configPath)
                ? ConfigurationLoader.Load(configPath)
                : new ScanConfiguration();

            if (File.Exists(config.Output.Json))
            {
                var content = await File.ReadAllTextAsync(config.Output.Json);
                return Results.Content(content, "application/json");
            }

            return Results.NotFound(new { error = "No report available" });
        });

        // Serve static UI
        app.MapGet("/", () => Results.Content(GetIndexHtml(), "text/html"));

        Console.WriteLine("🌐 Homelab Mapper API Server");
        Console.WriteLine($"📍 Listening on: {app.Urls.FirstOrDefault() ?? "http://localhost:5000"}");
        Console.WriteLine($"📋 Config: {configPath}");
        Console.WriteLine();

        await app.RunAsync();
    }

    public static async Task RunScanAsync(string configPath, ScanJob? job)
    {
        // Load configuration
        var config = File.Exists(configPath)
            ? ConfigurationLoader.Load(configPath)
            : new ScanConfiguration();

        if (!File.Exists(configPath))
        {
            Console.WriteLine($"⚠️  Configuration file '{configPath}' not found. Using defaults.");
            Console.WriteLine("   Copy 'config.example.yaml' to 'config.yaml' and customize.\n");
        }

        // Initialize services
        var logLevel = Enum.TryParse<LogLevel>(config.Logging.Level, true, out var level)
            ? level
            : LogLevel.Info;
        var logger = new ConsoleLogger(logLevel);
        var credentialStore = new InMemoryCredentialStore();

        // Load credentials from configuration
        LoadCredentials(credentialStore, config.Credentials);

        var registry = new ScannerRegistry(logger);

        // Register scanners
        registry.Register(new ProxmoxHostScanner());
        registry.Register(new DockerHostScanner());
        registry.Register(new PortainerScanner());
        registry.Register(new RouterF670YScanner());
        registry.Register(new UnraidScanner());

        var orchestrator = new ScanOrchestrator(registry, logger);

        // Phase 1: Network Discovery
        logger.Info("Starting network discovery...");
        job?.AddLog("Starting network discovery...");
        
        var networkScanner = new NetworkScanner();
        var subnets = config.Scan.Subnets.Any()
            ? config.Scan.Subnets
            : new List<string> { "192.168.1.0/24" };

        logger.Info($"Scanning subnets: {string.Join(", ", subnets)}");
        var discoveredIPs = await networkScanner.DiscoverHostsAsync(subnets, config.Scan.TimeoutMs.Ping);
        logger.Info($"Discovered {discoveredIPs.Count} active hosts");
        job?.AddLog($"Discovered {discoveredIPs.Count} active hosts");

        // Phase 2: Port Fingerprinting
        logger.Info("Port scanning discovered hosts...");
        job?.AddLog("Port scanning discovered hosts...");
        
        var portScanner = new PortScanner();
        var entities = new System.Collections.Concurrent.ConcurrentBag<Entity>();

        var scanProgress = new System.Collections.Concurrent.ConcurrentDictionary<string, (string Status, int PortCount)>();
        var completedCount = 0;
        var totalHosts = discoveredIPs.Count;

        var progressCts = new System.Threading.CancellationTokenSource();
        var progressTask = Task.Run(async () =>
        {
            while (!progressCts.Token.IsCancellationRequested)
            {
                var scanning = scanProgress.Where(x => x.Value.Status == "Scanning").ToList();
                var complete = Interlocked.CompareExchange(ref completedCount, 0, 0);

                Console.Write($"\r  Progress: {complete}/{totalHosts} | Scanning: ");

                if (scanning.Any())
                {
                    var displayScans = scanning.Take(5).Select(x => x.Key).ToList();
                    Console.Write(string.Join(", ", displayScans));
                    if (scanning.Count > 5)
                    {
                        Console.Write($" +{scanning.Count - 5} more");
                    }
                }
                else if (complete < totalHosts)
                {
                    Console.Write("waiting...");
                }
                else
                {
                    Console.Write("done");
                }

                Console.Write(new string(' ', Math.Max(0, Console.BufferWidth - Console.CursorLeft - 1)));

                await Task.Delay(1000, progressCts.Token);
            }
        }, progressCts.Token);

        var scanTasks = discoveredIPs.Select(async ip =>
        {
            scanProgress[ip] = ("Scanning", 0);
            var entity = await portScanner.ScanHostAsync(ip, config.Scan.TimeoutMs.Http);
            entities.Add(entity);
            scanProgress[ip] = ("Complete", entity.OpenPorts.Count);
            Interlocked.Increment(ref completedCount);
            return entity;
        }).ToList();

        await Task.WhenAll(scanTasks);

        progressCts.Cancel();
        try { await progressTask; } catch { }

        Console.WriteLine();
        logger.Info($"Port scanning complete. Found {entities.Count} entities");
        job?.AddLog($"Port scanning complete. Found {entities.Count} entities");

        var entityList = entities.ToList();

        if (config.Hints?.Services?.Any() == true)
        {
            ApplyHints(entityList, config.Hints.Services, logger);
        }

        // Phase 3: Execute scan with orchestrator
        var context = new ScannerContext
        {
            Client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(config.Scan.TimeoutMs.Http) },
            Credentials = credentialStore,
            Logger = logger,
            DiscoveredIPs = discoveredIPs.ToHashSet(),
            AllEntities = entityList
        };

        logger.Info("Starting platform detection and API scanning...");
        job?.AddLog("Starting platform detection and API scanning...");
        
        var report = await orchestrator.ExecuteScanAsync(context, entityList);
        report.Subnets = subnets;

        // Phase 4: Post-scan correlation
        logger.Info("Running correlation engine...");
        job?.AddLog("Running correlation engine...");
        
        CorrelationEngine.ReparentContainersToPortainerStacks(report.Entities);
        CorrelationEngine.CorrelateVmAndLxcWithProxmoxNodes(report.Entities, discoveredIPs.ToHashSet());

        // Phase 5: Display results
        Console.WriteLine("\n=== Scan Results ===");
        Console.WriteLine($"Scan ID: {report.ScanId}");
        Console.WriteLine($"Timestamp: {report.Timestamp}");
        Console.WriteLine($"Total Entities: {report.Summary.TotalEntities}");
        
        job?.AddLog($"Scan ID: {report.ScanId}");
        job?.AddLog($"Total Entities: {report.Summary.TotalEntities}");

        if (report.Conflicts.Any())
        {
            Console.WriteLine($"\n⚠️  Conflicts Detected: {report.Conflicts.Count}");
            job?.AddLog($"Conflicts Detected: {report.Conflicts.Count}");
        }

        // Phase 6: Generate reports
        logger.Info("Generating reports...");
        job?.AddLog("Generating reports...");

        await JsonReporter.WriteAsync(report, config.Output.Json);
        logger.Info($"JSON report saved to: {config.Output.Json}");

        await MarkdownReporter.WriteAsync(report, config.Output.Markdown);
        logger.Info($"Markdown report saved to: {config.Output.Markdown}");

        await MermaidGenerator.WriteAsync(report, config.Output.Mermaid);
        logger.Info($"Mermaid diagram saved to: {config.Output.Mermaid}");

        // Phase 7: Save to history and generate diff if enabled
        if (config.Diff.Enabled)
        {
            Directory.CreateDirectory(config.Diff.HistoryDir);

            var historyPath = Path.Combine(config.Diff.HistoryDir, $"{report.ScanId}.json");
            await JsonReporter.WriteAsync(report, historyPath);

            var historyFiles = Directory.GetFiles(config.Diff.HistoryDir, "*.json")
                .OrderByDescending(f => f)
                .Skip(1)
                .FirstOrDefault();

            if (historyFiles != null)
            {
                logger.Info("Generating diff report...");
                var previousReport = await JsonReporter.ReadAsync(historyFiles);

                if (previousReport != null)
                {
                    var diffReport = DiffEngine.Compare(previousReport, report);
                    await DiffEngine.WriteMarkdownAsync(diffReport, config.Output.DiffReport);

                    logger.Info($"Diff report saved to: {config.Output.DiffReport}");
                    Console.WriteLine($"\n📊 Changes since last scan: {diffReport.AddedCount} added, {diffReport.RemovedCount} removed, {diffReport.ModifiedCount} modified");
                }
            }

            var allHistoryFiles = Directory.GetFiles(config.Diff.HistoryDir, "*.json")
                .OrderByDescending(f => f)
                .Skip(config.Diff.KeepLast)
                .ToList();

            foreach (var oldFile in allHistoryFiles)
            {
                File.Delete(oldFile);
            }
        }

        if (job != null)
        {
            job.Report = report;
        }
    }

    static void ApplyHints(List<Entity> entities, List<ServiceHint> hints, ConsoleLogger logger)
    {
        var appliedCount = 0;

        foreach (var hint in hints)
        {
            var matchingEntities = entities.Where(e => e.Ip == hint.Ip);

            foreach (var entity in matchingEntities)
            {
                if (hint.Port.HasValue)
                {
                    if (!entity.OpenPorts.Contains(hint.Port.Value))
                    {
                        continue;
                    }
                }

                if (!string.IsNullOrEmpty(hint.Name))
                {
                    entity.Name = hint.Name;
                    entity.Metadata["hint_applied"] = true;
                    entity.Metadata["hint_name"] = hint.Name;
                }

                if (!string.IsNullOrEmpty(hint.Type) &&
                    Enum.TryParse<EntityType>(hint.Type, out var hintType))
                {
                    if (entity.Type == EntityType.Unknown)
                    {
                        entity.Type = hintType;
                        entity.Metadata["hint_type"] = hint.Type;
                    }
                }

                if (!string.IsNullOrEmpty(hint.TokenEnv))
                {
                    entity.Metadata["hint_token_env"] = hint.TokenEnv;
                }

                appliedCount++;
                logger.Info($"Applied hint to {entity.Ip}: {hint.Name ?? hint.Type ?? "unnamed"}");
            }
        }

        if (appliedCount > 0)
        {
            logger.Info($"Applied {appliedCount} service hint(s) to entities");
        }
    }

    static void LoadCredentials(InMemoryCredentialStore store, CredentialsSettings creds)
    {
        if (creds.Proxmox != null && !string.IsNullOrEmpty(creds.Proxmox.Token))
        {
            store.SetCredential("proxmox", "token", creds.Proxmox.Token);
        }
        if (creds.Portainer != null && !string.IsNullOrEmpty(creds.Portainer.Token))
        {
            store.SetCredential("portainer", "token", creds.Portainer.Token);
        }
        if (creds.Docker != null && !string.IsNullOrEmpty(creds.Docker.Token))
        {
            store.SetCredential("docker", "token", creds.Docker.Token);
        }
        if (creds.Unraid != null && !string.IsNullOrEmpty(creds.Unraid.ApiKey))
        {
            store.SetCredential("unraid", "api_key", creds.Unraid.ApiKey);
        }
        if (creds.Router != null)
        {
            if (!string.IsNullOrEmpty(creds.Router.Username))
            {
                store.SetCredential("router", "username", creds.Router.Username);
            }
            if (!string.IsNullOrEmpty(creds.Router.Password))
            {
                store.SetCredential("router", "password", creds.Router.Password);
            }
        }
        if (creds.Ssh != null && creds.Ssh.Enabled && !string.IsNullOrEmpty(creds.Ssh.Username))
        {
            store.SetCredential("ssh", "username", creds.Ssh.Username);
            if (!string.IsNullOrEmpty(creds.Ssh.Password))
            {
                store.SetCredential("ssh", "password", creds.Ssh.Password);
            }
            if (!string.IsNullOrEmpty(creds.Ssh.PrivateKeyPath))
            {
                store.SetCredential("ssh", "private_key_path", creds.Ssh.PrivateKeyPath);
            }
        }
    }

    static string GetIndexHtml()
    {
        return @"
<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Homelab Network Mapper</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Oxygen, Ubuntu, Cantarell, sans-serif;
            background: #0f172a;
            color: #e2e8f0;
            padding: 20px;
        }
        .container { max-width: 1200px; margin: 0 auto; }
        h1 { color: #60a5fa; margin-bottom: 30px; font-size: 2rem; }
        .card {
            background: #1e293b;
            border-radius: 8px;
            padding: 24px;
            margin-bottom: 20px;
            border: 1px solid #334155;
        }
        .button {
            background: #3b82f6;
            color: white;
            border: none;
            padding: 12px 24px;
            border-radius: 6px;
            cursor: pointer;
            font-size: 16px;
            font-weight: 500;
            transition: background 0.2s;
        }
        .button:hover { background: #2563eb; }
        .button:disabled {
            background: #475569;
            cursor: not-allowed;
        }
        .status {
            display: inline-block;
            padding: 4px 12px;
            border-radius: 4px;
            font-size: 14px;
            font-weight: 500;
        }
        .status.running { background: #fbbf24; color: #78350f; }
        .status.completed { background: #34d399; color: #064e3b; }
        .status.failed { background: #ef4444; color: #7f1d1d; }
        .status.idle { background: #64748b; color: #f1f5f9; }
        .logs {
            background: #0f172a;
            padding: 16px;
            border-radius: 4px;
            max-height: 400px;
            overflow-y: auto;
            font-family: 'Courier New', monospace;
            font-size: 13px;
            line-height: 1.5;
            margin-top: 16px;
        }
        .log-line { margin: 2px 0; }
        .info { margin: 12px 0; color: #cbd5e1; }
        .link {
            color: #60a5fa;
            text-decoration: none;
            font-weight: 500;
        }
        .link:hover { text-decoration: underline; }
        #reportFrame {
            width: 100%;
            height: 600px;
            border: 1px solid #334155;
            border-radius: 4px;
            background: white;
        }
    </style>
</head>
<body>
    <div class=""container"">
        <h1>🌐 Homelab Network Mapper</h1>
        
        <div class=""card"">
            <h2 style=""margin-bottom: 16px; color: #94a3b8;"">Control Panel</h2>
            <button id=""scanBtn"" class=""button"" onclick=""triggerScan()"">▶️ Run Scan</button>
            <div id=""status"" style=""margin-top: 16px;""></div>
            <div id=""logs"" class=""logs"" style=""display: none;""></div>
        </div>

        <div class=""card"">
            <h2 style=""margin-bottom: 16px; color: #94a3b8;"">Latest Report</h2>
            <div class=""info"">
                <a href=""/api/reports/latest"" target=""_blank"" class=""link"">📄 View Markdown</a> | 
                <a href=""/api/reports/latest.json"" target=""_blank"" class=""link"">📊 View JSON</a>
            </div>
            <iframe id=""reportFrame"" src=""/api/reports/latest""></iframe>
        </div>
    </div>

    <script>
        let pollInterval = null;

        async function triggerScan() {
            const btn = document.getElementById('scanBtn');
            const statusDiv = document.getElementById('status');
            const logsDiv = document.getElementById('logs');

            btn.disabled = true;
            logsDiv.style.display = 'none';
            statusDiv.innerHTML = '<span class=""status running"">Starting...</span>';

            try {
                const response = await fetch('/api/scan', { method: 'POST' });
                const data = await response.json();

                if (data.message === 'Scan already in progress') {
                    statusDiv.innerHTML = '<span class=""status running"">Scan Already Running</span>';
                } else {
                    statusDiv.innerHTML = `<span class=""status running"">Scan Running</span> <span class=""info"">(Job: ${data.jobId})</span>`;
                }

                startPolling(data.jobId);
            } catch (error) {
                statusDiv.innerHTML = `<span class=""status failed"">Error</span> <span class=""info"">${error.message}</span>`;
                btn.disabled = false;
            }
        }

        function startPolling(jobId) {
            const statusDiv = document.getElementById('status');
            const logsDiv = document.getElementById('logs');
            const btn = document.getElementById('scanBtn');

            if (pollInterval) clearInterval(pollInterval);

            pollInterval = setInterval(async () => {
                try {
                    const response = await fetch(`/api/scan/${jobId}`);
                    const job = await response.json();

                    const duration = job.duration ? job.duration.toFixed(1) + 's' : 
                                   ((Date.now() - new Date(job.startTime)) / 1000).toFixed(1) + 's';

                    if (job.status === 'running') {
                        statusDiv.innerHTML = `<span class=""status running"">Running</span> <span class=""info"">(${duration})</span>`;
                        
                        if (job.logs && job.logs.length > 0) {
                            logsDiv.style.display = 'block';
                            logsDiv.innerHTML = job.logs.map(log => 
                                `<div class=""log-line"">${escapeHtml(log)}</div>`
                            ).join('');
                            logsDiv.scrollTop = logsDiv.scrollHeight;
                        }
                    } else if (job.status === 'completed') {
                        clearInterval(pollInterval);
                        statusDiv.innerHTML = `<span class=""status completed"">Completed</span> <span class=""info"">(${duration})</span>`;
                        logsDiv.style.display = 'block';
                        logsDiv.innerHTML = job.logs.map(log => 
                            `<div class=""log-line"">${escapeHtml(log)}</div>`
                        ).join('');
                        btn.disabled = false;
                        
                        // Reload report
                        document.getElementById('reportFrame').src = '/api/reports/latest?' + Date.now();
                    } else if (job.status === 'failed') {
                        clearInterval(pollInterval);
                        statusDiv.innerHTML = `<span class=""status failed"">Failed</span> <span class=""info"">${escapeHtml(job.error || 'Unknown error')}</span>`;
                        if (job.logs && job.logs.length > 0) {
                            logsDiv.style.display = 'block';
                            logsDiv.innerHTML = job.logs.map(log => 
                                `<div class=""log-line"">${escapeHtml(log)}</div>`
                            ).join('');
                        }
                        btn.disabled = false;
                    }
                } catch (error) {
                    console.error('Polling error:', error);
                }
            }, 1000);
        }

        function escapeHtml(text) {
            const div = document.createElement('div');
            div.textContent = text;
            return div.innerHTML;
        }

        // Check for running scan on load
        (async function checkStatus() {
            try {
                const response = await fetch('/api/scan/current');
                const data = await response.json();
                
                if (data.status === 'running') {
                    document.getElementById('scanBtn').disabled = true;
                    document.getElementById('status').innerHTML = '<span class=""status running"">Scan Running</span>';
                    startPolling(data.jobId);
                } else {
                    document.getElementById('status').innerHTML = '<span class=""status idle"">Ready</span>';
                }
            } catch (error) {
                console.error('Status check error:', error);
            }
        })();
    </script>
</body>
</html>
";
    }
}
