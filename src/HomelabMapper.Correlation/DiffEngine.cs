using HomelabMapper.Core.Models;

namespace HomelabMapper.Correlation;

public enum ChangeType
{
    Added,
    Removed,
    ModifiedIp,
    ModifiedStatus,
    ModifiedParent,
    ModifiedPorts
}

public class EntityChange
{
    public ChangeType Type { get; set; }
    public Entity? OldEntity { get; set; }
    public Entity? NewEntity { get; set; }
    public List<string> ChangeDetails { get; set; } = new();
}

public class DiffReport
{
    public TopologyReport BaselineReport { get; set; } = null!;
    public TopologyReport CurrentReport { get; set; } = null!;
    public List<EntityChange> Changes { get; set; } = new();
    public int AddedCount => Changes.Count(c => c.Type == ChangeType.Added);
    public int RemovedCount => Changes.Count(c => c.Type == ChangeType.Removed);
    public int ModifiedCount => Changes.Count(c => c.Type != ChangeType.Added && c.Type != ChangeType.Removed);
}

public class DiffEngine
{
    public static DiffReport Compare(TopologyReport baseline, TopologyReport current)
    {
        var report = new DiffReport
        {
            BaselineReport = baseline,
            CurrentReport = current
        };

        var baselineMap = baseline.Entities.ToDictionary(GetEntityFingerprint);
        var currentMap = current.Entities.ToDictionary(GetEntityFingerprint);

        // Detect additions
        foreach (var fingerprint in currentMap.Keys.Except(baselineMap.Keys))
        {
            report.Changes.Add(new EntityChange
            {
                Type = ChangeType.Added,
                NewEntity = currentMap[fingerprint]
            });
        }

        // Detect removals
        foreach (var fingerprint in baselineMap.Keys.Except(currentMap.Keys))
        {
            report.Changes.Add(new EntityChange
            {
                Type = ChangeType.Removed,
                OldEntity = baselineMap[fingerprint]
            });
        }

        // Detect modifications
        foreach (var fingerprint in baselineMap.Keys.Intersect(currentMap.Keys))
        {
            var oldEntity = baselineMap[fingerprint];
            var newEntity = currentMap[fingerprint];

            var changes = GetChangeDetails(oldEntity, newEntity);
            if (changes.Any())
            {
                // Determine primary change type
                var changeType = DeterminePrimaryChangeType(oldEntity, newEntity);
                
                report.Changes.Add(new EntityChange
                {
                    Type = changeType,
                    OldEntity = oldEntity,
                    NewEntity = newEntity,
                    ChangeDetails = changes
                });
            }
        }

        return report;
    }

    public static async Task WriteMarkdownAsync(DiffReport diff, string filePath)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("# Network Change Report");
        sb.AppendLine();
        sb.AppendLine($"**Baseline Scan:** {diff.BaselineReport.ScanId} ({diff.BaselineReport.Timestamp:yyyy-MM-dd HH:mm:ss})");
        sb.AppendLine($"**Current Scan:** {diff.CurrentReport.ScanId} ({diff.CurrentReport.Timestamp:yyyy-MM-dd HH:mm:ss})");
        sb.AppendLine();

        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine($"- 🟢 **Added:** {diff.AddedCount}");
        sb.AppendLine($"- 🔴 **Removed:** {diff.RemovedCount}");
        sb.AppendLine($"- 🟡 **Modified:** {diff.ModifiedCount}");
        sb.AppendLine($"- **Total Changes:** {diff.Changes.Count}");
        sb.AppendLine();

        // Added entities
        var added = diff.Changes.Where(c => c.Type == ChangeType.Added).ToList();
        if (added.Any())
        {
            sb.AppendLine("## 🟢 Added Entities");
            sb.AppendLine();
            foreach (var change in added.OrderBy(c => c.NewEntity!.Type).ThenBy(c => c.NewEntity!.Name))
            {
                var entity = change.NewEntity!;
                sb.AppendLine($"- **{entity.Type}:** {entity.Name} ({entity.Ip})");
            }
            sb.AppendLine();
        }

