namespace HomelabMapper.Core.Models;

public class CertificateInfo
{
    public bool IsSelfSigned { get; set; }
    public string Issuer { get; set; } = string.Empty;
    public DateTime Expiry { get; set; }
    public string Fingerprint { get; set; } = string.Empty;
}
