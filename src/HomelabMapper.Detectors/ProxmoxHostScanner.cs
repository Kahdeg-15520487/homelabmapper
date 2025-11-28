using HomelabMapper.Core.Interfaces;
using HomelabMapper.Core.Models;
using HomelabMapper.Integration;

namespace HomelabMapper.Detectors;

public class ProxmoxHostScanner : IHostScanner
{
    public string ScannerName => "Proxmox";
    public int Priority => 10;
    public List<string> DependsOn => new();
    public List<string> OptionalDependsOn => new();

    public ScannerActivationCriteria GetActivationCriteria()
    {
        return new ScannerActivationCriteria
        {
            RequiredOpenPorts = new List<int> { 8006 }
        };
    }

    public async Task<ScanResult> ScanAsync(Entity host, ScannerContext context)
    {
        try
        {
            var client = context.CreateClientWithCertTracking(host);
            var token = context.Credentials.GetCredential("proxmox", "token");
            
            context.Logger.Debug($"Proxmox token present: {!string.IsNullOrEmpty(token)}");
            if (!string.IsNullOrEmpty(token))
            {
                var tokenPreview = token.Length > 20 ? $"{token.Substring(0, 20)}..." : token;
                context.Logger.Debug($"Token format: {tokenPreview} (length: {token.Length})");
            }
            context.Logger.Debug($"Attempting to connect to Proxmox at https://{host.Ip}:8006");
            
            var apiClient = new ProxmoxApiClient(client, host.Ip, token);

            // Verify this is actually Proxmox
            var version = await apiClient.GetVersionAsync();
            if (version == null)
            {
                context.Logger.Debug($"Proxmox version endpoint returned null for {host.Ip}");
                return ScanResult.Failed(host, "Proxmox API not responding", "Version endpoint returned null");
            }

            // Check for cluster membership
            var clusterStatus = await apiClient.GetClusterStatusAsync();
            bool isCluster = clusterStatus != null && !string.IsNullOrEmpty(clusterStatus.Name);
            
            if (isCluster)
            {
                var clusterId = $"proxmox-cluster-{clusterStatus!.Name}";
                
                // Check if we've already scanned this cluster
                if (context.Credentials.GetCredential("scanned_clusters", clusterId) != null)
                {
                    context.Logger.Info($"Proxmox host {host.Ip} is part of cluster '{clusterStatus.Name}' which was already scanned. Skipping.");
                    return ScanResult.Successful(new List<Entity>());
                }
                
                // Mark this cluster as scanned
                context.Credentials.SetCredential("scanned_clusters", clusterId, "true");
                context.Logger.Info($"Detected Proxmox cluster '{clusterStatus.Name}' with {clusterStatus.Nodes} nodes at {host.Ip}");
                
                host.Type = EntityType.ProxmoxCluster;
                host.Name = clusterStatus.Name;
                host.Id = $"proxmox-cluster-{clusterStatus.Name}";
                host.Ip = ""; // Cluster is a logical entity, no IP
                host.Metadata["proxmox_cluster"] = clusterStatus.Name;
                host.Metadata["proxmox_cluster_nodes"] = clusterStatus.Nodes ?? 0;
                host.Metadata["discovery_entry_point"] = host.Ip; // Remember which IP was used to discover this cluster
            }
            else
            {
                host.Type = EntityType.ProxmoxNode;
                host.Name = $"proxmox-{host.Ip}";
            }
            
            host.Metadata["proxmox_version"] = version.Version;
            host.Metadata["proxmox_release"] = version.Release;

            var entityTypeStr = isCluster ? "cluster" : "standalone node";
            context.Logger.Info($"Detected Proxmox {version.Version} {entityTypeStr} at {host.Ip}");

            // Get nodes with proper cluster information
            List<ProxmoxClusterNode> clusterNodes = new();
            List<ProxmoxNode> resourceNodes = new();
            
            if (isCluster)
            {
                clusterNodes = await apiClient.GetClusterNodesAsync();
                resourceNodes = await apiClient.GetNodesAsync();
                context.Logger.Info($"Found {clusterNodes.Count} cluster nodes and {resourceNodes.Count} resource nodes");
            }
            else
            {
                resourceNodes = await apiClient.GetNodesAsync();
            }
            
            var discoveredEntities = new List<Entity>();

            if (isCluster)
            {
                // Process cluster nodes using cluster status API for proper IPs
                foreach (var clusterNode in clusterNodes)
                {
                    context.Logger.Debug($"Processing cluster node: {clusterNode.Name} ({clusterNode.Ip})");

                    var nodeEntity = new Entity
                    {
                        Id = $"proxmox-node-{clusterNode.Name}",
                        Ip = clusterNode.Ip,
                        Type = EntityType.ProxmoxNode,
                        Name = clusterNode.Name,
                        ParentId = host.Id,
                        Status = clusterNode.Online ? ReachabilityStatus.Reachable : ReachabilityStatus.Unreachable,
                        Metadata = new Dictionary<string, object>
                        {
                            ["proxmox_node"] = clusterNode.Name,
                            ["proxmox_online"] = clusterNode.Online,
                            ["proxmox_local"] = clusterNode.Local,
                            ["proxmox_node_id"] = clusterNode.Id
                        }
                    };

                    // Add resource information if available
                    var resourceNode = resourceNodes.FirstOrDefault(r => r.Node == clusterNode.Name);
                    if (resourceNode != null)
                    {
                        nodeEntity.Metadata["proxmox_status"] = resourceNode.Status;
                        nodeEntity.Metadata["cpu"] = resourceNode.Cpu;
                        nodeEntity.Metadata["memory"] = resourceNode.Memory;
                    }

                    discoveredEntities.Add(nodeEntity);

                    // Process VMs and LXCs for this specific node
                    await ProcessNodeResources(apiClient, nodeEntity, clusterNode.Name, discoveredEntities, context);
                }
            }
            else
            {
                // Standalone node processing
                foreach (var node in resourceNodes)
                {
                    context.Logger.Debug($"Processing standalone node: {node.Node}");
                    
                    var nodeEntity = host;
                    nodeEntity.Type = EntityType.ProxmoxNode;
                    nodeEntity.Metadata["proxmox_node"] = node.Node;
                    nodeEntity.Metadata["proxmox_status"] = node.Status;
                    nodeEntity.Metadata["cpu"] = node.Cpu;
                    nodeEntity.Metadata["memory"] = node.Memory;

                    // Process VMs and LXCs for standalone node
                    await ProcessNodeResources(apiClient, nodeEntity, node.Node, discoveredEntities, context);
                }
            }

            var nodeCount = isCluster ? clusterNodes.Count : resourceNodes.Count;
            var vmCount = discoveredEntities.Count(e => e.Type == EntityType.Vm);
            var lxcCount = discoveredEntities.Count(e => e.Type == EntityType.Lxc);
            context.Logger.Info($"Proxmox scan found {nodeCount} nodes, {vmCount} VMs, {lxcCount} LXCs");

            return ScanResult.Successful(
                discoveredEntities,
                typeof(DockerHostScanner),
                typeof(PortainerScanner)
            );
        }
        catch (Exception ex)
        {
            return ScanResult.Failed(host, "Proxmox scan failed", ex.Message);
        }
    }

