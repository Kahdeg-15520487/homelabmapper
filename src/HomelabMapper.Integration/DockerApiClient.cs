using System.Text.Json;
using System.Text.Json.Serialization;

namespace HomelabMapper.Integration;

public class DockerApiClient
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    public DockerApiClient(HttpClient httpClient, string host, int port = 2375)
    {
        _httpClient = httpClient;
        _baseUrl = $"http://{host}:{port}";
    }

    public async Task<DockerVersion?> GetVersionAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/version");
            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<DockerVersion>(content);
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<DockerContainer>> GetContainersAsync(bool showAll = true)
    {
        try
        {
            var url = $"{_baseUrl}/containers/json?all={showAll.ToString().ToLower()}";
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return new List<DockerContainer>();

            var content = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<List<DockerContainer>>(content) ?? new List<DockerContainer>();
        }
        catch
        {
            return new List<DockerContainer>();
        }
    }

    public async Task<DockerContainerInspect?> InspectContainerAsync(string containerId)
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/containers/{containerId}/json");
            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<DockerContainerInspect>(content);
        }
        catch
        {
            return null;
        }
    }
}

public class DockerVersion
{
    [JsonPropertyName("Version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("ApiVersion")]
    public string ApiVersion { get; set; } = string.Empty;

    [JsonPropertyName("Os")]
    public string Os { get; set; } = string.Empty;

    [JsonPropertyName("Arch")]
    public string Arch { get; set; } = string.Empty;
}

public class DockerContainer
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("Names")]
    public List<string> Names { get; set; } = new();

    [JsonPropertyName("Image")]
    public string Image { get; set; } = string.Empty;

    [JsonPropertyName("State")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("Status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("Ports")]
    public List<DockerPort> Ports { get; set; } = new();

    [JsonPropertyName("NetworkSettings")]
    public DockerNetworkSettings? NetworkSettings { get; set; }

    [JsonPropertyName("Labels")]
    public Dictionary<string, string> Labels { get; set; } = new();
}

public class DockerPort
{
    [JsonPropertyName("PrivatePort")]
    public int PrivatePort { get; set; }

    [JsonPropertyName("PublicPort")]
    public int? PublicPort { get; set; }

    [JsonPropertyName("Type")]
    public string Type { get; set; } = string.Empty;
}

public class DockerNetworkSettings
{
    [JsonPropertyName("Networks")]
    public Dictionary<string, DockerNetwork> Networks { get; set; } = new();
}

public class DockerNetwork
{
    [JsonPropertyName("IPAddress")]
    public string IPAddress { get; set; } = string.Empty;

    [JsonPropertyName("Gateway")]
    public string Gateway { get; set; } = string.Empty;

    [JsonPropertyName("NetworkID")]
    public string NetworkID { get; set; } = string.Empty;
}

public class DockerContainerInspect
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("State")]
    public DockerContainerState State { get; set; } = new();

    [JsonPropertyName("Config")]
    public DockerContainerConfig Config { get; set; } = new();

    [JsonPropertyName("NetworkSettings")]
    public DockerContainerNetworkSettings NetworkSettings { get; set; } = new();

    [JsonPropertyName("HostConfig")]
    public DockerHostConfig HostConfig { get; set; } = new();
}

public class DockerContainerState
{
    [JsonPropertyName("Status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("Running")]
    public bool Running { get; set; }
}

public class DockerContainerConfig
{
    [JsonPropertyName("Image")]
    public string Image { get; set; } = string.Empty;

    [JsonPropertyName("Labels")]
    public Dictionary<string, string> Labels { get; set; } = new();
}

public class DockerContainerNetworkSettings
{
    [JsonPropertyName("Networks")]
    public Dictionary<string, DockerNetwork> Networks { get; set; } = new();

    [JsonPropertyName("IPAddress")]
    public string IPAddress { get; set; } = string.Empty;
}

public class DockerHostConfig
{
    [JsonPropertyName("NetworkMode")]
    public string NetworkMode { get; set; } = string.Empty;
}
