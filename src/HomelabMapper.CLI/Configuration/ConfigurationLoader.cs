using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace HomelabMapper.CLI.Configuration;

public class ScanConfiguration
{
    public ScanSettings Scan { get; set; } = new();
    public HintsSettings? Hints { get; set; }
    public CredentialsSettings Credentials { get; set; } = new();
    public DiffSettings Diff { get; set; } = new();
    public OutputSettings Output { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
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

public class HintsSettings
{
    public List<ServiceHint> Services { get; set; } = new();
}

public class ServiceHint
{
    public string Ip { get; set; } = string.Empty;
    public int? Port { get; set; }
    public string? Name { get; set; }
    public string? Type { get; set; }
    public string? TokenEnv { get; set; }
}

public class CredentialsSettings
{
    public ServiceCredentials Proxmox { get; set; } = new();
    public ServiceCredentials Portainer { get; set; } = new();
    public ServiceCredentials Docker { get; set; } = new();
    public ServiceCredentials Unraid { get; set; } = new();
    public ServiceCredentials Router { get; set; } = new();
    public SshSettings? Ssh { get; set; }
}

public class ServiceCredentials
{
    public string? TokenEnv { get; set; }
    public string? Token { get; set; }
    public string? ApiKeyEnv { get; set; }
    public string? ApiKey { get; set; }
    public string? Username { get; set; }
    public string? PasswordEnv { get; set; }
    public string? Password { get; set; }
}

public class SshSettings
{
    public string? Username { get; set; }
    public string? PasswordEnv { get; set; }
    public string? Password { get; set; }
    public string? PrivateKeyPath { get; set; }
    public bool Enabled { get; set; } = false;
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

public class LoggingSettings
{
    public string Level { get; set; } = "Info";
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
        ResolveServiceCredentials(config.Credentials.Router);
        ResolveSshSettings(config.Credentials.Ssh);
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

        if (!string.IsNullOrEmpty(creds.PasswordEnv))
        {
            creds.Password = Environment.GetEnvironmentVariable(creds.PasswordEnv);
        }
    }

    private static void ResolveSshSettings(SshSettings? ssh)
    {
        if (ssh == null) return;
        
        if (!string.IsNullOrEmpty(ssh.PasswordEnv))
        {
            ssh.Password = Environment.GetEnvironmentVariable(ssh.PasswordEnv);
        }
    }
}
