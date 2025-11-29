using System.Text;
using System.Text.Json;
using HomelabMapper.Core.Interfaces;
using HomelabMapper.Core.Models;

namespace HomelabMapper.Detectors;

public class UnraidScanner : IHostScanner
{
    public string ScannerName => "Unraid";
    public int Priority => 35; // After Portainer (30) so containers exist first
    public List<string> DependsOn => new();
    public List<string> OptionalDependsOn => new();

    public ScannerActivationCriteria GetActivationCriteria()
    {
        return new ScannerActivationCriteria
        {
            RequiredOpenPorts = new List<int> { 80, 443 },
            RequiredHttpHeaders = new Dictionary<string, string>
            {
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

            // If host is PortainerService or another identified type, create a NEW Unraid entity
            // Otherwise just mark the existing host as Unraid
            Entity unraidEntity;
            if (host.Type != EntityType.Unknown && host.Type != EntityType.Unraid)
            {
                // Create new Unraid entity
                unraidEntity = new Entity
                {
                    Id = $"unraid-{host.Ip}",
                    Ip = host.Ip,
                    Type = EntityType.Unraid,
                    Name = host.Name ?? "Unraid Server",
                    Status = ReachabilityStatus.Reachable,
                    OpenPorts = host.OpenPorts,
                    ParentId = string.Empty, // Empty string = root entity (prevents orchestrator from setting parent)
                    Metadata = new Dictionary<string, object>
                    {
                        ["unraid_detected"] = "true",
                        ["unraid_container_count"] = containers.Count.ToString()
                    }
                };
                
                // Make the original host (e.g., Portainer) a child of Unraid
                host.ParentId = unraidEntity.Id;
                
                context.Logger.Debug($"Created new Unraid entity and reparented {host.Type} to it");
            }
            else
            {
                // Just mark the existing entity as Unraid
                host.Type = EntityType.Unraid;
                host.Metadata["unraid_detected"] = "true";
                host.Metadata["unraid_container_count"] = containers.Count.ToString();
                unraidEntity = host;
            }

            // Find existing containers discovered by Portainer
            var existingContainers = context.AllEntities
                .Where(e => e.Type == EntityType.Container)
                .ToList();
            
            context.Logger.Debug($"Found {existingContainers.Count} existing containers to match against");
            var containersWithId = existingContainers.Count(c => c.Metadata.ContainsKey("container_id"));
            context.Logger.Debug($"{containersWithId} of them have container_id metadata");

            foreach (var container in containers)
            {
                var containerName = GetContainerName(container.Names);
                
                // Unraid returns IDs in format: "dockerid:containerid"
                // Extract just the container part after the colon
                var unraidContainerId = container.Id.Contains(':') 
                    ? container.Id.Split(':')[1] 
                    : container.Id;
                
                context.Logger.Debug($"Unraid container {containerName}: ID={unraidContainerId.Substring(0, Math.Min(12, unraidContainerId.Length))}...");
                
                // Try to match with existing container by Docker ID (stored by Portainer)
                // Match against both full ID and short ID (first 12 chars)
                var existingContainer = existingContainers.FirstOrDefault(ec =>
                {
                    if (!ec.Metadata.ContainsKey("container_id")) return false;
                    
                    var portainerContainerId = ec.Metadata["container_id"] as string ?? "";
                    
                    // Try exact match first
                    if (portainerContainerId == unraidContainerId) return true;
                    
                    // Try matching with short ID (first 12 chars)
                    if (portainerContainerId.Length >= 12 && unraidContainerId.StartsWith(portainerContainerId.Substring(0, 12)))
                        return true;
                    if (unraidContainerId.Length >= 12 && portainerContainerId.StartsWith(unraidContainerId.Substring(0, 12)))
                        return true;
                    
                    return false;
                });
                
                if (existingContainer != null)
                {
                    var portainerId = existingContainer.Metadata["container_id"] as string ?? "";
                    context.Logger.Debug($"Found match! Portainer ID: {portainerId.Substring(0, Math.Min(12, portainerId.Length))}...");
                    
                    // Enrich existing container with Unraid metadata and update IP to Unraid IP
                    existingContainer.Ip = unraidEntity.Ip;
                    existingContainer.Metadata["unraid_managed"] = true;
                    existingContainer.Metadata["unraid_image"] = container.Image ?? string.Empty;
                    existingContainer.Metadata["unraid_state"] = container.State ?? string.Empty;
                    existingContainer.Status = MapContainerStatus(container.State);
                    
                    // Update open ports from Unraid data
                    if (container.Ports?.Any() == true)
                    {
                        var publicPorts = container.Ports
                            .Where(p => p.PublicPort.HasValue && p.PublicPort.Value > 0)
                            .Select(p => p.PublicPort!.Value)
                            .Distinct()
                            .ToList();
                        
                        if (publicPorts.Any())
                        {
                            existingContainer.OpenPorts = publicPorts;
                        }
                    }
                    
                    context.Logger.Debug($"Matched and enriched container {containerName} with Unraid data (IP: {unraidEntity.Ip})");
                }
                else
                {
                    context.Logger.Debug($"Found Unraid container without existing match: {containerName}");
                }
            }

            // Return new Unraid entity if we created one, otherwise return empty list
            // The ReparentContainersToUnraid correlation will handle the parent-child relationship
            if (host.Type != EntityType.Unraid)
            {
                // We created a new Unraid entity, add it to the context
                return ScanResult.Successful(new List<Entity> { unraidEntity });
            }
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
                        containers {
                            id
                            names
                            image
                            ports {
                                ip
                                privatePort
                                publicPort
                                type
                            }
                            state
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
    
    [System.Text.Json.Serialization.JsonPropertyName("ports")]
    public List<UnraidPort>? Ports { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("state")]
    public string? State { get; set; }
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