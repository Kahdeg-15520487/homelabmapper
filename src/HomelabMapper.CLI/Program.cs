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
var logger = new ConsoleLogger();
var credentialStore = new InMemoryCredentialStore();

// Load credentials from configuration
LoadCredentials(credentialStore, config.Credentials);

var registry = new ScannerRegistry(logger);

// Register scanners
registry.Register(new ProxmoxHostScanner());
registry.Register(new DockerHostScanner());
registry.Register(new PortainerScanner());

var orchestrator = new ScanOrchestrator(registry, logger);

// Phase 1: Network Discovery
logger.Info("Starting network discovery...");
var networkScanner = new NetworkScanner();
var subnets = config.Scan.Subnets.Any() 
    ? config.Scan.Subnets 
    : new List<string> { "192.168.1.0/24" };

logger.Info($"Scanning subnets: {string.Join(", ", subnets)}");
var discoveredIPs = await networkScanner.DiscoverHostsAsync(subnets, config.Scan.TimeoutMs.Ping);
logger.Info($"Discovered {discoveredIPs.Count} active hosts");

// Phase 2: Port Fingerprinting
logger.Info("Port scanning discovered hosts...");
var portScanner = new PortScanner();
var entities = new List<HomelabMapper.Core.Models.Entity>();

foreach (var ip in discoveredIPs)
{
    var entity = await portScanner.ScanHostAsync(ip, config.Scan.TimeoutMs.Http);
    entities.Add(entity);
}

logger.Info($"Port scanning complete. Found {entities.Count} entities");

// Phase 3: Execute scan with orchestrator
var context = new ScannerContext
{
    Client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(config.Scan.TimeoutMs.Http) },
    Credentials = credentialStore,
    Logger = logger,
    DiscoveredIPs = discoveredIPs.ToHashSet()
};

logger.Info("Starting platform detection and API scanning...");
var report = await orchestrator.ExecuteScanAsync(context, entities);
report.Subnets = subnets;

// Phase 4: Post-scan correlation
logger.Info("Running correlation engine...");
CorrelationEngine.ReparentContainersToStacks(report.Entities);
CorrelationEngine.CorrelateVmIpsWithHosts(report.Entities, discoveredIPs.ToHashSet());
CorrelationEngine.FindPortainerContainers(report.Entities);

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

static void LoadCredentials(InMemoryCredentialStore store, CredentialsSettings creds)
{
    if (!string.IsNullOrEmpty(creds.Proxmox.Token))
    {
        store.SetCredential("proxmox", "token", creds.Proxmox.Token);
    }
    if (!string.IsNullOrEmpty(creds.Portainer.Token))
    {
        store.SetCredential("portainer", "token", creds.Portainer.Token);
    }
    if (!string.IsNullOrEmpty(creds.Docker.Token))
    {
        store.SetCredential("docker", "token", creds.Docker.Token);
    }
    if (!string.IsNullOrEmpty(creds.Unraid.ApiKey))
    {
        store.SetCredential("unraid", "api_key", creds.Unraid.ApiKey);
    }
}
