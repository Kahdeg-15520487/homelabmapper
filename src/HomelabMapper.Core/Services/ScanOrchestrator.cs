using HomelabMapper.Core.Interfaces;
using HomelabMapper.Core.Models;

namespace HomelabMapper.Core.Services;

public class ScanOrchestrator
{
    private readonly ScannerRegistry _registry;
    private readonly ILogger _logger;
    private readonly List<Conflict> _conflicts = new();

    public ScanOrchestrator(ScannerRegistry registry, ILogger logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public async Task<TopologyReport> ExecuteScanAsync(ScannerContext context, List<Entity> discoveredHosts)
    {
        var scanId = $"scan-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        _logger.Info($"Starting scan: {scanId}");

        // Phase 1: Add all discovered hosts to the entity list
        context.AllEntities.AddRange(discoveredHosts);

        // Phase 2: Recursive scanner activation with dependency management
        var queue = new Queue<Entity>(discoveredHosts);
        var scanned = new HashSet<string>();

        while (queue.Count > 0)
        {
            var entity = queue.Dequeue();
            if (scanned.Contains(entity.Id))
            {
                continue;
            }

            _logger.Debug($"Processing entity: {entity.Ip} ({entity.Type})");

            // Skip scanning entities with IPs outside the discovered subnets
            // (e.g., internal Docker container IPs like 172.17.0.x)
            if (!string.IsNullOrEmpty(entity.Ip) && !context.DiscoveredIPs.Contains(entity.Ip))
            {
                _logger.Debug($"Skipping scan for {entity.Ip} - not in target subnets");
                scanned.Add(entity.Id);
                continue;
            }

            var scanners = await _registry.FindApplicableScannersAsync(entity, context);
            
            // Sort by dependencies
            scanners = ResolveDependencies(scanners);

            foreach (var scanner in scanners)
            {
                _logger.Info($"Activating {scanner.ScannerName} for {entity.Ip}");

                try
                {
                    var result = await scanner.ScanAsync(entity, context);

                    if (!result.Success)
                    {
                        entity.Status = ReachabilityStatus.Unverified;
                        entity.Metadata["scan_error"] = result.ErrorMessage ?? "Unknown error";
                        entity.Metadata["scan_error_reason"] = result.ErrorDetails ?? "";
                        _logger.Warn($"Scanner {scanner.ScannerName} failed for {entity.Ip}: {result.ErrorMessage}");
                        continue;
                    }

                    // Add discovered children
                    foreach (var child in result.DiscoveredEntities)
                    {
                        child.ParentId ??= entity.Id;
                        context.AllEntities.Add(child);
                        queue.Enqueue(child);
                    }

                    _logger.Info($"Scanner {scanner.ScannerName} found {result.DiscoveredEntities.Count} entities");
                }
                catch (Exception ex)
                {
                    _logger.Error($"Exception in scanner {scanner.ScannerName} for {entity.Ip}", ex);
                    entity.Status = ReachabilityStatus.Unverified;
                    entity.Metadata["scan_exception"] = ex.Message;
                }
            }

            scanned.Add(entity.Id);
        }

        // Phase 3: Conflict detection
        _logger.Info("Detecting conflicts...");
        DetectConflicts(context);

        // Phase 4: Generate report
        // Deduplicate entities by ID (in case same entity was added multiple times)
        var uniqueEntities = context.AllEntities
            .GroupBy(e => e.Id)
            .Select(g => g.First())
            .ToList();
        
        var report = new TopologyReport
        {
            Timestamp = DateTime.UtcNow,
            ScanId = scanId,
            Entities = uniqueEntities,
            Conflicts = _conflicts,
            Summary = GenerateSummary(context)
        };

        if (uniqueEntities.Count < context.AllEntities.Count)
        {
            _logger.Warn($"Removed {context.AllEntities.Count - uniqueEntities.Count} duplicate entities");
        }

        _logger.Info($"Scan completed: {uniqueEntities.Count} entities, {_conflicts.Count} conflicts");

        return report;
    }

    private List<IHostScanner> ResolveDependencies(List<IHostScanner> scanners)
    {
        var resolved = new List<IHostScanner>();
        var remaining = new List<IHostScanner>(scanners);

        while (remaining.Any())
        {
            var canResolve = remaining.Where(scanner =>
                scanner.DependsOn.All(dep => resolved.Any(r => r.ScannerName == dep))
            ).ToList();

            if (!canResolve.Any())
            {
                // Check if remaining scanners only have optional dependencies missing
                var onlyOptionalMissing = remaining.All(scanner =>
                    scanner.DependsOn.All(dep => resolved.Any(r => r.ScannerName == dep))
                );

                if (!onlyOptionalMissing)
                {
                    // Circular dependency or missing required dependency
                    _logger.Warn("Unable to fully resolve scanner dependencies");
                }
                
                // Add remaining scanners - they either have circular deps or only optional deps missing
                resolved.AddRange(remaining);
                break;
            }

            resolved.AddRange(canResolve);
            remaining = remaining.Except(canResolve).ToList();
        }

        return resolved;
    }

    private bool IsNetworkEndpoint(EntityType type)
    {
        // Return true for entity types that represent actual network services/endpoints
        // Return false for logical groupings that don't represent network endpoints
        return type switch
        {
            EntityType.PortainerStack => false,  // Logical grouping, not a network endpoint
            EntityType.ProxmoxCluster => false,  // Logical grouping
            _ => true  // All others (services, containers, VMs, etc.) are network endpoints
        };
    }

    private void DetectConflicts(ScannerContext context)
    {
        // Conflict 1: Multiple entities with same IP and different types
        // Group by IP for entities that represent network endpoints
        var ipGroups = context.AllEntities
            .Where(e => !string.IsNullOrEmpty(e.Ip))
            .Where(e => IsNetworkEndpoint(e.Type)) // Only check actual network services
            .GroupBy(e => e.Ip);

        foreach (var group in ipGroups.Where(g => g.Count() > 1))
        {
            var entities = group.ToList();
            var types = entities.Select(e => e.Type).Distinct().ToList();

            if (types.Count > 1)
            {
                // Check if this is a resolvable conflict (Unknown + Identified entity)
                if (TryResolveUnknownConflict(entities, context))
                {
                    // Conflict was resolved by merging
                    continue;
                }

                _conflicts.Add(new Conflict
                {
                    Type = ConflictType.TypeMismatch,
                    Ip = group.Key,
                    InvolvedEntities = entities,
                    Description = $"Multiple entity types at {group.Key}: {string.Join(", ", types)}"
                });
            }
        }

        // Conflict 2: Unverified entities
        var unverified = context.AllEntities.Where(e => e.Status == ReachabilityStatus.Unverified).ToList();
        foreach (var entity in unverified)
        {
            _conflicts.Add(new Conflict
            {
                Type = ConflictType.UnverifiedEntity,
                Ip = entity.Ip,
                InvolvedEntities = new List<Entity> { entity },
                Description = $"Entity at {entity.Ip} ({entity.Type}: {entity.Name}) could not be verified"
            });
        }

        // Conflict 3: IP mismatch between API and scan
        foreach (var entity in context.AllEntities)
        {
            if (entity.Metadata.TryGetValue("api_reported_ip", out var apiIpObj) && apiIpObj is string apiIp)
            {
                if (!string.IsNullOrEmpty(apiIp) && apiIp != entity.Ip)
                {
                    _conflicts.Add(new Conflict
                    {
                        Type = ConflictType.IpMismatch,
                        Ip = entity.Ip,
                        InvolvedEntities = new List<Entity> { entity },
                        Description = $"API reported IP {apiIp} but scan found {entity.Ip}"
                    });
                }
            }
        }
    }

    private bool TryResolveUnknownConflict(List<Entity> conflictingEntities, ScannerContext context)
    {
        // Check if we have exactly one Unknown entity and one or more identified entities
        var unknownEntities = conflictingEntities.Where(e => e.Type == EntityType.Unknown).ToList();
        var identifiedEntities = conflictingEntities.Where(e => e.Type != EntityType.Unknown).ToList();

        if (unknownEntities.Count == 1 && identifiedEntities.Count >= 1)
        {
            var unknownEntity = unknownEntities[0];
            var primaryEntity = identifiedEntities[0]; // Use the first identified entity as primary

            _logger.Debug($"Resolving conflict: merging Unknown entity at {unknownEntity.Ip} with {primaryEntity.Type}: {primaryEntity.Name}");

            // Merge ports from Unknown entity into the identified entity
            var unknownPorts = unknownEntity.OpenPorts ?? new List<int>();
            var existingPorts = primaryEntity.OpenPorts ?? new List<int>();
            primaryEntity.OpenPorts = existingPorts.Union(unknownPorts).OrderBy(p => p).ToList();

            // Merge metadata if useful
            foreach (var kvp in unknownEntity.Metadata)
            {
                if (!primaryEntity.Metadata.ContainsKey(kvp.Key))
                {
                    primaryEntity.Metadata[kvp.Key] = kvp.Value;
                }
            }

            // Remove the Unknown entity from the list
            context.AllEntities.Remove(unknownEntity);

            _logger.Info($"Merged Unknown entity into {primaryEntity.Type}: {primaryEntity.Name} at {primaryEntity.Ip} (added {unknownPorts.Count} ports)");
            
            return true; // Conflict resolved
        }

        return false; // Could not resolve conflict
    }

    private ScanSummary GenerateSummary(ScannerContext context)
    {
        var summary = new ScanSummary
        {
            TotalEntities = context.AllEntities.Count
        };

        // Count by type
        foreach (var typeGroup in context.AllEntities.GroupBy(e => e.Type))
        {
            summary.EntitiesByType[typeGroup.Key] = typeGroup.Count();
        }

        // Count by status
        foreach (var statusGroup in context.AllEntities.GroupBy(e => e.Status))
        {
            summary.EntitiesByStatus[statusGroup.Key] = statusGroup.Count();
        }

        return summary;
    }
}
