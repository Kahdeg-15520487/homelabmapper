using HomelabMapper.Core.Interfaces;
using HomelabMapper.Core.Models;
using HomelabMapper.Integration;

namespace HomelabMapper.Detectors;

public class PortainerScanner : IHostScanner
{
    public string ScannerName => "Portainer";
    public int Priority => 30;
    public List<string> DependsOn => new() { "Docker" };

    private List<Entity> _allEntities = new();

    public ScannerActivationCriteria GetActivationCriteria()
    {
        return new ScannerActivationCriteria
        {
            RequiredOpenPorts = new List<int> { 9000, 9443 }
        };
    }

    public async Task<ScanResult> ScanAsync(Entity host, ScannerContext context)
    {
        try
        {
            var port = host.OpenPorts.Contains(9443) ? 9443 : 9000;
            var client = context.CreateClientWithCertTracking(host);
            var token = context.Credentials.GetCredential("portainer", "token");
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
            var portainerContainer = FindPortainerContainer(host);
            if (portainerContainer != null)
            {
                portainerContainer.Type = EntityType.PortainerService;
                portainerContainer.Metadata["portainer_version"] = status.Version;
            }

            // Get stacks
            var stacks = await apiClient.GetStacksAsync();
            var stackEntities = new List<Entity>();

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
                        ["portainer_endpoint_id"] = stack.EndpointId
                    }
                };

                stackEntities.Add(stackEntity);
            }

            // Get containers from Portainer API
            var portainerContainers = await apiClient.GetContainersAsync();
            
            // Reparent containers to stacks based on labels
            ReparentContainersToStacks(portainerContainers, stackEntities, context);

            context.Logger.Info($"Portainer scan found {stackEntities.Count} stacks");

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

    private Entity? FindPortainerContainer(Entity host)
    {
        // This is a placeholder - in reality, we'd need access to all entities
        // The ScanOrchestrator should handle this correlation after all scans complete
        return null;
    }

    private void ReparentContainersToStacks(
        List<DockerContainer> portainerContainers,
        List<Entity> stackEntities,
        ScannerContext context)
    {
        // Group containers by stack name from labels
        foreach (var container in portainerContainers)
        {
            // Portainer adds labels like "com.docker.compose.project"
            var containerName = container.Names.FirstOrDefault()?.TrimStart('/') ?? container.Id.Substring(0, 12);
            
            // Try to find stack association
            // This is simplified - real implementation would check multiple label formats
            foreach (var stack in stackEntities)
            {
                // If container name starts with stack name, it's probably part of the stack
                if (containerName.StartsWith(stack.Name, StringComparison.OrdinalIgnoreCase))
                {
                    // Store this information for correlation
                    if (!stack.Metadata.ContainsKey("container_ids"))
                    {
                        stack.Metadata["container_ids"] = new List<string>();
                    }
                    ((List<string>)stack.Metadata["container_ids"]).Add(container.Id);
                    
                    context.Logger.Debug($"Associated container {containerName} with stack {stack.Name}");
                }
            }
        }
    }
}
