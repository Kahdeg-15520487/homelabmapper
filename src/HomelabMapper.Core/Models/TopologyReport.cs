namespace HomelabMapper.Core.Models;

public class TopologyReport
{
    public DateTime Timestamp { get; set; }
    public string ScanId { get; set; } = string.Empty;
    public List<string> Subnets { get; set; } = new();
    public List<Entity> Entities { get; set; } = new();
    public List<Conflict> Conflicts { get; set; } = new();
    public ScanSummary Summary { get; set; } = new();
}

public class ScanSummary
{
    public int TotalEntities { get; set; }
    public Dictionary<EntityType, int> EntitiesByType { get; set; } = new();
    public Dictionary<ReachabilityStatus, int> EntitiesByStatus { get; set; } = new();
}
