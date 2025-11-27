using System.Text.Json;
using System.Text.Json.Serialization;

namespace HomelabMapper.Integration;

public class ProxmoxApiClient
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string? _token;

    public ProxmoxApiClient(HttpClient httpClient, string host, string? token = null)
    {
        _httpClient = httpClient;
        _baseUrl = $"https://{host}:8006/api2/json";
        _token = token;
    }

    public async Task<ProxmoxVersion?> GetVersionAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/version");
            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<ProxmoxApiResponse<ProxmoxVersion>>(content);
            return result?.Data;
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<ProxmoxNode>> GetNodesAsync()
    {
        try
        {
            var request = CreateAuthenticatedRequest($"{_baseUrl}/nodes");
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return new List<ProxmoxNode>();

            var content = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<ProxmoxApiResponse<List<ProxmoxNode>>>(content);
            return result?.Data ?? new List<ProxmoxNode>();
        }
        catch
        {
            return new List<ProxmoxNode>();
        }
    }

    public async Task<List<ProxmoxVm>> GetVmsAsync(string nodeName)
    {
        try
        {
            var request = CreateAuthenticatedRequest($"{_baseUrl}/nodes/{nodeName}/qemu");
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return new List<ProxmoxVm>();

            var content = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<ProxmoxApiResponse<List<ProxmoxVm>>>(content);
            return result?.Data ?? new List<ProxmoxVm>();
        }
        catch
        {
            return new List<ProxmoxVm>();
        }
    }

    public async Task<List<ProxmoxLxc>> GetLxcsAsync(string nodeName)
    {
        try
        {
            var request = CreateAuthenticatedRequest($"{_baseUrl}/nodes/{nodeName}/lxc");
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return new List<ProxmoxLxc>();

            var content = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<ProxmoxApiResponse<List<ProxmoxLxc>>>(content);
            return result?.Data ?? new List<ProxmoxLxc>();
        }
        catch
        {
            return new List<ProxmoxLxc>();
        }
    }

    public async Task<ProxmoxVmConfig?> GetVmConfigAsync(string nodeName, int vmId)
    {
        try
        {
            var request = CreateAuthenticatedRequest($"{_baseUrl}/nodes/{nodeName}/qemu/{vmId}/config");
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<ProxmoxApiResponse<ProxmoxVmConfig>>(content);
            return result?.Data;
        }
        catch
        {
            return null;
        }
    }

    private HttpRequestMessage CreateAuthenticatedRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(_token))
        {
            request.Headers.Add("Authorization", $"PVEAPIToken={_token}");
        }
        return request;
    }
}

public class ProxmoxApiResponse<T>
{
    [JsonPropertyName("data")]
    public T? Data { get; set; }
}

public class ProxmoxVersion
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("release")]
    public string Release { get; set; } = string.Empty;
}

public class ProxmoxNode
{
    [JsonPropertyName("node")]
    public string Node { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("cpu")]
    public double Cpu { get; set; }

    [JsonPropertyName("mem")]
    public long Memory { get; set; }
}

public class ProxmoxVm
{
    [JsonPropertyName("vmid")]
    public int VmId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("cpu")]
    public double Cpu { get; set; }

    [JsonPropertyName("mem")]
    public long Memory { get; set; }
}

public class ProxmoxLxc
{
    [JsonPropertyName("vmid")]
    public int VmId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("cpu")]
    public double Cpu { get; set; }

    [JsonPropertyName("mem")]
    public long Memory { get; set; }
}

public class ProxmoxVmConfig
{
    [JsonPropertyName("net0")]
    public string? Net0 { get; set; }

    [JsonPropertyName("net1")]
    public string? Net1 { get; set; }
}
