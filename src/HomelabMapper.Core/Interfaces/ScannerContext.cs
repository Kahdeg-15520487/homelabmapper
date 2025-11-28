namespace HomelabMapper.Core.Interfaces;

public interface ICredentialStore
{
    string? GetCredential(string service, string key);
    void SetCredential(string service, string key, string value);
}

public interface ILogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
    void Debug(string message);
}

public class ScannerContext
{
    public HttpClient Client { get; set; } = null!;
    public ICredentialStore Credentials { get; set; } = null!;
    public ILogger Logger { get; set; } = null!;
    public HashSet<string> DiscoveredIPs { get; set; } = new();
    public List<Models.Entity> AllEntities { get; set; } = new();
    
    public HttpClient CreateClientWithCertTracking(Models.Entity entity)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
            {
                if (errors != System.Net.Security.SslPolicyErrors.None && cert != null)
                {
                    entity.Certificate = new Models.CertificateInfo
                    {
                        IsSelfSigned = true,
                        Issuer = cert.Issuer,
                        Expiry = cert.NotAfter,
                        Fingerprint = cert.GetCertHashString()
                    };
                }
                return true; // Accept all certificates
            }
        };
        
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
    }
}
