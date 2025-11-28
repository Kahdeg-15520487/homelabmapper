using HomelabMapper.Core.Models;
using System.Net.Sockets;

namespace HomelabMapper.Discovery;

public class PortScanner
{
    private static readonly int[] CommonPorts = new[]
    {
        22,    // SSH
        80,    // HTTP
        443,   // HTTPS
        2375,  // Docker
        2376,  // Docker TLS
        8006,  // Proxmox
        9000,  // Portainer
        9010,  // Portainer alt
        9443,  // Portainer HTTPS
        5000,  // Various services
        8080,  // HTTP alt
        3000   // Various services
    };

    public async Task<Entity> ScanHostAsync(string ip, int timeoutMs = 1000)
    {
        var entity = new Entity
        {
            Id = Guid.NewGuid().ToString(),
            Ip = ip,
            Status = ReachabilityStatus.Reachable
        };

        var semaphore = new SemaphoreSlim(10); // Limit concurrent port scans
        var tasks = CommonPorts.Select(async port =>
        {
            await semaphore.WaitAsync();
            try
            {
                if (await IsPortOpenAsync(ip, port, timeoutMs))
                {
                    lock (entity.OpenPorts)
                    {
                        entity.OpenPorts.Add(port);
                    }
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        // Try to get HTTP headers if port 80 or 443 is open
        if (entity.OpenPorts.Contains(80) || entity.OpenPorts.Contains(443))
        {
            entity.HttpHeaders = await TryGetHttpHeadersAsync(ip);
        }

        return entity;
    }

    private async Task<bool> IsPortOpenAsync(string ip, int port, int timeoutMs)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(ip, port);
            var timeoutTask = Task.Delay(timeoutMs);

            var completedTask = await Task.WhenAny(connectTask, timeoutTask);
            if (completedTask == connectTask && client.Connected)
            {
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private async Task<Dictionary<string, string>?> TryGetHttpHeadersAsync(string ip)
    {
        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(3)
            };

            // Try HTTPS first, then HTTP
            foreach (var scheme in new[] { "https", "http" })
            {
                try
                {
                    var response = await client.GetAsync($"{scheme}://{ip}/", HttpCompletionOption.ResponseHeadersRead);
                    var headers = new Dictionary<string, string>();

                    foreach (var header in response.Headers)
                    {
                        headers[header.Key] = string.Join(", ", header.Value);
                    }

                    if (response.Content.Headers != null)
                    {
                        foreach (var header in response.Content.Headers)
                        {
                            headers[header.Key] = string.Join(", ", header.Value);
                        }
                    }

                    return headers;
                }
                catch
                {
                    // Try next scheme
                }
            }
        }
        catch
        {
            // Ignore
        }

        return null;
    }
}
