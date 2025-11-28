using System.Net.Http.Headers;
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
        
        // Set authorization header if token is provided
        if (!string.IsNullOrEmpty(token))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("PVEAPIToken", token);
        }
    }

    public async Task<ProxmoxVersion?> GetVersionAsync()
    {
        try
        {
            // Try with authentication first
            var request = CreateAuthenticatedRequest($"{_baseUrl}/version");
            var response = await _httpClient.SendAsync(request);
            
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[DEBUG] Proxmox version check failed: HTTP {response.StatusCode}");
                var errorContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"[DEBUG] Response body: {errorContent.Substring(0, Math.Min(200, errorContent.Length))}");
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<ProxmoxApiResponse<ProxmoxVersion>>(content);
            return result?.Data;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] Proxmox version check exception: {ex.GetType().Name} - {ex.Message}");
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

    public async Task<ProxmoxClusterStatus?> GetClusterStatusAsync()
    {
        try
        {
            var request = CreateAuthenticatedRequest($"{_baseUrl}/cluster/status");
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<ProxmoxApiResponse<List<ProxmoxClusterStatus>>>(content);
            return result?.Data?.FirstOrDefault(s => s.Type == "cluster");
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<ProxmoxClusterNode>> GetClusterNodesAsync()
    {
        try
        {
            var request = CreateAuthenticatedRequest($"{_baseUrl}/cluster/status");
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return new List<ProxmoxClusterNode>();

            var content = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<ProxmoxApiResponse<List<ProxmoxClusterStatus>>>(content);
            
            // Filter for node entries in cluster status
            var nodeEntries = result?.Data?.Where(s => s.Type == "node").ToList() ?? new List<ProxmoxClusterStatus>();
            
            return nodeEntries.Select(n => new ProxmoxClusterNode
            {
                Name = n.Name,
                Ip = n.Ip ?? "",
                Online = n.Online == 1,
                Local = n.Local == 1,
                Id = n.Id ?? ""
            }).ToList();
        }
        catch
        {
            return new List<ProxmoxClusterNode>();
        }
    }

    private HttpRequestMessage CreateAuthenticatedRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
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

public class ProxmoxClusterStatus
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("nodes")]
    public int? Nodes { get; set; }

    [JsonPropertyName("quorate")]
    public int? Quorate { get; set; }

    [JsonPropertyName("ip")]
    public string? Ip { get; set; }

    [JsonPropertyName("online")]
    public int? Online { get; set; }

    [JsonPropertyName("local")]
    public int? Local { get; set; }
}

public class ProxmoxClusterNode
{
    public string Name { get; set; } = string.Empty;
    public string Ip { get; set; } = string.Empty;
    public bool Online { get; set; }
    public bool Local { get; set; }
    public string Id { get; set; } = string.Empty;
}
