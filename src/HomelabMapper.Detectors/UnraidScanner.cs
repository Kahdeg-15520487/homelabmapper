using System.Text;
using System.Text.Json;
using HomelabMapper.Core.Interfaces;
using HomelabMapper.Core.Models;

namespace HomelabMapper.Detectors;

public class UnraidScanner : IHostScanner
{
    public string ScannerName => "Unraid";
    public int Priority => 35; // After Portainer (30)
    public List<string> DependsOn => new();
    public List<string> OptionalDependsOn => new();

    public ScannerActivationCriteria GetActivationCriteria()
    {
        return new ScannerActivationCriteria
        {
            RequiredOpenPorts = new List<int> { 80, 443 },
            RequiredHttpHeaders = new Dictionary<string, string>
            {
                { "Location", "/Main" },
                { "Content-Security-Policy", "connect.myunraid.net" }
            }
        };
    }

    public async Task<ScanResult> ScanAsync(Entity host, ScannerContext context)
    {
        try
        {
            var apiKey = GetApiKey(context);
            if (string.IsNullOrEmpty(apiKey))
            {
                return ScanResult.Failed(host, "Unraid API key not found", "Set UNRAID_API_KEY environment variable or configure unraid.api_key_env");
            }

            var containers = await GetDockerContainersAsync(host.Ip, apiKey, context);
            if (containers == null)
            {
                return ScanResult.Failed(host, "Failed to query Unraid GraphQL API", "Check API key and network connectivity");
            }

            context.Logger.Info($"Detected Unraid server at {host.Ip} with {containers.Count} containers");

            // Mark host as Unraid type
            host.Type = EntityType.Unraid;
            host.Metadata["unraid_detected"] = "true";
            host.Metadata["unraid_container_count"] = containers.Count.ToString();

            // Find existing Docker containers at this IP and enrich them with Unraid metadata
            var existingContainers = context.AllEntities
                .Where(e => e.Type == EntityType.Container && e.Ip == host.Ip)
                .ToList();

            foreach (var container in containers)
            {
                // Try to match with existing Docker-discovered container
                var existingContainer = existingContainers.FirstOrDefault(ec =>
                    ec.Metadata.ContainsKey("docker_id") &&
                    ec.Metadata["docker_id"] as string == container.Id
                );

                if (existingContainer != null)
                {
                    // Enrich existing container with Unraid metadata
                    existingContainer.Metadata["unraid_managed"] = true;
                    existingContainer.Metadata["unraid_container_id"] = container.Id;
                    existingContainer.Metadata["unraid_image"] = container.Image ?? string.Empty;
                    existingContainer.Metadata["unraid_state"] = container.State ?? string.Empty;
                    
                    context.Logger.Debug($"Enriched container {existingContainer.Name} with Unraid metadata");
                }
                else
                {
                    context.Logger.Debug($"Found Unraid container not in Docker scan: {GetContainerName(container.Names)} ({container.Id})");
                }
            }

            // Return empty list since we're just enriching existing entities
            // The ReparentContainersToUnraid correlation will handle the parent-child relationship
            return ScanResult.Successful(new List<Entity>());
        }
        catch (Exception ex)
        {
            return ScanResult.Failed(host, "Unraid scan failed", ex.Message);
        }
    }

    public IEnumerable<Type> GetChildScannerTypes(ScanResult result)
    {
        return Array.Empty<Type>();
    }

    private string? GetApiKey(ScannerContext context)
    {
        // Try to get API key from credentials
        var apiKey = context.Credentials.GetCredential("unraid", "api_key");
        if (!string.IsNullOrEmpty(apiKey))
        {
            context.Logger.Debug("Unraid API key loaded from credentials");
            return apiKey;
        }

        context.Logger.Debug("Unraid API key not found in credentials");
        return null;
    }

