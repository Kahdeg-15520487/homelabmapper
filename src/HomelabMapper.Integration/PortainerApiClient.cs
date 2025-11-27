using System.Text.Json;
using System.Text.Json.Serialization;

namespace HomelabMapper.Integration;

public class PortainerApiClient
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string? _token;

    public PortainerApiClient(HttpClient httpClient, string host, int port = 9000, string? token = null)
    {
        _httpClient = httpClient;
        _baseUrl = $"https://{host}:{port}/api";
        _token = token;
    }

    public async Task<PortainerStatus?> GetStatusAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/status");
            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<PortainerStatus>(content);
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<PortainerEndpoint>> GetEndpointsAsync()
    {
        try
        {
            var request = CreateAuthenticatedRequest($"{_baseUrl}/endpoints");
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return new List<PortainerEndpoint>();

            var content = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<List<PortainerEndpoint>>(content) ?? new List<PortainerEndpoint>();
        }
        catch
        {
            return new List<PortainerEndpoint>();
        }
    }

    public async Task<List<PortainerStack>> GetStacksAsync(int endpointId = 1)
    {
        try
        {
            var request = CreateAuthenticatedRequest($"{_baseUrl}/stacks");
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return new List<PortainerStack>();

            var content = await response.Content.ReadAsStringAsync();
            var allStacks = JsonSerializer.Deserialize<List<PortainerStack>>(content) ?? new List<PortainerStack>();
            return allStacks.Where(s => s.EndpointId == endpointId).ToList();
        }
        catch
        {
            return new List<PortainerStack>();
        }
    }

    public async Task<List<DockerContainer>> GetContainersAsync(int endpointId = 1)
    {
        try
        {
            var request = CreateAuthenticatedRequest($"{_baseUrl}/endpoints/{endpointId}/docker/containers/json?all=true");
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return new List<DockerContainer>();

            var content = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<List<DockerContainer>>(content) ?? new List<DockerContainer>();
        }
        catch
        {
            return new List<DockerContainer>();
        }
    }

    private HttpRequestMessage CreateAuthenticatedRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(_token))
        {
            request.Headers.Add("X-API-Key", _token);
        }
        return request;
    }
}

public class PortainerStatus
{
    [JsonPropertyName("Version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("InstanceID")]
    public string InstanceID { get; set; } = string.Empty;
}

public class PortainerEndpoint
{
    [JsonPropertyName("Id")]
    public int Id { get; set; }

    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Type")]
    public int Type { get; set; }

    [JsonPropertyName("URL")]
    public string URL { get; set; } = string.Empty;

    [JsonPropertyName("Status")]
    public int Status { get; set; }
}

public class PortainerStack
{
    [JsonPropertyName("Id")]
    public int Id { get; set; }

    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Type")]
    public int Type { get; set; }

    [JsonPropertyName("EndpointId")]
    public int EndpointId { get; set; }

    [JsonPropertyName("Status")]
    public int Status { get; set; }

    [JsonPropertyName("ResourceControl")]
    public PortainerResourceControl? ResourceControl { get; set; }
}

public class PortainerResourceControl
{
    [JsonPropertyName("ResourceId")]
    public string ResourceId { get; set; } = string.Empty;
}
