using HomelabMapper.Core.Models;

namespace HomelabMapper.Correlation;

public class CorrelationEngine
{
    public static void ReparentContainersToStacks(List<Entity> allEntities)
    {
        var stacks = allEntities.Where(e => e.Type == EntityType.PortainerStack).ToList();
        var containers = allEntities.Where(e => e.Type == EntityType.Container).ToList();

        foreach (var stack in stacks)
        {
            if (!stack.Metadata.ContainsKey("container_ids"))
            {
                continue;
            }

            var containerIds = stack.Metadata["container_ids"] as List<string>;
            if (containerIds == null) continue;

            foreach (var containerId in containerIds)
            {
                var container = containers.FirstOrDefault(c =>
                    c.Metadata.ContainsKey("docker_id") &&
                    c.Metadata["docker_id"] as string == containerId
                );

                if (container != null)
                {
                    // Reparent container from docker host to stack
                    container.ParentId = stack.Id;
                }
            }
        }
    }

    public static void CorrelateVmIpsWithHosts(List<Entity> allEntities, HashSet<string> discoveredIPs)
    {
        var vms = allEntities.Where(e => e.Type == EntityType.Vm || e.Type == EntityType.Lxc).ToList();

        foreach (var vm in vms)
        {
            // If VM has no IP but metadata contains api_reported_ip
            if (string.IsNullOrEmpty(vm.Ip) && vm.Metadata.ContainsKey("api_reported_ip"))
            {
                var apiIp = vm.Metadata["api_reported_ip"] as string;
                if (!string.IsNullOrEmpty(apiIp))
                {
                    vm.Ip = apiIp;
                    vm.Status = discoveredIPs.Contains(apiIp)
                        ? ReachabilityStatus.Reachable
                        : ReachabilityStatus.Unverified;
                }
            }

            // Try to find a matching host by IP
            if (!string.IsNullOrEmpty(vm.Ip))
            {
                var matchingHost = allEntities.FirstOrDefault(e =>
                    e.Id != vm.Id &&
                    e.Ip == vm.Ip &&
                    (e.Type == EntityType.DockerHost || e.Type == EntityType.PortainerService)
                );

                if (matchingHost != null)
                {
                    // This host is actually running on this VM
                    matchingHost.ParentId = vm.Id;
                }
            }
        }
    }

    public static void FindPortainerContainers(List<Entity> allEntities)
    {
        var portainerServices = allEntities.Where(e => e.Type == EntityType.PortainerService).ToList();
        var containers = allEntities.Where(e => e.Type == EntityType.Container).ToList();

        foreach (var portainerService in portainerServices)
        {
            // Find container that matches Portainer by IP or name
            var portainerContainer = containers.FirstOrDefault(c =>
                c.Ip == portainerService.Ip ||
                c.Name.Contains("portainer", StringComparison.OrdinalIgnoreCase)
            );

            if (portainerContainer != null)
            {
                // Mark this container as the Portainer service
                portainerContainer.Type = EntityType.PortainerService;
                portainerContainer.Metadata["is_portainer_service"] = true;
            }
        }
    }
}
