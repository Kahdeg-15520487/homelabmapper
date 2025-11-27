using HomelabMapper.Core.Models;
using System.Text;

namespace HomelabMapper.Reporting;

public class MermaidGenerator
{
    public static async Task WriteAsync(TopologyReport report, string filePath)
    {
        var mermaid = GenerateMermaid(report);
        await File.WriteAllTextAsync(filePath, mermaid);
    }

    private static string GenerateMermaid(TopologyReport report)
    {
        var sb = new StringBuilder();
        
        sb.AppendLine("graph TD");
        sb.AppendLine();

        // Generate nodes
        foreach (var entity in report.Entities)
        {
            var nodeId = SanitizeId(entity.Id);
            var label = GenerateLabel(entity);
            var style = GetNodeStyle(entity.Type);

            sb.AppendLine($"    {nodeId}[\"{label}\"]");
            if (!string.IsNullOrEmpty(style))
            {
                sb.AppendLine($"    {style}({nodeId})");
            }
        }

        sb.AppendLine();

        // Generate edges (parent-child relationships)
        foreach (var entity in report.Entities.Where(e => !string.IsNullOrEmpty(e.ParentId)))
        {
            var childId = SanitizeId(entity.Id);
            var parentId = SanitizeId(entity.ParentId!);
            
            sb.AppendLine($"    {parentId} --> {childId}");
        }

        sb.AppendLine();

        // Add styling
        sb.AppendLine("    classDef proxmox fill:#2196F3,stroke:#1976D2,color:#fff");
        sb.AppendLine("    classDef vm fill:#4CAF50,stroke:#388E3C,color:#fff");
        sb.AppendLine("    classDef docker fill:#2496ED,stroke:#1E88E5,color:#fff");
        sb.AppendLine("    classDef container fill:#FF9800,stroke:#F57C00,color:#fff");
        sb.AppendLine("    classDef portainer fill:#13BEF9,stroke:#0288D1,color:#fff");
        sb.AppendLine("    classDef unreachable fill:#9E9E9E,stroke:#616161,color:#fff");

        return sb.ToString();
    }

    private static string GenerateLabel(Entity entity)
    {
        var name = string.IsNullOrEmpty(entity.Name) ? entity.Type.ToString() : entity.Name;
        var ip = !string.IsNullOrEmpty(entity.Ip) ? entity.Ip : "N/A";
        
        var statusIcon = entity.Status switch
        {
            ReachabilityStatus.Reachable => "✓",
            ReachabilityStatus.Unreachable => "✗",
            ReachabilityStatus.Unverified => "?",
            _ => ""
        };

        return $"{entity.Type}\\n{name}\\n{ip} {statusIcon}";
    }

    private static string GetNodeStyle(EntityType type)
    {
        return type switch
        {
            EntityType.Proxmox => "class",
            EntityType.Vm or EntityType.Lxc => "class",
            EntityType.DockerHost => "class",
            EntityType.Container => "class",
            EntityType.PortainerService or EntityType.PortainerStack => "class",
            _ => ""
        };
    }

    private static string SanitizeId(string id)
    {
        // Remove characters that Mermaid doesn't like
        return id.Replace("-", "_").Replace(".", "_").Replace("/", "_");
    }
}
