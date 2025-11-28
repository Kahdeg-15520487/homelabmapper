namespace HomelabMapper.Core.Interfaces;

public interface IHostScanner
{
    string ScannerName { get; }
    int Priority { get; }
    List<string> DependsOn { get; }
    List<string> OptionalDependsOn { get; }
    
    ScannerActivationCriteria GetActivationCriteria();
    Task<ScanResult> ScanAsync(Models.Entity host, ScannerContext context);
    IEnumerable<Type> GetChildScannerTypes(ScanResult result);
}
