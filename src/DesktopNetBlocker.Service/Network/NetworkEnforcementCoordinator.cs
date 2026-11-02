using System;
using System.Collections.Generic;
using DesktopNetBlocker.Core.Data;
using DesktopNetBlocker.Core.Models;
using Microsoft.Extensions.Logging;

namespace DesktopNetBlocker.Service.Network;

public class NetworkEnforcementCoordinator : IDisposable
{
    private readonly ILogger<NetworkEnforcementCoordinator> _logger;
    private readonly BlockerDbContext _db;
    private readonly DnsProxyServer _dnsServer;
    private readonly HostsFileManager _hostsManager;
    private readonly FirewallPolicyManager _firewallManager;
    private readonly AdapterDnsManager _adapterManager;

    public NetworkEnforcementCoordinator(ILogger<NetworkEnforcementCoordinator> logger, BlockerDbContext db)
    {
        _logger = logger;
        _db = db;
        _dnsServer = new DnsProxyServer(logger, db);
        _hostsManager = new HostsFileManager(logger);
        _firewallManager = new FirewallPolicyManager(logger);
        _adapterManager = new AdapterDnsManager(logger);
    }

    public void StartEnforcement()
    {
        _logger.LogInformation("Starting multi-layer network restriction enforcement...");

        // 1. Start local DNS proxy server
        _dnsServer.Start();

        // 2. Synchronize hosts file fallback layer
        SyncRules();

        // 3. Configure network adapters
        _adapterManager.ConfigureLoopbackDns();

        // 4. Enforce Windows Firewall anti-bypass rules
        _firewallManager.EnforceFirewallRules();

        _logger.LogInformation("Multi-layer network enforcement activated.");
    }

    public void SyncRules()
    {
        var activeBlocked = _db.GetActiveBlockedDomains();
        var tempAccess = _db.GetActiveTemporaryAccessList();
        var tempSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tempAccess)
        {
            tempSet.Add(t.TargetDomain);
        }

        _dnsServer.RefreshRules();
        _hostsManager.SyncHostsFile(activeBlocked, tempSet);
    }

    public void StopEnforcement()
    {
        _logger.LogInformation("Stopping network enforcement...");
        _dnsServer.Stop();
        _adapterManager.RestoreDefaultDns();
        _firewallManager.RemoveFirewallRules();
        _hostsManager.RemoveManagedEntries();
    }

    public void Dispose()
    {
        StopEnforcement();
        _dnsServer.Dispose();
    }
}

