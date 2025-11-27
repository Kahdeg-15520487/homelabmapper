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
            var apiClient = new ProxmoxApiClient(client, host.Ip, token);

            // Verify this is actually Proxmox
            var version = await apiClient.GetVersionAsync();
            if (version == null)
            {
                return ScanResult.Failed(host, "Proxmox API not responding", "Version endpoint returned null");
            }

            host.Type = EntityType.Proxmox;
            host.Name = $"proxmox-{host.Ip}";
            host.Metadata["proxmox_version"] = version.Version;
            host.Metadata["proxmox_release"] = version.Release;

            context.Logger.Info($"Detected Proxmox {version.Version} at {host.Ip}");

            // Get nodes
            var nodes = await apiClient.GetNodesAsync();
            var discoveredEntities = new List<Entity>();

            foreach (var node in nodes)
            {
                context.Logger.Debug($"Processing Proxmox node: {node.Node}");

                // Get VMs
                var vms = await apiClient.GetVmsAsync(node.Node);
                foreach (var vm in vms)
                {
                    // Try to get VM network configuration
                    var config = await apiClient.GetVmConfigAsync(node.Node, vm.VmId);
                    var vmIp = ExtractIpFromConfig(config);

                    var vmEntity = new Entity
                    {
                        Id = $"proxmox-vm-{vm.VmId}",
                        Ip = vmIp ?? "",
                        Type = EntityType.Vm,
                        Name = vm.Name,
                        ParentId = host.Id,
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
                        Id = $"proxmox-lxc-{lxc.VmId}",
                        Ip = "",
                        Type = EntityType.Lxc,
                        Name = lxc.Name,
                        ParentId = host.Id,
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

            context.Logger.Info($"Proxmox scan found {discoveredEntities.Count} VMs/LXCs");

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
