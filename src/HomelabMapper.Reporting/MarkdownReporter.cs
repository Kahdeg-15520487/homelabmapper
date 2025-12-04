using HomelabMapper.Core.Models;
using System.Text;

namespace HomelabMapper.Reporting;

public class MarkdownReporter
{
    public static async Task WriteAsync(TopologyReport report, string filePath)
    {
        var markdown = GenerateMarkdown(report);
        await File.WriteAllTextAsync(filePath, markdown);
    }

    private static string GenerateMarkdown(TopologyReport report)
    {
        var sb = new StringBuilder();

        // Header
        sb.AppendLine("# Homelab Network Scan Report");
        sb.AppendLine();
        sb.AppendLine($"**Scan ID:** {report.ScanId}");
        sb.AppendLine($"**Scan Date:** {report.Timestamp:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"**Subnets:** {string.Join(", ", report.Subnets)}");
        sb.AppendLine();

        // Summary
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine($"- **Total Entities:** {report.Summary.TotalEntities}");
        sb.AppendLine();

        sb.AppendLine("### Entities by Type");
        foreach (var kvp in report.Summary.EntitiesByType.OrderByDescending(x => x.Value))
        {
            sb.AppendLine($"- **{kvp.Key}:** {kvp.Value}");
        }
        sb.AppendLine();

        sb.AppendLine("### Entities by Status");
        foreach (var kvp in report.Summary.EntitiesByStatus.OrderByDescending(x => x.Value))
        {
            sb.AppendLine($"- **{kvp.Key}:** {kvp.Value}");
        }
        sb.AppendLine();

        // Topology
        sb.AppendLine("## Topology");
        sb.AppendLine();

        var rootEntities = report.Entities.Where(e => string.IsNullOrEmpty(e.ParentId)).ToList();
        
        // Sort by IP address (handling empty IPs and proper numeric sorting)
        var sortedRoots = rootEntities.OrderBy(e => 
        {
            if (string.IsNullOrEmpty(e.Ip)) return (new byte[] { 255, 255, 255, 255 }, e.Name ?? "");
            
            var parts = e.Ip.Split('.');
            if (parts.Length == 4 && 
                byte.TryParse(parts[0], out var b1) &&
                byte.TryParse(parts[1], out var b2) &&
                byte.TryParse(parts[2], out var b3) &&
                byte.TryParse(parts[3], out var b4))
            {
                return (new byte[] { b1, b2, b3, b4 }, e.Name ?? "");
            }
            
            return (new byte[] { 255, 255, 255, 255 }, e.Ip);
        }, Comparer<(byte[], string)>.Create((a, b) =>
        {
            for (int i = 0; i < 4; i++)
            {
                var cmp = a.Item1[i].CompareTo(b.Item1[i]);
                if (cmp != 0) return cmp;
            }
            return string.Compare(a.Item2, b.Item2, StringComparison.Ordinal);
        }));
        
        foreach (var root in sortedRoots)
        {
            AppendEntityTree(sb, root, report.Entities, 0);
        }

        // Conflicts
        if (report.Conflicts.Any())
        {
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine($"## ⚠️ CONFLICTS ({report.Conflicts.Count})");
            sb.AppendLine();

            for (int i = 0; i < report.Conflicts.Count; i++)
            {
                var conflict = report.Conflicts[i];
                sb.AppendLine($"### Conflict #{i + 1}: {conflict.Type}");
                sb.AppendLine();
                sb.AppendLine($"**IP:** {conflict.Ip}");
                sb.AppendLine($"**Description:** {conflict.Description}");
                sb.AppendLine();

                if (conflict.InvolvedEntities.Any())
                {
                    sb.AppendLine("**Involved Entities:**");
                    foreach (var entity in conflict.InvolvedEntities)
                    {
                        sb.AppendLine($"- {entity.Type}: {entity.Name} ({entity.Ip}) - Status: {entity.Status}");
                        
                        if (entity.Metadata.ContainsKey("scan_error"))
                        {
                            sb.AppendLine($"  - Error: {entity.Metadata["scan_error"]}");
                        }
                    }
                    sb.AppendLine();
                }
            }
        }

        // Certificate Information
        var entitiesWithCerts = report.Entities.Where(e => e.Certificate != null).ToList();
        if (entitiesWithCerts.Any())
        {
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("## 🔒 SSL Certificates");
            sb.AppendLine();

            foreach (var entity in entitiesWithCerts)
            {
                var cert = entity.Certificate!;
                sb.AppendLine($"### {entity.Name} ({entity.Ip})");
                sb.AppendLine();
                sb.AppendLine($"- **Self-Signed:** {(cert.IsSelfSigned ? "Yes ⚠️" : "No")}");
                sb.AppendLine($"- **Issuer:** {cert.Issuer}");
                sb.AppendLine($"- **Expiry:** {cert.Expiry:yyyy-MM-dd}");
                sb.AppendLine($"- **Fingerprint:** {cert.Fingerprint}");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static void AppendEntityTree(StringBuilder sb, Entity entity, List<Entity> allEntities, int depth)
    {
        var indent = new string(' ', depth * 2);
        var icon = GetEntityIcon(entity.Type);
        var statusIcon = GetStatusIcon(entity.Status);
        
        var name = string.IsNullOrEmpty(entity.Name) ? entity.Type.ToString() : entity.Name;
        var ipPart = !string.IsNullOrEmpty(entity.Ip) ? $" ({entity.Ip})" : "";
        
        // Add ports to display if available
        var portsPart = "";
        if (entity.OpenPorts.Any())
        {
            portsPart = $" - Ports: {string.Join(", ", entity.OpenPorts.OrderBy(p => p))}";
        }
        
        sb.AppendLine($"{indent}{icon} **{name}**{ipPart}{portsPart} {statusIcon}");

        // Add metadata details
        if (entity.Metadata.ContainsKey("docker_image"))
        {
            sb.AppendLine($"{indent}  - Image: `{entity.Metadata["docker_image"]}`");
        }
        if (entity.Metadata.ContainsKey("container_image"))
        {
            sb.AppendLine($"{indent}  - Image: `{entity.Metadata["container_image"]}`");
        }

        // Recursively append children
        var children = allEntities.Where(e => e.ParentId == entity.Id).OrderBy(e => e.Name).ToList();
        foreach (var child in children)
        {
            AppendEntityTree(sb, child, allEntities, depth + 1);
        }
    }

    private static string GetEntityIcon(EntityType type)
    {
        return type switch
        {
            EntityType.Proxmox => "🖧",
            EntityType.ProxmoxCluster => "🗄️",
            EntityType.ProxmoxNode => "🖥️",
            EntityType.PC => "💻",
            EntityType.Vm => "🖳",
            EntityType.Lxc => "📦",
            EntityType.DockerHost => "🐳",
            EntityType.Container => "📦",
            EntityType.PortainerService => "🎛️",
            EntityType.PortainerStack => "📚",
            EntityType.Unraid => "🧱",
            EntityType.Nas => "💾",
            EntityType.Service => "⚙️",
            EntityType.Router => "🌐",
            EntityType.AccessPoint => "📡",
            _ => "❓"
        };
    }

    private static string GetStatusIcon(ReachabilityStatus status)
    {
        return status switch
        {
            ReachabilityStatus.Reachable => "✅",
            ReachabilityStatus.Unreachable => "🔒",
            ReachabilityStatus.Unverified => "❓",
            ReachabilityStatus.Conflicting => "⚠️",
            ReachabilityStatus.Stale => "⏰",
            _ => ""
        };
    }
}
