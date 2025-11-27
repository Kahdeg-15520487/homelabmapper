namespace HomelabMapper.Core.Models;

public enum ReachabilityStatus
{
    Reachable,      // Responds to ping/port scan
    Unreachable,    // Known IP (e.g. 172.17.x.x) but not accessible
    Unverified,     // API says it exists, but scan failed
    Conflicting,    // Multiple sources disagree
    Stale           // Was reachable in previous scan, not now
}
