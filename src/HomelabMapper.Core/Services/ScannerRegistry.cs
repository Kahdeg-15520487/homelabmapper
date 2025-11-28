using HomelabMapper.Core.Interfaces;
using HomelabMapper.Core.Models;

namespace HomelabMapper.Core.Services;

public class ScannerRegistry
{
    private readonly Dictionary<string, IHostScanner> _scanners = new();
    private readonly ILogger _logger;

    public ScannerRegistry(ILogger logger)
    {
        _logger = logger;
    }

    public void Register(IHostScanner scanner)
    {
        _scanners[scanner.ScannerName] = scanner;
        _logger.Info($"Registered scanner: {scanner.ScannerName} (Priority: {scanner.Priority})");
    }

    public async Task<List<IHostScanner>> FindApplicableScannersAsync(Entity host, ScannerContext context)
    {
        var applicable = new List<IHostScanner>();

        foreach (var scanner in _scanners.Values.OrderBy(s => s.Priority))
        {
            // First check: Does entity type hint match this scanner?
            // This allows hints to directly activate scanners regardless of port criteria
            if (IsEntityTypeMatchForScanner(host.Type, scanner.ScannerName))
            {
                _logger.Info($"Scanner {scanner.ScannerName} activated by entity type hint: {host.Type}");
                applicable.Add(scanner);
                continue; // Skip port/header checks if type matches
            }

            var criteria = scanner.GetActivationCriteria();

            // Check port criteria
            if (criteria.RequiredOpenPorts?.Any() == true)
            {
                var hasAnyPort = criteria.RequiredOpenPorts.Any(port => host.OpenPorts.Contains(port));
                if (!hasAnyPort) continue;
            }

            // Check HTTP header criteria
            if (criteria.RequiredHttpHeaders?.Any() == true)
            {
                if (host.HttpHeaders == null) continue;
                
                var hasAllHeaders = criteria.RequiredHttpHeaders.All(kvp =>
                    host.HttpHeaders.TryGetValue(kvp.Key, out var value) && 
                    value?.Contains(kvp.Value, StringComparison.OrdinalIgnoreCase) == true
                );
                if (!hasAllHeaders) continue;
            }

            // Check URL pattern criteria
            if (criteria.RequiredUrlPatterns?.Any() == true)
            {
                var hasPattern = await CheckUrlPatternsAsync(host, criteria.RequiredUrlPatterns, context);
                if (!hasPattern) continue;
            }

            // Check custom predicate
            if (criteria.CustomPredicate?.Invoke(host) == false) continue;

            applicable.Add(scanner);
        }

        return applicable;
    }

    private bool IsEntityTypeMatchForScanner(EntityType entityType, string scannerName)
    {
        // Map entity types to their corresponding scanners
        return (entityType, scannerName) switch
        {
            (EntityType.PortainerService, "Portainer") => true,
            (EntityType.Proxmox, "Proxmox") => true,
            (EntityType.ProxmoxCluster, "Proxmox") => true,
            (EntityType.ProxmoxNode, "Proxmox") => true,
            (EntityType.DockerHost, "Docker") => true,
            (EntityType.Unraid, "Unraid") => true,
            _ => false
        };
    }

    private async Task<bool> CheckUrlPatternsAsync(Entity host, List<string> urlPatterns, ScannerContext context)
    {
        foreach (var pattern in urlPatterns)
        {
            try
            {
                var url = $"https://{host.Ip}{pattern}";
                var response = await context.Client.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch
            {
                // Try HTTP if HTTPS fails
                try
                {
                    var url = $"http://{host.Ip}{pattern}";
                    var response = await context.Client.GetAsync(url);
                    if (response.IsSuccessStatusCode)
                    {
                        return true;
                    }
                }
                catch
                {
                    // Ignore and try next pattern
                }
            }
        }

        return false;
    }

    public IHostScanner? GetScanner(string name)
    {
        return _scanners.GetValueOrDefault(name);
    }

    public IEnumerable<IHostScanner> GetAllScanners()
    {
        return _scanners.Values;
    }
}
