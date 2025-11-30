using HomelabMapper.Core.Models;
using HomelabMapper.Core.Interfaces;

namespace HomelabMapper.Correlation;

public class CorrelationEngine
{
    public static void ReparentContainersToPortainerStacks(List<Entity> allEntities)
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

    public static void CorrelateVmAndLxcWithProxmoxNodes(List<Entity> allEntities, HashSet<string> discoveredIPs)
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
                // First, remove Unknown entities that match this VM/LXC IP
                var unknownEntities = allEntities.Where(e =>
                    e.Type == EntityType.Unknown &&
                    e.Ip == vm.Ip &&
                    e.Id != vm.Id
                ).ToList();

                foreach (var unknown in unknownEntities)
                {
                    // Copy open ports from Unknown entity to VM/LXC if VM doesn't have them
                    if (unknown.OpenPorts?.Any() == true && (vm.OpenPorts == null || !vm.OpenPorts.Any()))
                    {
                        vm.OpenPorts = unknown.OpenPorts;
                    }

                    // Remove the duplicate Unknown entity
                    allEntities.Remove(unknown);
                }

                // Then, check for services running on this VM
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

    public static void ReparentPhysicalHostsToCluster(List<Entity> allEntities, ICredentialStore credentialStore)
    {
        var clusters = allEntities.Where(e => e.Type == EntityType.ProxmoxCluster).ToList();

        foreach (var cluster in clusters)
        {
            // Get the node IPs for this cluster from the credential store
            var nodeIpsStr = credentialStore.GetCredential("cluster_node_ips", cluster.Id);
            if (!string.IsNullOrEmpty(nodeIpsStr))
            {
                var nodeIps = nodeIpsStr.Split(',', StringSplitOptions.RemoveEmptyEntries);
                
                // Find all physical hosts that match these node IPs
                foreach (var nodeIp in nodeIps)
                {
                    var matchingHosts = allEntities.Where(e =>
                        (e.Type == EntityType.Proxmox || e.Type == EntityType.Service) &&
                        e.Ip == nodeIp.Trim() &&
                        string.IsNullOrEmpty(e.ParentId)
                    ).ToList();

                    foreach (var host in matchingHosts)
                    {
                        host.ParentId = cluster.Id;
                        host.Status = ReachabilityStatus.Unreachable;
                        host.Metadata["reason"] = "Duplicate cluster node";
                    }
                }
            }
        }
    }

    public static void ReparentContainersToUnraid(List<Entity> allEntities)
    {
        var unraidHosts = allEntities.Where(e => e.Type == EntityType.Unraid).ToList();
        
        foreach (var unraidHost in unraidHosts)
        {
            // Find all Container entities at the same IP as the Unraid host
            // This includes containers discovered by Portainer or other scanners
            var containers = allEntities.Where(e => 
                e.Type == EntityType.Container && 
                e.Ip == unraidHost.Ip &&
                e.Id != unraidHost.Id
            ).ToList();

            // Find all stacks at this IP and reparent them to Unraid
            var stacks = allEntities.Where(e => 
                e.Type == EntityType.PortainerStack && 
                e.Ip == unraidHost.Ip
            ).ToList();

            foreach (var stack in stacks)
            {
                // Reparent stack to Unraid host
                stack.ParentId = unraidHost.Id;
            }

            // Reparent containers that are not already children of stacks
            foreach (var container in containers)
            {
                // Skip if already a child of a stack (stacks will be children of Unraid)
                var isStackChild = stacks.Any(s => s.Id == container.ParentId);
                
                if (!isStackChild && container.ParentId != unraidHost.Id)
                {
                    container.ParentId = unraidHost.Id;
                }
            }
        }
    }
}
