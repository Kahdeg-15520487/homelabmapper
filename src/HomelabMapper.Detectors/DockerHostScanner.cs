using HomelabMapper.Core.Interfaces;
using HomelabMapper.Core.Models;
using HomelabMapper.Integration;

namespace HomelabMapper.Detectors;

public class DockerHostScanner : IHostScanner
{
    public string ScannerName => "Docker";
    public int Priority => 20;
    public List<string> DependsOn => new();
    public List<string> OptionalDependsOn => new();

    public ScannerActivationCriteria GetActivationCriteria()
    {
        return new ScannerActivationCriteria
        {
            RequiredOpenPorts = new List<int> { 2375, 2376 }
        };
    }

    public async Task<ScanResult> ScanAsync(Entity host, ScannerContext context)
    {
        try
        {
            // Try port 2375 first (unencrypted), then 2376 (TLS)
            var port = host.OpenPorts.Contains(2375) ? 2375 : 2376;
            var apiClient = new DockerApiClient(new HttpClient { Timeout = TimeSpan.FromSeconds(5) }, host.Ip, port);

            // Verify this is actually Docker
            var version = await apiClient.GetVersionAsync();
            if (version == null)
            {
                return ScanResult.Failed(host, "Docker API not responding", "Version endpoint returned null");
            }

            host.Type = EntityType.DockerHost;
            host.Name = $"docker-{host.Ip}";
            host.Metadata["docker_version"] = version.Version;
            host.Metadata["docker_api_version"] = version.ApiVersion;
            host.Metadata["docker_os"] = version.Os;
            host.Metadata["docker_arch"] = version.Arch;

            context.Logger.Info($"Detected Docker {version.Version} at {host.Ip}");

            // Get containers
            var containers = await apiClient.GetContainersAsync(showAll: true);
            var discoveredEntities = new List<Entity>();

            foreach (var container in containers)
            {
                var containerName = container.Names.FirstOrDefault()?.TrimStart('/') ?? container.Id.Substring(0, 12);
                
                // Get the primary IP address
                var containerIp = GetContainerIpAddress(container);
                
                var containerEntity = new Entity
                {
                    Id = $"container-{container.Id.Substring(0, 12)}",
                    Ip = containerIp ?? "",
                    Type = EntityType.Container,
                    Name = containerName,
                    ParentId = host.Id,
                    Status = DetermineReachability(containerIp, context.DiscoveredIPs),
                    Metadata = new Dictionary<string, object>
                    {
                        ["docker_id"] = container.Id,
                        ["docker_image"] = container.Image,
                        ["docker_state"] = container.State,
                        ["docker_status"] = container.Status
                    }
                };

                // Add exposed ports
                if (container.Ports.Any())
                {
                    containerEntity.Metadata["exposed_ports"] = container.Ports
                        .Where(p => p.PublicPort.HasValue)
                        .Select(p => $"{p.PublicPort}:{p.PrivatePort}/{p.Type}")
                        .ToList();
                }

                // Store network mode for correlation
                if (container.NetworkSettings?.Networks.Any() == true)
                {
                    var networkNames = string.Join(", ", container.NetworkSettings.Networks.Keys);
                    containerEntity.Metadata["docker_networks"] = networkNames;
                }

                discoveredEntities.Add(containerEntity);
            }

            context.Logger.Info($"Docker scan found {discoveredEntities.Count} containers");

            return ScanResult.Successful(discoveredEntities);
        }
        catch (Exception ex)
        {
            return ScanResult.Failed(host, "Docker scan failed", ex.Message);
        }
    }

    public IEnumerable<Type> GetChildScannerTypes(ScanResult result)
    {
        return Array.Empty<Type>();
    }

    private string? GetContainerIpAddress(DockerContainer container)
    {
        if (container.NetworkSettings?.Networks == null || !container.NetworkSettings.Networks.Any())
        {
            return null;
        }

        // Try to get IP from any network
        foreach (var network in container.NetworkSettings.Networks.Values)
        {
            if (!string.IsNullOrEmpty(network.IPAddress))
            {
                return network.IPAddress;
            }
        }

        return null;
    }

    private ReachabilityStatus DetermineReachability(string? ip, HashSet<string> discoveredIPs)
    {
        if (string.IsNullOrEmpty(ip))
        {
            return ReachabilityStatus.Unreachable;
        }

        // Bridge networks (172.x.x.x, 10.x.x.x) are typically not reachable from outside
        if (ip.StartsWith("172.") || ip.StartsWith("10."))
        {
            return ReachabilityStatus.Unreachable;
        }

        // Check if we discovered this IP in the network scan
        return discoveredIPs.Contains(ip)
            ? ReachabilityStatus.Reachable
            : ReachabilityStatus.Unverified;
    }
}
