using System.Text;
using System.Text.RegularExpressions;
using Renci.SshNet;

namespace HomelabMapper.Integration;

public class ProxmoxSshClient : IDisposable
{
    private readonly SshClient _sshClient;
    private readonly string _nodeIp;
    private bool _isConnected;
    private bool _disposed;

    public ProxmoxSshClient(string nodeIp, string username, string? password = null, string? privateKeyPath = null)
    {
        _nodeIp = nodeIp;

        AuthenticationMethod authMethod;
        if (!string.IsNullOrEmpty(privateKeyPath) && File.Exists(privateKeyPath))
        {
            // SSH Key authentication
            authMethod = new PrivateKeyAuthenticationMethod(username, new PrivateKeyFile(privateKeyPath));
        }
        else if (!string.IsNullOrEmpty(password))
        {
            // Password authentication
            authMethod = new PasswordAuthenticationMethod(username, password);
        }
        else
        {
            throw new ArgumentException("Either password or private key path must be provided");
        }

        var connectionInfo = new ConnectionInfo(nodeIp, 22, username, authMethod);
        _sshClient = new SshClient(connectionInfo);
    }

    public async Task<bool> ConnectAsync()
    {
        try
        {
            if (_sshClient.IsConnected) return true;

            await Task.Run(() => _sshClient.Connect());
            _isConnected = _sshClient.IsConnected;
            return _isConnected;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] SSH connection to {_nodeIp} failed: {ex.Message}");
            return false;
        }
    }

    public async Task<Dictionary<int, List<string>>> GetVmIpAddressesAsync(List<int> vmIds)
    {
        var results = new Dictionary<int, List<string>>();
        
        if (!_isConnected || !_sshClient.IsConnected)
        {
            if (!await ConnectAsync())
                return results;
        }

        foreach (var vmId in vmIds)
        {
            var ips = await GetVmIpAsync(vmId);
            if (ips.Any())
            {
                results[vmId] = ips;
            }
        }

        return results;
    }

    public async Task<Dictionary<int, List<string>>> GetLxcIpAddressesAsync(List<int> lxcIds)
    {
        var results = new Dictionary<int, List<string>>();
        
        if (!_isConnected || !_sshClient.IsConnected)
        {
            if (!await ConnectAsync())
                return results;
        }

        foreach (var lxcId in lxcIds)
        {
            var ips = await GetLxcIpAsync(lxcId);
            if (ips.Any())
            {
                results[lxcId] = ips;
            }
        }

        return results;
    }

    private async Task<List<string>> GetVmIpAsync(int vmId)
    {
        var ips = new List<string>();

        try
        {
            // Method 1: Try QEMU Guest Agent
            var guestAgentIps = await TryQemuGuestAgent(vmId);
            if (guestAgentIps.Any())
            {
                ips.AddRange(guestAgentIps);
                return ips;
            }

            // Method 2: Try guest exec if guest agent is available
            var guestExecIps = await TryQemuGuestExec(vmId);
            if (guestExecIps.Any())
            {
                ips.AddRange(guestExecIps);
                return ips;
            }

            // Method 3: Fallback to config parsing and DHCP lease lookup
            var configIps = await TryVmConfigParsing(vmId);
            ips.AddRange(configIps);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] Error getting VM {vmId} IP: {ex.Message}");
        }

        return ips;
    }

    private async Task<List<string>> GetLxcIpAsync(int lxcId)
    {
        var ips = new List<string>();

        try
        {
            // Method 1: Try direct LXC attach
            var lxcIps = await TryLxcAttach(lxcId);
            if (lxcIps.Any())
            {
                ips.AddRange(lxcIps);
                return ips;
            }

            // Method 2: Try pct exec
            var pctExecIps = await TryPctExec(lxcId);
            if (pctExecIps.Any())
            {
                ips.AddRange(pctExecIps);
                return ips;
            }

            // Method 3: Fallback to config parsing
            var configIps = await TryLxcConfigParsing(lxcId);
            ips.AddRange(configIps);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] Error getting LXC {lxcId} IP: {ex.Message}");
        }

        return ips;
    }

    private async Task<List<string>> TryQemuGuestAgent(int vmId)
    {
        var ips = new List<string>();
        
        try
        {
            var command = $"qm guest cmd {vmId} network-get-interfaces";
            var result = await ExecuteCommandAsync(command);
            
            if (result.ExitStatus == 0 && !string.IsNullOrEmpty(result.Output))
            {
                // Parse JSON response from guest agent
                var ipMatches = Regex.Matches(result.Output, @"""ip-address""\s*:\s*""([^""]+)""");
                foreach (Match match in ipMatches)
                {
                    var ip = match.Groups[1].Value;
                    if (IsValidIpAddress(ip) && !IsLoopbackOrLinkLocal(ip))
                    {
                        ips.Add(ip);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] QEMU guest agent failed for VM {vmId}: {ex.Message}");
        }

        return ips;
    }

    private async Task<List<string>> TryQemuGuestExec(int vmId)
    {
        var ips = new List<string>();
        
        try
        {
            var command = $"qm guest exec {vmId} -- ip -4 addr show | grep 'inet ' | grep -v '127.0.0.1'";
            var result = await ExecuteCommandAsync(command);
            
            if (result.ExitStatus == 0)
            {
                ips.AddRange(ExtractIpsFromOutput(result.Output));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] QEMU guest exec failed for VM {vmId}: {ex.Message}");
        }

        return ips;
    }

    private async Task<List<string>> TryLxcAttach(int lxcId)
    {
        var ips = new List<string>();
        
        try
        {
            var command = $"lxc-attach -n {lxcId} -- ip -4 addr show | grep 'inet ' | grep -v '127.0.0.1'";
            var result = await ExecuteCommandAsync(command);
            
            if (result.ExitStatus == 0)
            {
                ips.AddRange(ExtractIpsFromOutput(result.Output));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] LXC attach failed for LXC {lxcId}: {ex.Message}");
        }

        return ips;
    }

    private async Task<List<string>> TryPctExec(int lxcId)
    {
        var ips = new List<string>();
        
        try
        {
            var command = $"pct exec {lxcId} -- ip -4 addr show | grep 'inet ' | grep -v '127.0.0.1'";
            var result = await ExecuteCommandAsync(command);
            
            if (result.ExitStatus == 0)
            {
                ips.AddRange(ExtractIpsFromOutput(result.Output));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] PCT exec failed for LXC {lxcId}: {ex.Message}");
        }

        return ips;
    }

    private async Task<List<string>> TryVmConfigParsing(int vmId)
    {
        var ips = new List<string>();
        
        try
        {
            var command = $"cat /etc/pve/qemu-server/{vmId}.conf | grep net";
            var result = await ExecuteCommandAsync(command);
            
            if (result.ExitStatus == 0)
            {
                var ipMatches = Regex.Matches(result.Output, @"ip=([0-9.]+)");
                foreach (Match match in ipMatches)
                {
                    var ip = match.Groups[1].Value;
                    if (IsValidIpAddress(ip))
                    {
                        ips.Add(ip);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] VM config parsing failed for VM {vmId}: {ex.Message}");
        }

        return ips;
    }

    private async Task<List<string>> TryLxcConfigParsing(int lxcId)
    {
        var ips = new List<string>();
        
        try
        {
            var command = $"cat /etc/pve/lxc/{lxcId}.conf | grep 'net[0-9]'";
            var result = await ExecuteCommandAsync(command);
            
            if (result.ExitStatus == 0)
            {
                var ipMatches = Regex.Matches(result.Output, @"ip=([0-9.]+)");
                foreach (Match match in ipMatches)
                {
                    var ip = match.Groups[1].Value;
                    if (IsValidIpAddress(ip))
                    {
                        ips.Add(ip);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] LXC config parsing failed for LXC {lxcId}: {ex.Message}");
        }

        return ips;
    }

    private async Task<SshCommandResult> ExecuteCommandAsync(string command)
    {
        return await Task.Run(() =>
        {
            using var cmd = _sshClient.CreateCommand(command);
            cmd.CommandTimeout = TimeSpan.FromSeconds(30);
            
            var output = cmd.Execute();
            
            return new SshCommandResult
            {
                Output = output,
                Error = cmd.Error,
                ExitStatus = cmd.ExitStatus ?? -1
            };
        });
    }

    private List<string> ExtractIpsFromOutput(string output)
    {
        var ips = new List<string>();
        
        // Extract IPs from "inet x.x.x.x/xx" format
        var ipMatches = Regex.Matches(output, @"inet\s+([0-9.]+)");
        foreach (Match match in ipMatches)
        {
            var ip = match.Groups[1].Value;
            if (IsValidIpAddress(ip) && !IsLoopbackOrLinkLocal(ip))
            {
                ips.Add(ip);
            }
        }

        return ips;
    }

    private static bool IsValidIpAddress(string ip)
    {
        return System.Net.IPAddress.TryParse(ip, out _);
    }

    private static bool IsLoopbackOrLinkLocal(string ip)
    {
        if (!System.Net.IPAddress.TryParse(ip, out var addr))
            return true;

        return System.Net.IPAddress.IsLoopback(addr) || 
               ip.StartsWith("169.254.") || // Link-local
               ip.StartsWith("127.");        // Loopback range
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _sshClient?.Dispose();
            _disposed = true;
        }
    }
}

public class SshCommandResult
{
    public string Output { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public int ExitStatus { get; set; }
}