    private async Task<List<UnraidContainer>?> GetDockerContainersAsync(string hostIp, string apiKey, ScannerContext context)
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("x-api-key", apiKey);
            client.Timeout = TimeSpan.FromSeconds(10);

            var graphqlQuery = new
            {
                query = @"
                query ExampleQuery {
                    docker {
                        id
                        containers {
                            id
                            names
                            image
                            imageId
                            ports {
                                ip
                                privatePort
                                publicPort
                                type
                            }
                            labels
                            state
                            status
                        }
                    }
                }"
            };

            var json = JsonSerializer.Serialize(graphqlQuery);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            // Use HTTP for Unraid GraphQL API
            var url = $"http://{hostIp}/graphql";
            
            try
            {
                context.Logger.Debug($"Querying Unraid GraphQL API at {url}");
                var response = await client.PostAsync(url, content);
                
                if (response.IsSuccessStatusCode)
                {
                    var responseJson = await response.Content.ReadAsStringAsync();
                    context.Logger.Debug($"Unraid API response: {responseJson}");
                    
                    var graphqlResponse = JsonSerializer.Deserialize<UnraidGraphQLResponse>(responseJson);
                    
                    if (graphqlResponse?.Data?.Docker?.Containers != null)
                    {
                        context.Logger.Debug($"Successfully retrieved {graphqlResponse.Data.Docker.Containers.Count} containers from Unraid API");
                        return graphqlResponse.Data.Docker.Containers;
                    }
                    else
                    {
                        context.Logger.Debug($"Failed to parse containers from response. Data: {graphqlResponse?.Data}, Docker: {graphqlResponse?.Data?.Docker}");
                    }
                }
                else
                {
                    context.Logger.Debug($"Unraid API returned {response.StatusCode}: {response.ReasonPhrase}");
                }
            }
            catch (HttpRequestException ex)
            {
                context.Logger.Debug($"HTTP error for {url}: {ex.Message}");
            }

            return null;
        }
        catch (Exception ex)
        {
            context.Logger.Debug($"Error querying Unraid GraphQL API: {ex.Message}");
            return null;
        }
    }

    private string GetContainerName(List<string>? names)
    {
        if (names == null || !names.Any())
            return "unnamed-container";
            
        // Docker names often start with '/', remove it
        var name = names.FirstOrDefault() ?? "unnamed-container";
        return name.StartsWith("/") ? name[1..] : name;
    }

    private ReachabilityStatus MapContainerStatus(string? state)
    {
        return state?.ToLower() switch
        {
            "running" => ReachabilityStatus.Reachable,
            "exited" => ReachabilityStatus.Unreachable,
            "created" => ReachabilityStatus.Unverified,
            "paused" => ReachabilityStatus.Unverified,
            "restarting" => ReachabilityStatus.Unverified,
            _ => ReachabilityStatus.Unverified
        };
    }
}

// GraphQL response models
public class UnraidGraphQLResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public UnraidData? Data { get; set; }
}

public class UnraidData
{
    [System.Text.Json.Serialization.JsonPropertyName("docker")]
    public UnraidDocker? Docker { get; set; }
}

public class UnraidDocker
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public string? Id { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("containers")]
    public List<UnraidContainer> Containers { get; set; } = new();
}

public class UnraidContainer
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
    
    [System.Text.Json.Serialization.JsonPropertyName("names")]
    public List<string> Names { get; set; } = new();
    
    [System.Text.Json.Serialization.JsonPropertyName("image")]
    public string? Image { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("imageId")]
    public string? ImageId { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("ports")]
    public List<UnraidPort>? Ports { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("labels")]
    public Dictionary<string, object>? Labels { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("state")]
    public string? State { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public string? Status { get; set; }
}

public class UnraidPort
{
    [System.Text.Json.Serialization.JsonPropertyName("ip")]
    public string? Ip { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("privatePort")]
    public int PrivatePort { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("publicPort")]
    public int? PublicPort { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("type")]
    public string? Type { get; set; }
}