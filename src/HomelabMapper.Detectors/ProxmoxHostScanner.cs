using HomelabMapper.Core.Interfaces;
using HomelabMapper.Core.Models;
using HomelabMapper.Integration;

namespace HomelabMapper.Detectors;

public class ProxmoxHostScanner : IHostScanner
{
    public string ScannerName => "Proxmox";
    public int Priority => 10;
    public List<string> DependsOn => new();

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
                host.Metadata["proxmox_cluster"] = clusterStatus.Name;
                host.Metadata["proxmox_cluster_nodes"] = clusterStatus.Nodes ?? 0;
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

            // Get nodes
            var nodes = await apiClient.GetNodesAsync();
            var discoveredEntities = new List<Entity>();

            foreach (var node in nodes)
            {
                context.Logger.Debug($"Processing Proxmox node: {node.Node}");

                Entity nodeEntity;
                
                if (isCluster)
                {
                    // In a cluster, create node entities under the cluster
                    nodeEntity = new Entity
                    {
                        Id = $"proxmox-node-{node.Node}",
                        Ip = host.Ip, // Nodes share the cluster IP
                        Type = EntityType.ProxmoxNode,
                        Name = node.Node,
                        ParentId = host.Id,
                        Status = ReachabilityStatus.Reachable,
                        Metadata = new Dictionary<string, object>
                        {
                            ["proxmox_node"] = node.Node,
                            ["proxmox_status"] = node.Status,
                            ["cpu"] = node.Cpu,
                            ["memory"] = node.Memory
                        }
                    };
                    discoveredEntities.Add(nodeEntity);
                }
                else
                {
                    // Standalone node - use the host entity itself
                    nodeEntity = host;
                    nodeEntity.Metadata["proxmox_node"] = node.Node;
                    nodeEntity.Metadata["proxmox_status"] = node.Status;
                    nodeEntity.Metadata["cpu"] = node.Cpu;
                    nodeEntity.Metadata["memory"] = node.Memory;
                }

                // Get VMs
                var vms = await apiClient.GetVmsAsync(node.Node);
                foreach (var vm in vms)
                {
                    // Try to get VM network configuration
                    var config = await apiClient.GetVmConfigAsync(node.Node, vm.VmId);
                    var vmIp = ExtractIpFromConfig(config);

                    var vmEntity = new Entity
                    {
                        Id = $"proxmox-vm-{node.Node}-{vm.VmId}",
                        Ip = vmIp ?? "",
                        Type = EntityType.Vm,
                        Name = vm.Name,
                        ParentId = nodeEntity.Id,
                        Status = DetermineReachability(vmIp, context.DiscoveredIPs),
                        Metadata = new Dictionary<string, object>
                        {
                            ["proxmox_vmid"] = vm.VmId,
                            ["proxmox_node"] = node.Node,
                            ["proxmox_status"] = vm.Status,
                            ["cpu"] = vm.Cpu,
                            ["memory"] = vm.Memory
                        }
                    };

                    if (!string.IsNullOrEmpty(vmIp))
                    {
                        vmEntity.Metadata["api_reported_ip"] = vmIp;
                    }

                    discoveredEntities.Add(vmEntity);
                }

                // Get LXCs
                var lxcs = await apiClient.GetLxcsAsync(node.Node);
                foreach (var lxc in lxcs)
                {
                    var lxcEntity = new Entity
                    {
                        Id = $"proxmox-lxc-{node.Node}-{lxc.VmId}",
                        Ip = "",
                        Type = EntityType.Lxc,
                        Name = lxc.Name,
                        ParentId = nodeEntity.Id,
                        Status = ReachabilityStatus.Unverified,
                        Metadata = new Dictionary<string, object>
                        {
                            ["proxmox_vmid"] = lxc.VmId,
                            ["proxmox_node"] = node.Node,
                            ["proxmox_status"] = lxc.Status,
                            ["cpu"] = lxc.Cpu,
                            ["memory"] = lxc.Memory
                        }
                    };

                    discoveredEntities.Add(lxcEntity);
                }
            }

            var nodeCount = nodes.Count;
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
