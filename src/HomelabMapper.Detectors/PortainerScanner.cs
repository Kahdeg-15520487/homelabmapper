using HomelabMapper.Core.Interfaces;
using HomelabMapper.Core.Models;
using HomelabMapper.Integration;

namespace HomelabMapper.Detectors;

public class PortainerScanner : IHostScanner
{
    public string ScannerName => "Portainer";
    public int Priority => 30;
    public List<string> DependsOn => new();
    public List<string> OptionalDependsOn => new() { "Docker" };

    private List<Entity> _allEntities = new();

    public ScannerActivationCriteria GetActivationCriteria()
    {
        return new ScannerActivationCriteria
        {
            RequiredOpenPorts = new List<int> { 9000, 9010, 9443 }
        };
    }

    public async Task<ScanResult> ScanAsync(Entity host, ScannerContext context)
    {
        try
        {
            // Select port in order of preference: 9443 (HTTPS), 9010 (custom), 9000 (default)
            var port = host.OpenPorts.Contains(9443) ? 9443 :
                       host.OpenPorts.Contains(9010) ? 9010 : 9000;
            var client = context.CreateClientWithCertTracking(host);
            
            // Check if entity has a hint-specific token, otherwise use default
            string? token = null;
            if (host.Metadata.TryGetValue("hint_token_env", out var tokenEnvObj) && tokenEnvObj is string tokenEnv)
            {
                token = Environment.GetEnvironmentVariable(tokenEnv);
                context.Logger.Debug($"Using hint-specific token from {tokenEnv} for {host.Ip}");
            }
            else
            {
                token = context.Credentials.GetCredential("portainer", "token");
            }
            
            var apiClient = new PortainerApiClient(client, host.Ip, port, token);

            // Verify this is actually Portainer
            var status = await apiClient.GetStatusAsync();
            if (status == null)
            {
                return ScanResult.Failed(host, "Portainer API not responding", "Status endpoint returned null");
            }

            context.Logger.Info($"Detected Portainer {status.Version} at {host.Ip}");

            // Mark host as Portainer service type
            host.Metadata["portainer_version"] = status.Version;
            host.Metadata["portainer_instance_id"] = status.InstanceID;

            // Find the Portainer container itself (it should be in the Docker host's containers)
            // This will be marked as PortainerService type
            var portainerContainer = FindPortainerContainer(host, context);
            if (portainerContainer != null)
            {
                portainerContainer.Type = EntityType.PortainerService;
                portainerContainer.Metadata["portainer_version"] = status.Version;
            }

            // Get all endpoints (environments) first
            var endpoints = await apiClient.GetEndpointsAsync();
            context.Logger.Info($"Found {endpoints.Count} Portainer endpoint(s)");

            var stackEntities = new List<Entity>();

            // Process each endpoint
            foreach (var endpoint in endpoints)
            {
                context.Logger.Debug($"Processing endpoint: {endpoint.Name} (ID: {endpoint.Id})");

                // Get stacks for this endpoint
                var stacks = await apiClient.GetStacksAsync(endpoint.Id);
                
                foreach (var stack in stacks)
                {
                    var stackEntity = new Entity
                    {
                        Id = $"portainer-stack-{stack.Id}",
                        Ip = host.Ip,
                        Type = EntityType.PortainerStack,
                        Name = stack.Name,
                        ParentId = portainerContainer?.Id ?? host.Id,
                        Status = ReachabilityStatus.Reachable,
                        Metadata = new Dictionary<string, object>
                        {
                            ["portainer_stack_id"] = stack.Id,
                            ["portainer_stack_type"] = stack.Type,
                            ["portainer_endpoint_id"] = stack.EndpointId,
                            ["portainer_endpoint_name"] = endpoint.Name
                        }
                    };

                    stackEntities.Add(stackEntity);
                }

                // Get containers from Portainer API for this endpoint
                var portainerContainers = await apiClient.GetContainersAsync(endpoint.Id);

                // Create container entities and associate with stacks
                var containerEntities = CreateContainerEntities(portainerContainers, stackEntities, host, endpoint, context);
                stackEntities.AddRange(containerEntities);
            }

            context.Logger.Info($"Portainer scan found {stackEntities.Count} entities (stacks + containers)");

            return ScanResult.Successful(stackEntities);
        }
        catch (Exception ex)
        {
            return ScanResult.Failed(host, "Portainer scan failed", ex.Message);
        }
    }

