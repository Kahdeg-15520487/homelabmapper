using System.Text.Json;
using System.Text.Json.Serialization;
using HomelabMapper.Core.Models;

namespace HomelabMapper.Reporting;

public class JsonReporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task WriteAsync(TopologyReport report, string filePath)
    {
        var json = JsonSerializer.Serialize(report, Options);
        await File.WriteAllTextAsync(filePath, json);
    }

    public static async Task<TopologyReport?> ReadAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(filePath);
        return JsonSerializer.Deserialize<TopologyReport>(json, Options);
    }
}
