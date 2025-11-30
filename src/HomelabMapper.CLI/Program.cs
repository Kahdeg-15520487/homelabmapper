using HomelabMapper.Core.Services;
using HomelabMapper.Core.Interfaces;
using HomelabMapper.Discovery;
using HomelabMapper.Detectors;
using HomelabMapper.Reporting;
using HomelabMapper.Correlation;
using HomelabMapper.CLI.Configuration;

Console.WriteLine("=== Homelab Network Mapper ===\n");

// Load configuration
var configPath = args.Length > 0 ? args[0] : "config.yaml";
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

// Debug: Check if credentials were loaded
if (!string.IsNullOrEmpty(config.Credentials.Proxmox?.Token))
{
    logger.Debug($"Proxmox token loaded from env: length={config.Credentials.Proxmox.Token.Length}, starts with: {config.Credentials.Proxmox.Token.Substring(0, Math.Min(20, config.Credentials.Proxmox.Token.Length))}...");
}
else
{
    logger.Debug("Proxmox token NOT loaded!");
}

var registry = new ScannerRegistry(logger);

// Register scanners
registry.Register(new ProxmoxHostScanner());
registry.Register(new DockerHostScanner());
registry.Register(new PortainerScanner());
registry.Register(new UnraidScanner());

var orchestrator = new ScanOrchestrator(registry, logger);

// Phase 1: Network Discovery
logger.Info("Starting network discovery...");
var networkScanner = new NetworkScanner();
var subnets = config.Scan.Subnets.Any() 
    ? config.Scan.Subnets 
    : new List<string> { "192.168.1.0/24" };

logger.Info($"Scanning subnets: {string.Join(", ", subnets)}");
var discoveredIPs = await networkScanner.DiscoverHostsAsync(subnets, config.Scan.TimeoutMs.Ping);
logger.Info($"Discovered {discoveredIPs.Count} active hosts:");
foreach (var ip in discoveredIPs)
{
    logger.Info($"  - {ip}");
}

// Phase 2: Port Fingerprinting
logger.Info("Port scanning discovered hosts...");
var portScanner = new PortScanner();
var entities = new System.Collections.Concurrent.ConcurrentBag<HomelabMapper.Core.Models.Entity>();

// Parallel scan with progress tracking
var scanProgress = new System.Collections.Concurrent.ConcurrentDictionary<string, (string Status, int PortCount)>();
var completedCount = 0;
var totalHosts = discoveredIPs.Count;

// Start progress display task
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
        
        // Clear rest of line
        Console.Write(new string(' ', Math.Max(0, Console.BufferWidth - Console.CursorLeft - 1)));
        
        await Task.Delay(1000, progressCts.Token);
    }
}, progressCts.Token);

// Start scanning tasks
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

// Stop progress display
progressCts.Cancel();
try { await progressTask; } catch { }

Console.WriteLine();
logger.Info($"Port scanning complete. Found {entities.Count} entities");

// Convert to list for processing
var entityList = entities.ToList();

// Apply hints to entities
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
    AllEntities = entityList // Pass entities so scanners can access them
};

logger.Info("Starting platform detection and API scanning...");
var report = await orchestrator.ExecuteScanAsync(context, entityList);
report.Subnets = subnets;

// Phase 4: Post-scan correlation
logger.Info("Running correlation engine...");
CorrelationEngine.ReparentContainersToPortainerStacks(report.Entities);
CorrelationEngine.CorrelateVmAndLxcWithProxmoxNodes(report.Entities, discoveredIPs.ToHashSet());

// Phase 5: Display results
Console.WriteLine("\n=== Scan Results ===");
Console.WriteLine($"Scan ID: {report.ScanId}");
Console.WriteLine($"Timestamp: {report.Timestamp}");
Console.WriteLine($"Total Entities: {report.Summary.TotalEntities}");
Console.WriteLine("\nEntities by Type:");
foreach (var kvp in report.Summary.EntitiesByType.OrderByDescending(x => x.Value))
{
    Console.WriteLine($"  {kvp.Key}: {kvp.Value}");
}

Console.WriteLine("\nEntities by Status:");
foreach (var kvp in report.Summary.EntitiesByStatus.OrderByDescending(x => x.Value))
{
    Console.WriteLine($"  {kvp.Key}: {kvp.Value}");
}

if (report.Conflicts.Any())
{
    Console.WriteLine($"\n⚠️  Conflicts Detected: {report.Conflicts.Count}");
    foreach (var conflict in report.Conflicts.Take(5))
    {
        Console.WriteLine($"  - {conflict.Description}");
    }
    if (report.Conflicts.Count > 5)
    {
        Console.WriteLine($"  ... and {report.Conflicts.Count - 5} more (see report)");
    }
}

// Phase 6: Generate reports
logger.Info("Generating reports...");

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
    
    // Find previous scan
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
    
    // Clean up old scans
    var allHistoryFiles = Directory.GetFiles(config.Diff.HistoryDir, "*.json")
        .OrderByDescending(f => f)
        .Skip(config.Diff.KeepLast)
        .ToList();
    
    foreach (var oldFile in allHistoryFiles)
    {
        File.Delete(oldFile);
    }
}

Console.WriteLine("\n✅ Scan complete!");

static void ApplyHints(List<HomelabMapper.Core.Models.Entity> entities, List<ServiceHint> hints, ConsoleLogger logger)
{
    var appliedCount = 0;
    
    foreach (var hint in hints)
    {
        var matchingEntities = entities.Where(e => e.Ip == hint.Ip);
        
        foreach (var entity in matchingEntities)
        {
            // Check if port matches if specified in hint
            if (hint.Port.HasValue)
            {
                if (!entity.OpenPorts.Contains(hint.Port.Value))
                {
                    continue; // Skip this entity if port doesn't match
                }
            }
            
            // Apply hint name if provided
            if (!string.IsNullOrEmpty(hint.Name))
            {
                entity.Name = hint.Name;
                entity.Metadata["hint_applied"] = true;
                entity.Metadata["hint_name"] = hint.Name;
            }
            
            // Apply hint type if provided and entity is still Unknown
            if (!string.IsNullOrEmpty(hint.Type) && 
                Enum.TryParse<HomelabMapper.Core.Models.EntityType>(hint.Type, out var hintType))
            {
                if (entity.Type == HomelabMapper.Core.Models.EntityType.Unknown)
                {
                    entity.Type = hintType;
                    entity.Metadata["hint_type"] = hint.Type;
                }
            }
            
            // Store hint token_env if specified
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


