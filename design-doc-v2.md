# Homelab Network Discovery & Mapping Service (v2)

**Design Document**

---

# 1. Overview

This service performs **agentless discovery** of an entire homelab network, identifies every reachable device, detects their platform type (Proxmox, Portainer, Docker hosts, containers, NAS, generic servers), and reconstructs the **nested topology**.

It uses a **modular, plugin-based architecture** where specialized scanners (`IHostScanner`) are activated based on detection criteria (ports, headers). It supports **diff-based reporting** to track network changes over time and handles **conflicts** by reporting them for user review rather than guessing.

---

# 2. Goals

*   **Autonomous Discovery**: Scan subnets, find active IPs, fingerprint services.
*   **Modular Architecture**: Easily extensible scanner plugins (Proxmox, Docker, Portainer, Unraid, etc.).
*   **Deep Topology**: Reconstruct `Proxmox -> VM -> Docker -> Portainer -> Stack -> Container`.
*   **Change Detection**: Compare current scan with previous scans to report added/removed/modified entities.
*   **Conflict Awareness**: Identify and report discrepancies between Network Scans and API data.
*   **Resilience**: Best-effort scanning; individual host failures do not abort the process.
*   **Security**: Support for self-signed certificates (with tracking) and secure credential management.

---

# 3. Architecture

The system is built around a **Scan Orchestrator** that manages a queue of entities and a registry of **Host Scanners**.

### 3.1 Core Components

1.  **Scan Orchestrator**: Manages the scan lifecycle, entity queue, and conflict detection.
2.  **Scanner Registry**: Holds available `IHostScanner` implementations.
3.  **IHostScanner**: Interface for platform-specific discovery logic.
4.  **Diff Engine**: Compares topology snapshots.
5.  **Reporters**: Output generators (JSON, Markdown, Mermaid).

### 3.2 The `IHostScanner` Interface

```csharp
interface IHostScanner {
    string ScannerName { get; }
    int Priority { get; }
    
    // Explicit dependencies to ensure correct execution order
    // e.g., Portainer scanner depends on Docker scanner to exist first
    List<string> DependsOn { get; } 

    // Determines if this scanner should run for a given host
    ScannerActivationCriteria GetActivationCriteria();

    // Performs the scan and returns discovered children
    Task<ScanResult> ScanAsync(Entity host, ScannerContext context);
}

class ScannerActivationCriteria {
    List<int> RequiredOpenPorts;
    Dictionary<string, string> RequiredHttpHeaders;
    List<string> RequiredUrlPatterns;
    Func<Entity, bool> CustomPredicate;
}

class ScannerContext {
    HttpClient Client; // Pre-configured with SSL settings
    ICredentialStore Credentials;
    ILogger Logger;
}
```

---

# 4. Data Models

### 4.1 Entity

```csharp
class Entity {
    string Id;                  // Unique ID (UUID or API-provided ID)
    string Ip;
    EntityType Type;            // Proxmox, VM, DockerHost, Container, PortainerStack, etc.
    string Name;
    string ParentId;            // ID of the hosting entity
    
    ReachabilityStatus Status;  // Reachable, Unreachable, Unverified, Stale
    CertificateInfo Certificate;// SSL details if applicable
    
    Dictionary<string, object> Metadata; // Extensible data (OS version, Agent versions, etc.)
}
```

### 4.2 Enums

```csharp
enum EntityType {
    Proxmox, Vm, Lxc,
    DockerHost, Container,
    PortainerService, PortainerStack,
    Nas, Unraid,
    Service, Unknown
}

enum ReachabilityStatus {
    Reachable,      // Responds to ping/port scan
    Unreachable,    // Known IP (e.g. 172.17.x.x) but not accessible
    Unverified,     // API says it exists, but scan failed
    Conflicting     // Multiple sources disagree
}
```

### 4.3 Conflict

```csharp
class Conflict {
    string Ip;
    ConflictType Type; // TypeMismatch, IpMismatch, UnverifiedEntity
    List<Entity> InvolvedEntities;
    string Description;
}
```

