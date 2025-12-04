using HomelabMapper.Core.Interfaces;
using HomelabMapper.Core.Models;
using HomelabMapper.Integration;

namespace HomelabMapper.Detectors;

public class RouterF670YScanner : IHostScanner
{
    public string ScannerName => "RouterF670Y";
    public int Priority => 5;
    public List<string> DependsOn => new();
    public List<string> OptionalDependsOn => new();

    public ScannerActivationCriteria GetActivationCriteria()
    {
        return new ScannerActivationCriteria
        {
            // Activate only when the host IP equals 192.168.1.1 (router)
            CustomPredicate = host => host != null && host.Ip == "192.168.1.1"
        };
    }

    public async Task<ScanResult> ScanAsync(Entity host, ScannerContext context)
    {
        try
        {
            // Mark the host as a router/gateway
            host.Type = EntityType.Router;
            host.Name = "Router Gateway (F670Y)";
            context.Logger.Info($"RouterF670Y scanner activated for {host.Ip}");

            // Get router credentials from credential store
            var username = context.Credentials.GetCredential("router", "username") ?? "admin";
            var password = context.Credentials.GetCredential("router", "password") ?? "";

            if (string.IsNullOrEmpty(password))
            {
                context.Logger.Warn("Router password not configured. Set ROUTER_PASSWORD environment variable or configure router.password_env in config.yaml");
                return ScanResult.Failed(host, "Router credentials not configured", "Password not provided");
            }

            context.Logger.Info("Connecting to router web UI to extract DHCP leases...");

            using var routerClient = new RouterF670YClient(host.Ip, username, password);
            
            // Initialize browser
            await routerClient.InitializeAsync();
            context.Logger.Info("Browser initialized, attempting login...");

            // Login
            var loginSuccess = await routerClient.LoginAsync();
            if (!loginSuccess)
            {
                return ScanResult.Failed(host, "Router login failed", "Could not authenticate to router web UI");
            }

            context.Logger.Info("Login successful, extracting DHCP leases...");

            // Get DHCP leases
            var leases = await routerClient.GetDhcpLeasesAsync();
            context.Logger.Info($"Retrieved {leases.Count} DHCP leases from router");

            // Store leases in metadata for correlation
            host.Metadata["dhcp_lease_count"] = leases.Count.ToString();
            host.Metadata["dhcp_leases"] = leases;

            // Create a mapping of IP -> MAC for easy lookup during correlation
            var ipToMacMapping = new Dictionary<string, string>();
            foreach (var lease in leases)
            {
                if (!string.IsNullOrEmpty(lease.IpAddress) && !string.IsNullOrEmpty(lease.MacAddress))
                {
                    ipToMacMapping[lease.IpAddress] = lease.MacAddress;
                    context.Logger.Debug($"  {lease.IpAddress} -> {lease.MacAddress} ({lease.Hostname ?? "no hostname"})");
                }
            }

            host.Metadata["ip_to_mac"] = ipToMacMapping;

            // Enrich discovered entities with MAC addresses and hostnames
            var enrichedCount = 0;
            foreach (var entity in context.AllEntities)
            {
                if (!string.IsNullOrEmpty(entity.Ip) && ipToMacMapping.TryGetValue(entity.Ip, out var macAddress))
                {
                    // Add MAC address
                    entity.Metadata["mac_address"] = macAddress;
                    
                    // Find the lease to get hostname
                    var lease = leases.FirstOrDefault(l => l.IpAddress == entity.Ip);
                    if (lease != null)
                    {
                        if (!string.IsNullOrEmpty(lease.Hostname))
                        {
                            // Update entity name if it's generic or not set
                            if (string.IsNullOrEmpty(entity.Name) || entity.Name.StartsWith("Host-") || entity.Name == entity.Ip)
                            {
                                entity.Name = lease.Hostname;
                            }
                            entity.Metadata["router_hostname"] = lease.Hostname;
                        }
                        
                        enrichedCount++;
                        context.Logger.Debug($"Enriched {entity.Ip}: MAC={macAddress}, Hostname={lease.Hostname ?? "N/A"}");
                    }
                }
            }

            context.Logger.Info($"Enriched {enrichedCount} entities with MAC addresses and hostnames");
            
            // Create access point entities
            var accessPoints = new List<Entity>();
            var apLeases = leases.Where(l => l.IsAccessPoint).ToList();
            
            foreach (var apLease in apLeases)
            {
                if (!string.IsNullOrEmpty(apLease.IpAddress))
                {
                    // Skip the router itself (it shouldn't be its own child)
                    if (apLease.IpAddress == host.Ip)
                    {
                        context.Logger.Debug($"Skipping router itself ({apLease.IpAddress}) from access point list");
                        continue;
                    }
                    
                    // Check if entity already exists for this IP
                    var existingEntity = context.AllEntities.FirstOrDefault(e => e.Ip == apLease.IpAddress);
                    
                    if (existingEntity != null)
                    {
                        // Enrich existing entity
                        existingEntity.Type = EntityType.AccessPoint;
                        if (!string.IsNullOrEmpty(apLease.Hostname))
                        {
                            existingEntity.Name = apLease.Hostname;
                        }
                        if (!string.IsNullOrEmpty(apLease.Role))
                        {
                            existingEntity.Metadata["ap_role"] = apLease.Role;
                        }
                        if (!string.IsNullOrEmpty(apLease.Backhaul))
                        {
                            existingEntity.Metadata["ap_backhaul"] = apLease.Backhaul;
                        }
                        existingEntity.Metadata["mac_address"] = apLease.MacAddress ?? "";
                        existingEntity.ParentId = host.Id;
                        context.Logger.Info($"Enriched existing entity as AccessPoint: {apLease.IpAddress}");
                    }
                    else
                    {
                        // Create new access point entity
                        var apEntity = new Entity
                        {
                            Id = Guid.NewGuid().ToString(),
                            Type = EntityType.AccessPoint,
                            Name = apLease.Hostname ?? $"AccessPoint-{apLease.IpAddress}",
                            Ip = apLease.IpAddress,
                            Status = ReachabilityStatus.Reachable,
                            ParentId = host.Id
                        };
                        
                        apEntity.Metadata["mac_address"] = apLease.MacAddress ?? "";
                        if (!string.IsNullOrEmpty(apLease.Role))
                        {
                            apEntity.Metadata["ap_role"] = apLease.Role;
                        }
                        if (!string.IsNullOrEmpty(apLease.Backhaul))
                        {
                            apEntity.Metadata["ap_backhaul"] = apLease.Backhaul;
                        }
                        
                        accessPoints.Add(apEntity);
                        context.Logger.Info($"Created new AccessPoint entity: {apEntity.Name} ({apEntity.Ip})");
                    }
                }
            }
            
            context.Logger.Info($"Enriched/created {apLeases.Count} access point entities ({accessPoints.Count} new)");
            context.Logger.Info("Router scan completed successfully");
            
            // Return empty list since we're enriching existing entities
            // The host (router) entity already exists and access points are enriched in-place
            return ScanResult.Successful(accessPoints);
        }
        catch (Exception ex)
        {
            context.Logger.Error($"Router scan failed: {ex.Message}", ex);
            return ScanResult.Failed(host, "Router scan failed", ex.Message);
        }
    }

    public IEnumerable<Type> GetChildScannerTypes(ScanResult result)
    {
        return Array.Empty<Type>();
    }
}
