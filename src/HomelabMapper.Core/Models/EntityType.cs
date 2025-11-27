namespace HomelabMapper.Core.Models;

public enum EntityType
{
    Unknown,
    Proxmox,
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
