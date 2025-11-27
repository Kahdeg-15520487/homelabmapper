namespace HomelabMapper.Core.Interfaces;

public class ScanResult
{
    public bool Success { get; set; }
    public List<Models.Entity> DiscoveredEntities { get; set; } = new();
    public Type[] ChildScannerCandidates { get; set; } = Array.Empty<Type>();
    public string? ErrorMessage { get; set; }
    public string? ErrorDetails { get; set; }
    
    public static ScanResult Failed(Models.Entity host, string message, string details)
    {
        return new ScanResult
        {
            Success = false,
            ErrorMessage = message,
            ErrorDetails = details
        };
    }
    
    public static ScanResult Successful(List<Models.Entity> entities, params Type[] childScanners)
    {
        return new ScanResult
        {
            Success = true,
            DiscoveredEntities = entities,
            ChildScannerCandidates = childScanners
        };
    }
}
