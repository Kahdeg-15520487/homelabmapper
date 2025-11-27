namespace HomelabMapper.Core.Models;

public class Entity
{
    public string Id { get; set; } = string.Empty;
    public string Ip { get; set; } = string.Empty;
    public EntityType Type { get; set; } = EntityType.Unknown;
    public string Name { get; set; } = string.Empty;
    public string? ParentId { get; set; }
    public ReachabilityStatus Status { get; set; } = ReachabilityStatus.Unverified;
    public CertificateInfo? Certificate { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
    
    public List<int> OpenPorts { get; set; } = new();
    public Dictionary<string, string>? HttpHeaders { get; set; }
}