    public IEnumerable<Type> GetChildScannerTypes(ScanResult result)
    {
        return result.ChildScannerCandidates;
    }

    private async Task ProcessNodeResources(ProxmoxApiClient apiClient, Entity nodeEntity, string nodeName, List<Entity> discoveredEntities, ScannerContext context)
    {
        // Get VMs and LXCs
        var vms = await apiClient.GetVmsAsync(nodeName);
        var lxcs = await apiClient.GetLxcsAsync(nodeName);

        // Try SSH-based IP discovery if SSH is configured
        Dictionary<int, List<string>> vmSshIps = new();
        Dictionary<int, List<string>> lxcSshIps = new();
        
        if (TryGetSshConfig(context, out var sshConfig) && !string.IsNullOrEmpty(nodeEntity.Ip))
        {
            context.Logger.Debug($"Attempting SSH IP discovery for node {nodeName} at {nodeEntity.Ip}");
            
            var vmIds = vms.Select(v => v.VmId).ToList();
            var lxcIds = lxcs.Select(l => l.VmId).ToList();
            
            vmSshIps = await apiClient.GetVmIpAddressesViaSshAsync(
                nodeEntity.Ip, vmIds, sshConfig.Username!, sshConfig.Password, sshConfig.PrivateKeyPath);
            
            lxcSshIps = await apiClient.GetLxcIpAddressesViaSshAsync(
                nodeEntity.Ip, lxcIds, sshConfig.Username!, sshConfig.Password, sshConfig.PrivateKeyPath);
                
            if (vmSshIps.Any() || lxcSshIps.Any())
            {
                context.Logger.Info($"SSH IP discovery found {vmSshIps.Count} VM IPs and {lxcSshIps.Count} LXC IPs on node {nodeName}");
            }
        }

        // Process VMs
        foreach (var vm in vms)
        {
            // Try SSH-discovered IP first, then fall back to config
            string? vmIp = null;
            if (vmSshIps.TryGetValue(vm.VmId, out var sshIps) && sshIps.Any())
            {
                vmIp = sshIps.First(); // Use first discovered IP
            }
            else
            {
                // Fallback to config parsing
                var config = await apiClient.GetVmConfigAsync(nodeName, vm.VmId);
                vmIp = ExtractIpFromConfig(config);
            }

            var vmEntity = new Entity
            {
                Id = $"proxmox-vm-{nodeName}-{vm.VmId}",
                Ip = vmIp ?? "",
                Type = EntityType.Vm,
                Name = vm.Name,
                ParentId = nodeEntity.Id,
                Status = DetermineReachability(vmIp, context.DiscoveredIPs),
                Metadata = new Dictionary<string, object>
                {
                    ["proxmox_vmid"] = vm.VmId,
                    ["proxmox_node"] = nodeName,
                    ["proxmox_status"] = vm.Status,
                    ["cpu"] = vm.Cpu,
                    ["memory"] = vm.Memory
                }
            };

            // Add IP discovery method to metadata
            if (vmSshIps.ContainsKey(vm.VmId))
            {
                vmEntity.Metadata["ip_discovery"] = "ssh";
                if (vmSshIps[vm.VmId].Count > 1)
                {
                    vmEntity.Metadata["all_ips"] = vmSshIps[vm.VmId];
                }
            }
            else if (!string.IsNullOrEmpty(vmIp))
            {
                vmEntity.Metadata["ip_discovery"] = "config";
            }

            discoveredEntities.Add(vmEntity);
        }

        // Process LXCs
        foreach (var lxc in lxcs)
        {
            // Try SSH-discovered IP first
            string? lxcIp = null;
            if (lxcSshIps.TryGetValue(lxc.VmId, out var sshIps) && sshIps.Any())
            {
                lxcIp = sshIps.First(); // Use first discovered IP
            }

            var lxcEntity = new Entity
            {
                Id = $"proxmox-lxc-{nodeName}-{lxc.VmId}",
                Ip = lxcIp ?? "",
                Type = EntityType.Lxc,
                Name = lxc.Name,
                ParentId = nodeEntity.Id,
                Status = DetermineReachability(lxcIp, context.DiscoveredIPs),
                Metadata = new Dictionary<string, object>
                {
                    ["proxmox_vmid"] = lxc.VmId,
                    ["proxmox_node"] = nodeName,
                    ["proxmox_status"] = lxc.Status,
                    ["cpu"] = lxc.Cpu,
                    ["memory"] = lxc.Memory
                }
            };

            // Add IP discovery method to metadata
            if (lxcSshIps.ContainsKey(lxc.VmId))
            {
                lxcEntity.Metadata["ip_discovery"] = "ssh";
                if (lxcSshIps[lxc.VmId].Count > 1)
                {
                    lxcEntity.Metadata["all_ips"] = lxcSshIps[lxc.VmId];
                }
            }

            discoveredEntities.Add(lxcEntity);
        }
    }

