Below is a clean, implementation-ready **design document** you can hand directly to Claude.
It fully describes the architecture, data flow, detection logic, and output model for your network-wide homelab scanner.

---

# Homelab Network Discovery & Mapping Service

**Design Document**

---

# 1. Overview

This service performs **agentless discovery** of an entire homelab network, identifies every reachable device, detects their platform type (Proxmox, Portainer, Docker hosts, containers, NAS, generic servers), and reconstructs the **nested topology**:

```
Proxmox node → VM/LXC → Docker host → Containers → Services
```

It runs as a **single binary**, scanning any subnet(s), probing each IP, querying APIs when available, and correlating internal metadata to build a unified tree.

---

# 2. Goals

* Autonomous, zero-config scanning.
* Identify all nodes on the LAN.
* Detect virtualization layers:

  * Proxmox nodes
  * VMs/LXCs via Proxmox API
  * Docker hosts via Docker/Portainer APIs
  * Containers via Docker API
* Detect front-facing services (Nginx, etc.).
* Correlate internal container IPs with externally-visible addresses.
* Build a correct parent/child hierarchy across multiple layers.
* Export results as:

  * JSON (machine-readable)
  * Markdown report
  * Mermaid diagram (topology map)

---

# 3. Architecture

**Single process** with the following internal modules:

### 3.1 Modules

1. **Network Discovery**

   * ARP sweep
   * Ping sweep
   * Port scan (targeted)
   * Host info collection (DNS, HTTP headers, SSH banner, etc.)

2. **Fingerprinting & Platform Detection**

   * Proxmox detector
   * Portainer detector
   * Docker host detector
   * NAS detector (Synology/TrueNAS/QNAP)
   * Generic service detection (nginx, sshd, etc.)

3. **Metadata Integration Layer**

   * Query Proxmox API
   * Query Portainer API
   * Query Docker Engine API
   * Extract container IPs for correlation

4. **Relationship Builder**

   * Correlates entities by:

     * IP mapping
     * Proxmox VM ↔ IP
     * Portainer/Docker container ↔ internal IP
     * Upstream VM host ↔ Proxmox node

5. **Report Generator**

   * Constructs hierarchical model
   * Outputs JSON, Markdown, Mermaid diagram

---

# 4. Data Flow

Below shows how data flows through the system during a scan:

1. **Discovery**

   * Sweep subnet → collect list of active IPs.
   * For each IP → perform port fingerprinting.

2. **Detection**

   * If port 8006 open → attempt Proxmox version API.
   * If port 9000 open → test Portainer API.
   * If docker API open on 2375/2376 → treat as Docker host.
   * If HTTP headers match known patterns → identify NAS.
   * Else → classify generic server.

3. **Metadata Retrieval**
   For each identified component:

   * **Proxmox**

     * Query `/nodes/<node>/qemu` and `/lxc` for VMs/LXCs.
     * Query guest-agent endpoints to retrieve VM IPs when possible.
   * **Portainer**

     * Query `/api/endpoints/1/docker/containers/json`.
     * Extract container internal IPs.
   * **Docker Host**

     * Query `/containers/json` directly.
     * Extract container internal IPs.

4. **Correlation**

   * Match container internal IPs ↔ discovered active IPs.
   * Match Portainer host IP ↔ Proxmox node VM/LXC IPs.
   * Insert hierarchy links:

     * Container → Docker Host
     * Docker Host → VM/LXC → Proxmox Node

5. **Output**

   * Generate a nested object representing the infrastructure.
   * Produce final reports.

---

# 5. Detection Logic Details

### 5.1 Proxmox Node Detection

Criteria:

* Port `8006` open
* HTTPS GET to `/api2/json/version` returns valid JSON
* Optional: TLS certificate CN contains “pve”

Outputs:

* Node name, version, API access
* VM/LXC lists (through subsequent API calls)
* VM/LXC IPs (via guest agent if available)

---

### 5.2 VM/LXC → Proxmox Node Relationship

Proxmox API provides:

* VMID
* Name
* Status
* Network interfaces
* Agent-reported IP addresses

Match VM/LXC IPs with discovered IPs.

---

### 5.3 Portainer Host Detection

Criteria:

* Port `9000` or `9443` open
* Query `/api/status` returns JSON
* Query `/api/endpoints/1/docker/containers/json` yields container metadata

Outputs:

* Containers and their internal IPs
* Portainer version
* Docker endpoint type

---

### 5.4 Docker Host Detection

Criteria:

* TCP port `2375` or `2376` is responsive
* `/version` responds with Docker engine signature

Outputs:

* List of containers
* Each container’s IP and network info

---

### 5.5 Container → Host Relationship

If a container reports:

```
NetworkSettings.Networks.*.IPAddress = X.X.X.X
```

And the scanner previously discovered that IP as an active host:

→ That host is actually a container.

Insert into hierarchy:

```
Docker Host → Container(X.X.X.X)
```

---

### 5.6 Example Correlation for Your Case

Discovered:

* 192.168.1.51 → Proxmox
* 192.168.1.80 → Portainer
* 192.168.1.120 → Nginx container

Correlation results:

* Portainer’s Docker endpoint reports a container with internal IP `192.168.1.120`
* Proxmox VM list reports a VM with IP `192.168.1.80`
* Thus:

```
Proxmox(51)
 └─ VM @ 80 (Portainer Host)
     └─ Container @ 120 (nginx)
```

---

# 6. Data Models

### Entity object:

```
Entity {
  id: string
  ip: string
  type: enum(proxmox, vm, lxc, docker_host, container, nas, service, unknown)
  name: string
  parent_id: string | null
  metadata: map[string]any
}
```

### Output structure:

```
Topology {
  nodes: []Entity
}
```

---

# 7. Output Formats

### 7.1 JSON

Machine-readable for later tooling.

### 7.2 Markdown

Human-readable report summarizing each node.

### 7.3 Mermaid diagram

For visual maps:

```
graph TD
  PVE51 --> VM_80
  VM_80 --> Docker_80
  Docker_80 --> Container_120
```

---

# 8. Technologies

Implementation language: **C#**

Dependencies:

* ICMP ping library
* Nmap wrapper
* HTTP client
* JSON parser
* Concurrency via Threads and Async tasks

---

# 9. Execution Flow Summary

1. Input: Subnet (e.g., `192.168.1.0/24`)
2. Discover active IPs
3. Port fingerprint each IP
4. Detect platform type per IP
5. Pull metadata from detected platforms
6. Correlate VM and container IPs
7. Build hierarchical topology
8. Export reports

---