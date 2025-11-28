using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace HomelabMapper.CLI.Configuration;

public class ScanConfiguration
{
    public ScanSettings Scan { get; set; } = new();
    public CredentialsSettings Credentials { get; set; } = new();
    public DiffSettings Diff { get; set; } = new();
    public OutputSettings Output { get; set; } = new();
}

public class ScanSettings
{
    public List<string> Subnets { get; set; } = new();
    public TimeoutSettings TimeoutMs { get; set; } = new();
    public int ParallelScans { get; set; } = 50;
    public SslSettings Ssl { get; set; } = new();
}

public class TimeoutSettings
{
    public int Ping { get; set; } = 500;
    public int Http { get; set; } = 3000;
}

public class SslSettings
{
    public bool AcceptSelfSigned { get; set; } = true;
    public bool LogCertificateDetails { get; set; } = true;
}

public class CredentialsSettings
{
    public ServiceCredentials Proxmox { get; set; } = new();
    public ServiceCredentials Portainer { get; set; } = new();
    public ServiceCredentials Docker { get; set; } = new();
    public ServiceCredentials Unraid { get; set; } = new();
}

public class ServiceCredentials
{
    public string? TokenEnv { get; set; }
    public string? Token { get; set; }
    public string? ApiKeyEnv { get; set; }
    public string? ApiKey { get; set; }
}

public class DiffSettings
{
    public bool Enabled { get; set; } = true;
    public string HistoryDir { get; set; } = ".homelabmapper/scans";
    public int KeepLast { get; set; } = 10;
}

public class OutputSettings
{
    public string Json { get; set; } = "scan-results.json";
    public string Markdown { get; set; } = "report.md";
    public string Mermaid { get; set; } = "topology.mmd";
    public string DiffReport { get; set; } = "changes.md";
}

public class ConfigurationLoader
{
    public static ScanConfiguration Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new ScanConfiguration();
        }

        var yaml = File.ReadAllText(filePath);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        var config = deserializer.Deserialize<ScanConfiguration>(yaml);

        // Resolve environment variables
        ResolveEnvironmentVariables(config);

        return config;
    }

    private static void ResolveEnvironmentVariables(ScanConfiguration config)
    {
        ResolveServiceCredentials(config.Credentials.Proxmox);
        ResolveServiceCredentials(config.Credentials.Portainer);
        ResolveServiceCredentials(config.Credentials.Docker);
        ResolveServiceCredentials(config.Credentials.Unraid);
    }

    private static void ResolveServiceCredentials(ServiceCredentials? creds)
    {
        if (creds == null) return;
        
        if (!string.IsNullOrEmpty(creds.TokenEnv))
        {
            creds.Token = Environment.GetEnvironmentVariable(creds.TokenEnv);
        }

        if (!string.IsNullOrEmpty(creds.ApiKeyEnv))
        {
            creds.ApiKey = Environment.GetEnvironmentVariable(creds.ApiKeyEnv);
        }
    }
}