---

# 5. Scanners & Logic

### 5.1 Proxmox Scanner
*   **Criteria**: Port 8006 open, `/api2/json/version` accessible.
*   **Action**: Query API for Nodes, VMs, LXCs.
*   **Output**: VM/LXC entities.
*   **Dependencies**: None.

### 5.2 Unraid Scanner
*   **Criteria**: Port 80/443 open, Server header contains "unRAID".
*   **Action**: Query API for Docker containers and VMs.
*   **Output**: VM and Container entities.
*   **Dependencies**: None.

### 5.3 Docker Host Scanner
*   **Criteria**: Ports 2375/2376 open OR identified as VM from Proxmox scan.
*   **Action**: Query `/containers/json`.
*   **Output**: Container entities.
*   **Logic**:
    *   If container IP is in `172.x` or `10.x` (bridge), mark `Status = Unreachable` (unless port mapped).
    *   If container IP matches a discovered LAN IP, mark `Status = Reachable`.
*   **Dependencies**: None.

### 5.4 Portainer Scanner
*   **Criteria**: Ports 9000/9443 open.
*   **Dependencies**: `["Docker"]` (Must run after Docker scan to correctly reparent containers).
*   **Action**:
    1.  Identify the Portainer container itself (from Docker scan results).
    2.  Query Portainer API for Stacks.
    3.  **Reparenting**: Move Containers from `DockerHost` -> `PortainerStack`.
*   **Constraint**: Only supports 1 level of Portainer nesting.

---

# 6. Execution Flow

1.  **Configuration Load**: Read YAML config (subnets, credentials, SSL settings).
2.  **Network Discovery**: Ping/ARP sweep subnets to find active IPs.
3.  **Port Fingerprinting**: Scan common ports (8006, 9000, 2375, 80, 443, etc.) on active IPs.
4.  **Orchestration Loop**:
    *   Queue all discovered IPs as initial Entities.
    *   While Queue is not empty:
        *   Dequeue Entity.
        *   Find applicable Scanners (matching criteria).
        *   Sort Scanners by `Priority` and respect `DependsOn`.
        *   Execute Scanners.
        *   Add discovered children (VMs, Containers) to Queue.
5.  **Conflict Detection**: Analyze `_allEntities` for IP collisions or type mismatches.
6.  **Reporting**: Generate JSON/Markdown/Mermaid.
7.  **Diff (Optional)**: If enabled, compare with previous scan and generate Diff Report.

---

# 7. Configuration Schema

```yaml
scan:
  subnets:
    - 192.168.1.0/24
  timeout_ms:
    ping: 500
    http: 3000
  parallel_scans: 50
  ssl:
    accept_self_signed: true
    log_certificate_details: true

credentials:
  proxmox:
    token_env: "PROXMOX_TOKEN"
  portainer:
    token_env: "PORTAINER_TOKEN"

diff:
  enabled: true
  history_dir: ".homelabmapper/scans"
  keep_last: 10

output:
  json: "scan-results.json"
  markdown: "report.md"
  mermaid: "topology.mmd"
```

---

# 8. Conflict Resolution

Conflicts are **not auto-resolved**. They are collected and appended to the report.

**Types of Conflicts:**
1.  **Type Mismatch**: IP 192.168.1.50 is identified as both a Proxmox Node and a Docker Container.
2.  **Ghost Entity**: API reports VM at 192.168.1.60, but Network Scan found nothing (Status: Unverified).
3.  **IP Drift**: Proxmox Guest Agent says 192.168.1.60, but DNS/ARP says 192.168.1.65.

---

# 9. Diff Mode

Compares the current scan against the most recent JSON snapshot.

**Reported Changes:**
*   **Topology**: New/Removed Entities (VMs, Containers).
*   **Reachability**: Service went from Reachable -> Unreachable.
*   **Addressing**: IP address changes.
*   **Metadata**: Significant version changes (if configured).

**Output**: A separate section in the Markdown report or a dedicated Diff file.