    private bool TryGetSshConfig(ScannerContext context, out (string Username, string? Password, string? PrivateKeyPath) sshConfig)
    {
        sshConfig = default;
        
        // Try to get SSH config from credentials store
        var sshUsername = context.Credentials.GetCredential("ssh", "username");
        var sshPassword = context.Credentials.GetCredential("ssh", "password");
        var sshKeyPath = context.Credentials.GetCredential("ssh", "private_key_path");
        
        if (!string.IsNullOrEmpty(sshUsername))
        {
            sshConfig = (sshUsername, sshPassword, sshKeyPath);
            return true;
        }
        
        return false;
    }

    private string? ExtractIpFromConfig(ProxmoxVmConfig? config)
    {
        if (config == null) return null;

        // Try to extract IP from net0 configuration
        // Format: virtio=XX:XX:XX:XX:XX:XX,bridge=vmbr0,firewall=1,ip=192.168.1.100/24
        var nets = new[] { config.Net0, config.Net1 }.Where(n => !string.IsNullOrEmpty(n));
        foreach (var net in nets)
        {
            if (string.IsNullOrEmpty(net)) continue;

            var parts = net.Split(',');
            foreach (var part in parts)
            {
                if (part.StartsWith("ip="))
                {
                    var ip = part.Substring(3).Split('/')[0];
                    return ip;
                }
            }
        }

        return null;
    }

    private ReachabilityStatus DetermineReachability(string? ip, HashSet<string> discoveredIPs)
    {
        if (string.IsNullOrEmpty(ip))
        {
            return ReachabilityStatus.Unverified;
        }

        return discoveredIPs.Contains(ip) 
            ? ReachabilityStatus.Reachable 
            : ReachabilityStatus.Unverified;
    }
}
