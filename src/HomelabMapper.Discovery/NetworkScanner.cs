using HomelabMapper.Core.Models;
using System.Net;
using System.Net.NetworkInformation;

namespace HomelabMapper.Discovery;

public class NetworkScanner
{
    public async Task<List<string>> DiscoverHostsAsync(List<string> subnets, int timeoutMs = 500)
    {
        var discoveredIPs = new HashSet<string>();
        var tasks = new List<Task<List<string>>>();

        foreach (var subnet in subnets)
        {
            tasks.Add(ScanSubnetAsync(subnet, timeoutMs));
        }

        var results = await Task.WhenAll(tasks);
        foreach (var result in results)
        {
            foreach (var ip in result)
            {
                discoveredIPs.Add(ip);
            }
        }

        return discoveredIPs.ToList();
    }

    private async Task<List<string>> ScanSubnetAsync(string subnet, int timeoutMs)
    {
        var ips = GenerateIPsFromSubnet(subnet);
        var discoveredIPs = new List<string>();
        var semaphore = new SemaphoreSlim(50); // Limit concurrent pings

        var tasks = ips.Select(async ip =>
        {
            await semaphore.WaitAsync();
            try
            {
                if (await PingHostAsync(ip, timeoutMs))
                {
                    lock (discoveredIPs)
                    {
                        discoveredIPs.Add(ip);
                    }
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return discoveredIPs;
    }

    private async Task<bool> PingHostAsync(string ip, int timeoutMs)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, timeoutMs);
            return reply.Status == IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }

    private List<string> GenerateIPsFromSubnet(string subnet)
    {
        // Parse CIDR notation (e.g., 192.168.1.0/24)
        var parts = subnet.Split('/');
        if (parts.Length != 2)
        {
            throw new ArgumentException($"Invalid subnet format: {subnet}");
        }

        var baseIp = parts[0];
        var prefixLength = int.Parse(parts[1]);

        // Handle /32 (single host) case
        if (prefixLength == 32)
        {
            return new List<string> { baseIp };
        }

        var ipBytes = IPAddress.Parse(baseIp).GetAddressBytes();
        var ipInt = BitConverter.ToUInt32(ipBytes.Reverse().ToArray(), 0);

        var hostBits = 32 - prefixLength;
        var hostsCount = (1u << hostBits) - 2; // Exclude network and broadcast addresses

        var ips = new List<string>();
        for (uint i = 1; i <= hostsCount && i < 256; i++) // Limit to reasonable size
        {
            var currentIp = ipInt + i;
            var currentBytes = BitConverter.GetBytes(currentIp).Reverse().ToArray();
            ips.Add(new IPAddress(currentBytes).ToString());
        }

        return ips;
    }
}
