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
                        ParentId = host.Id,
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

            // Try to find existing container created by Unraid scanner (match by Docker ID)
            var existingContainer = context.AllEntities.FirstOrDefault(e => 
                e.Type == EntityType.Container && 
                e.Metadata.ContainsKey("container_id") &&
                (e.Metadata["container_id"] as string ?? "").StartsWith(container.Id.Substring(0, Math.Min(12, container.Id.Length)))
            );

            if (existingContainer != null)
            {
                // Enrich existing Unraid container with Portainer metadata
                context.Logger.Debug($"Found existing container {containerName}, enriching with Portainer data");
                
                // Add Portainer-specific metadata
                existingContainer.Metadata["portainer_endpoint_id"] = endpoint.Id;
                existingContainer.Metadata["portainer_endpoint_name"] = endpoint.Name;
                
                // Update internal IP from Docker network if available
                if (container.NetworkSettings?.Networks != null)
                {
                    var network = container.NetworkSettings.Networks.Values.FirstOrDefault();
                    if (!string.IsNullOrEmpty(network?.IPAddress))
                    {
                        existingContainer.Metadata["internal_ip"] = network.IPAddress;
                    }
                }
                
                // Associate with stack if found
                if (parentStack != null)
                {
                    existingContainer.ParentId = parentStack.Id;
                    existingContainer.Metadata["stack_name"] = stackName;
                    context.Logger.Debug($"Associated container {containerName} with stack {parentStack.Name}");
                }
                
                // Don't add to containerEntities - it's already in context.AllEntities
                continue;
            }
            
            // Container not found in existing entities - create new one
            // This can happen if Portainer manages containers not visible to Unraid scanner
            context.Logger.Debug($"Container {containerName} not found in existing entities, creating new container entity");
            
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
                containerEntity.Metadata["stack_name"] = stackName;
            }

            containerEntities.Add(containerEntity);
        }

        return containerEntities;
    }
}
