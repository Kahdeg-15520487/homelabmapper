# Homelab Network Mapper

A modular, extensible network discovery and mapping service for homelab environments. Automatically detects and maps Proxmox nodes, VMs, Docker hosts, Portainer stacks, containers, and NAS devices, building a complete topology of your infrastructure.

## Features

- **Autonomous Network Discovery**: Ping/ARP sweep to find active hosts
- **Port Fingerprinting**: Identifies services by open ports and HTTP headers
- **Modular Scanner Architecture**: Plugin-based system for platform detection
- **Deep Topology Mapping**: Reconstructs nested hierarchies (Proxmox → VM → Docker → Portainer → Stack → Container)
- **Conflict Detection**: Identifies discrepancies between network scans and API data
- **Diff Mode**: Compare scans over time to detect infrastructure changes
- **SSL Support**: Handles self-signed certificates with tracking
- **Best-Effort Scanning**: Individual failures don't abort the entire scan

## Architecture

### Project Structure

```
HomelabMapper/
├── src/
│   ├── HomelabMapper.Core/          # Domain models, interfaces, orchestration
│   ├── HomelabMapper.Discovery/     # Network and port scanning
│   ├── HomelabMapper.Detectors/     # Platform-specific scanners (Proxmox, Docker, etc.)
│   ├── HomelabMapper.Integration/   # API clients for external services
│   ├── HomelabMapper.Correlation/   # Topology building and IP correlation
│   ├── HomelabMapper.Reporting/     # Output generators (JSON, Markdown, Mermaid)
│   └── HomelabMapper.CLI/           # Command-line interface
└── tests/                           # Unit and integration tests
```

### Core Components

1. **ScanOrchestrator**: Manages the scan lifecycle and entity queue
2. **ScannerRegistry**: Holds and activates platform-specific scanners
3. **IHostScanner**: Interface for implementing new platform detectors
4. **NetworkScanner**: Performs ping sweeps and host discovery
5. **PortScanner**: Fingerprints hosts by scanning common ports

## Current Status

### ✅ Completed - Fully Functional!
- [x] Solution structure with 7 projects
- [x] Core domain models (Entity, EntityType, ReachabilityStatus, CertificateInfo, Conflict)
- [x] Scanner interface with dependency management (`IHostScanner`, `DependsOn`)
- [x] ScannerRegistry with activation criteria matching
- [x] ScanOrchestrator with queue-based scanning and conflict detection
- [x] Network discovery (ping sweep with parallel execution)
- [x] Port scanning and HTTP header detection
- [x] **Platform-specific scanners:**
  - [x] ProxmoxHostScanner (detects VMs/LXCs)
  - [x] DockerHostScanner (detects containers)
  - [x] PortainerScanner (detects stacks)
- [x] **API clients with SSL certificate tracking:**
  - [x] ProxmoxApiClient
  - [x] DockerApiClient
  - [x] PortainerApiClient
- [x] **Correlation engine:**
  - [x] Reparent containers to Portainer stacks
  - [x] Match VM IPs with discovered hosts
  - [x] Identify Portainer containers
- [x] **Diff engine for change detection**
  - [x] Compare scans and detect added/removed/modified entities
  - [x] Generate diff reports
- [x] **Report generators:**
  - [x] JSON (machine-readable)
  - [x] Markdown (human-readable with icons)
  - [x] Mermaid (topology diagrams)
- [x] **Configuration loader (YAML)**
  - [x] Subnet configuration
  - [x] Credential management with environment variables
  - [x] Diff settings
  - [x] Output paths

### 🎯 Ready to Use
The scanner is **fully functional** and ready to scan your homelab!

## Quick Start

### Build

```bash
dotnet build HomelabMapper.sln
```

### Run

```bash
# Run with default config (looks for config.yaml)
dotnet run --project src/HomelabMapper.CLI/HomelabMapper.CLI.csproj

# Or specify a config file
dotnet run --project src/HomelabMapper.CLI/HomelabMapper.CLI.csproj -- /path/to/config.yaml

# Or publish and run as standalone
dotnet publish src/HomelabMapper.CLI/HomelabMapper.CLI.csproj -c Release
./src/HomelabMapper.CLI/bin/Release/net9.0/HomelabMapper.CLI
```

### Configuration

Copy `config.example.yaml` to `config.yaml` and customize:

```yaml
scan:
  subnets:
    - 192.168.1.0/24
  timeout_ms:
    ping: 500
    http: 3000

credentials:
  proxmox:
    token_env: "PROXMOX_TOKEN"
  portainer:
    token_env: "PORTAINER_TOKEN"
```

## Extending with New Scanners

Implement the `IHostScanner` interface:

```csharp
public class MyCustomScanner : IHostScanner
{
    public string ScannerName => "MyService";
    public int Priority => 20;
    public List<string> DependsOn => new();
    
    public ScannerActivationCriteria GetActivationCriteria()
    {
        return new ScannerActivationCriteria
        {
            RequiredOpenPorts = new List<int> { 8080 },
            RequiredUrlPatterns = new List<string> { "/api/health" }
        };
    }
    
    public async Task<ScanResult> ScanAsync(Entity host, ScannerContext context)
    {
        // Your detection logic here
        return ScanResult.Successful(discoveredEntities);
    }
    
    public IEnumerable<Type> GetChildScannerTypes(ScanResult result)
    {
        return Array.Empty<Type>();
    }
}
```

Register in `Program.cs`:

```csharp
registry.Register(new MyCustomScanner());
```

## Design Documents

- `design-doc.md` - Original design (v1)
- `design-doc-v2.md` - Updated modular architecture

## License

MIT

## Example Output

After running a scan, you'll get:

**Console Output:**
```
=== Homelab Network Mapper ===

[INFO] 14:30:22 Starting network discovery...
[INFO] 14:30:22 Scanning subnets: 192.168.1.0/24
[INFO] 14:30:28 Discovered 15 active hosts
[INFO] 14:30:28 Port scanning discovered hosts...
[INFO] 14:30:35 Port scanning complete. Found 15 entities
[INFO] 14:30:35 Starting platform detection and API scanning...
[INFO] 14:30:36 Activating Proxmox for 192.168.1.51
[INFO] 14:30:37 Detected Proxmox 8.1.3 at 192.168.1.51
[INFO] 14:30:38 Proxmox scan found 8 VMs/LXCs
[INFO] 14:30:39 Activating Docker for 192.168.1.80
[INFO] 14:30:40 Detected Docker 24.0.7 at 192.168.1.80
[INFO] 14:30:41 Docker scan found 12 containers
[INFO] 14:30:42 Activating Portainer for 192.168.1.80
[INFO] 14:30:43 Detected Portainer 2.19.4 at 192.168.1.80
[INFO] 14:30:44 Portainer scan found 3 stacks

=== Scan Results ===
Scan ID: scan-20251127-143044
Total Entities: 38

Entities by Type:
  Container: 12
  Vm: 8
  PortainerStack: 3
  Proxmox: 1
  DockerHost: 1
  ...

✅ Scan complete!
```

**Generated Files:**
- `scan-results.json` - Full machine-readable scan data
- `report.md` - Human-readable topology with conflicts
- `topology.mmd` - Mermaid diagram for visualization
- `changes.md` - Diff report (if enabled)

## Next Steps

**Future Enhancements:**
1. Add Unraid scanner implementation
2. Add Kubernetes/K3s scanner
3. Add TrueNAS/Synology/QNAP NAS detectors
4. Add unit and integration tests
5. Add web UI for visualization
6. Add scheduled scanning with notifications
7. Add metrics export (Prometheus/InfluxDB)
8. Add network performance testing