        // Removed entities
        var removed = diff.Changes.Where(c => c.Type == ChangeType.Removed).ToList();
        if (removed.Any())
        {
            sb.AppendLine("## 🔴 Removed Entities");
            sb.AppendLine();
            foreach (var change in removed.OrderBy(c => c.OldEntity!.Type).ThenBy(c => c.OldEntity!.Name))
            {
                var entity = change.OldEntity!;
                sb.AppendLine($"- **{entity.Type}:** {entity.Name} ({entity.Ip})");
            }
            sb.AppendLine();
        }

        // Modified entities
        var modified = diff.Changes.Where(c => c.Type != ChangeType.Added && c.Type != ChangeType.Removed).ToList();
        if (modified.Any())
        {
            sb.AppendLine("## 🟡 Modified Entities");
            sb.AppendLine();
            foreach (var change in modified.OrderBy(c => c.NewEntity!.Type).ThenBy(c => c.NewEntity!.Name))
            {
                var entity = change.NewEntity!;
                sb.AppendLine($"### {entity.Type}: {entity.Name}");
                sb.AppendLine();
                foreach (var detail in change.ChangeDetails)
                {
                    sb.AppendLine($"- {detail}");
                }
                sb.AppendLine();
            }
        }

        await File.WriteAllTextAsync(filePath, sb.ToString());
    }

    private static string GetEntityFingerprint(Entity entity)
    {
        // Priority 1: Use stable API IDs
        if (entity.Metadata.ContainsKey("docker_id"))
        {
            return $"docker:{entity.Metadata["docker_id"]}";
        }
        if (entity.Metadata.ContainsKey("proxmox_vmid"))
        {
            return $"proxmox:{entity.Metadata["proxmox_vmid"]}";
        }
        if (entity.Metadata.ContainsKey("portainer_stack_id"))
        {
            return $"portainer-stack:{entity.Metadata["portainer_stack_id"]}";
        }

        // Priority 2: Name-based
        if (!string.IsNullOrEmpty(entity.Name))
        {
            return $"{entity.Type}:{entity.Name}";
        }

        // Priority 3: IP-based (least reliable)
        return $"ip:{entity.Ip}";
    }

    private static List<string> GetChangeDetails(Entity oldEntity, Entity newEntity)
    {
        var changes = new List<string>();

        if (oldEntity.Ip != newEntity.Ip)
        {
            changes.Add($"IP changed: {oldEntity.Ip} → {newEntity.Ip}");
        }

        if (oldEntity.Status != newEntity.Status)
        {
            changes.Add($"Status changed: {oldEntity.Status} → {newEntity.Status}");
        }

        if (oldEntity.ParentId != newEntity.ParentId)
        {
            changes.Add($"Parent changed: {oldEntity.ParentId ?? "none"} → {newEntity.ParentId ?? "none"}");
        }

        if (oldEntity.Name != newEntity.Name)
        {
            changes.Add($"Name changed: {oldEntity.Name} → {newEntity.Name}");
        }

        // Check exposed ports
        var oldPorts = GetExposedPorts(oldEntity);
        var newPorts = GetExposedPorts(newEntity);
        if (!oldPorts.SequenceEqual(newPorts))
        {
            changes.Add($"Ports changed: [{string.Join(", ", oldPorts)}] → [{string.Join(", ", newPorts)}]");
        }

        return changes;
    }

    private static ChangeType DeterminePrimaryChangeType(Entity oldEntity, Entity newEntity)
    {
        if (oldEntity.Ip != newEntity.Ip)
        {
            return ChangeType.ModifiedIp;
        }
        if (oldEntity.Status != newEntity.Status)
        {
            return ChangeType.ModifiedStatus;
        }
        if (oldEntity.ParentId != newEntity.ParentId)
        {
            return ChangeType.ModifiedParent;
        }

        var oldPorts = GetExposedPorts(oldEntity);
        var newPorts = GetExposedPorts(newEntity);
        if (!oldPorts.SequenceEqual(newPorts))
        {
            return ChangeType.ModifiedPorts;
        }

        return ChangeType.ModifiedStatus; // Default
    }

    private static List<string> GetExposedPorts(Entity entity)
    {
        if (entity.Metadata.ContainsKey("exposed_ports") && 
            entity.Metadata["exposed_ports"] is List<string> ports)
        {
            return ports;
        }
        return new List<string>();
    }
}
