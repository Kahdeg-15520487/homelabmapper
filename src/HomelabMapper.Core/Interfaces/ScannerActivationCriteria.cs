namespace HomelabMapper.Core.Interfaces;

public class ScannerActivationCriteria
{
    public List<int> RequiredOpenPorts { get; set; } = new();
    public Dictionary<string, string> RequiredHttpHeaders { get; set; } = new();
    public List<string> RequiredUrlPatterns { get; set; } = new();
    public Func<Models.Entity, bool>? CustomPredicate { get; set; }
}
