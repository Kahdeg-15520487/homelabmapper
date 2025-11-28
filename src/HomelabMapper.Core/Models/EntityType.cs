namespace HomelabMapper.Core.Models;

public enum EntityType
{
    Unknown,
    Proxmox,
    ProxmoxCluster,
    ProxmoxNode,
    Vm,
    Lxc,
    DockerHost,
    Container,
    PortainerService,
    PortainerStack,
    Nas,
    Unraid,
    Service
}