    public IEnumerable<Type> GetChildScannerTypes(ScanResult result)
    {
        return Array.Empty<Type>();
    }

    private Entity? FindPortainerContainer(Entity host, ScannerContext context)
    {
        // Look for Portainer container in Docker scan results at the same IP
        // Portainer container typically has "portainer" in its name and runs on port 9000/9443
        var containers = context.AllEntities
            .Where(e => e.Type == EntityType.Container && e.Ip == host.Ip)
            .ToList();

        foreach (var container in containers)
        {
            var containerName = container.Name?.ToLower() ?? string.Empty;
            var hasPortainerPort = container.OpenPorts.Any(p => p == 9000 || p == 9443 || p == 9010);
            
            // Check if container name is exactly "portainer" or "portainer-ce" or "portainer-ee"
            // AND has Portainer ports exposed (to avoid false positives like "portainer-agent")
            var isPortainerService = (containerName == "portainer" || 
                                     containerName == "portainer-ce" || 
                                     containerName == "portainer-ee") && 
                                     hasPortainerPort;
            
            if (isPortainerService)
            {
                context.Logger.Debug($"Found Portainer service container: {container.Name} at {container.Ip}");
                return container;
            }
        }

        return null;
    }

    private List<Entity> CreateContainerEntities(
        List<DockerContainer> portainerContainers,
        List<Entity> stackEntities,
        Entity host,
        PortainerEndpoint endpoint,
        ScannerContext context)
    {
        var containerEntities = new List<Entity>();

        foreach (var container in portainerContainers)
        {
            var containerName = container.Names.FirstOrDefault()?.TrimStart('/') ?? container.Id.Substring(0, 12);
            
            // Try to find stack association via compose labels
            string? stackName = null;
            if (container.Labels != null && container.Labels.TryGetValue("com.docker.compose.project", out var composeProject))
            {
                stackName = composeProject;
            }

            // Find parent stack entity
            var parentStack = stackName != null 
                ? stackEntities.FirstOrDefault(s => s.Name.Equals(stackName, StringComparison.OrdinalIgnoreCase))
                : null;

            // Extract container IP and ports
            var containerIp = host.Ip; // Default to host IP
            var openPorts = new List<int>();

            // Get published ports
            if (container.Ports != null)
            {
                foreach (var port in container.Ports)
                {
                    if (port.PublicPort.HasValue && port.PublicPort.Value > 0)
                    {
                        openPorts.Add(port.PublicPort.Value);
                    }
                }
            }

            // Try to get container's internal IP from networks
            if (container.NetworkSettings?.Networks != null)
            {
                var network = container.NetworkSettings.Networks.Values.FirstOrDefault();
                if (!string.IsNullOrEmpty(network?.IPAddress))
                {
                    containerIp = network.IPAddress;
                }
            }

            var containerEntity = new Entity
            {
                Id = $"portainer-container-{container.Id}",
                Ip = containerIp,
                Type = EntityType.Container,
                Name = containerName,
                ParentId = parentStack?.Id ?? host.Id,
                Status = container.State == "running" ? ReachabilityStatus.Reachable : ReachabilityStatus.Unreachable,
                OpenPorts = openPorts,
                Metadata = new Dictionary<string, object>
                {
                    ["container_id"] = container.Id,
                    ["container_image"] = container.Image,
                    ["container_state"] = container.State,
                    ["portainer_endpoint_id"] = endpoint.Id,
                    ["portainer_endpoint_name"] = endpoint.Name
                }
            };

            if (parentStack != null)
            {
                context.Logger.Debug($"Associated container {containerName} with stack {parentStack.Name}");
            }

            containerEntities.Add(containerEntity);
        }

        return containerEntities;
    }
}
