using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DesktopNetBlocker.Service.Network;

public class FirewallPolicyManager
{
    private const string RuleBlockOutboundDns = "DesktopNetBlocker-BlockOutboundDNS";
    private const string RuleBlockDoT = "DesktopNetBlocker-BlockDoT";
    private const string RuleBlockDoH = "DesktopNetBlocker-BlockDoH";

    private readonly ILogger _logger;

    public FirewallPolicyManager(ILogger logger)
    {
        _logger = logger;
    }

    public void EnforceFirewallRules()
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogInformation("Skipping Windows Firewall configuration on non-Windows environment.");
            return;
        }

        try
        {
            // 1. Block outbound UDP and TCP port 53 (direct DNS bypass prevention)
            RunNetsh($"advfirewall firewall add rule name=\"{RuleBlockOutboundDns}\" dir=out action=block protocol=UDP remoteport=53");
            RunNetsh($"advfirewall firewall add rule name=\"{RuleBlockOutboundDns}-TCP\" dir=out action=block protocol=TCP remoteport=53");

            // 2. Block outbound port 853 (DNS-over-TLS bypass prevention)
            RunNetsh($"advfirewall firewall add rule name=\"{RuleBlockDoT}\" dir=out action=block protocol=TCP remoteport=853");

            // 3. Block well-known public DoH resolver IPs on port 443 (Cloudflare, Google, Quad9)
            // 1.1.1.1, 1.0.0.1, 8.8.8.8, 8.8.4.4, 9.9.9.9, 149.112.112.112
            string dohIps = "1.1.1.1,1.0.0.1,8.8.8.8,8.8.4.4,9.9.9.9,149.112.112.112";
            RunNetsh($"advfirewall firewall add rule name=\"{RuleBlockDoH}\" dir=out action=block protocol=TCP remoteport=443 remoteip={dohIps}");

            _logger.LogInformation("Windows Firewall anti-bypass rules enforced successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed applying Windows Firewall rules: {Message}", ex.Message);
        }
    }

    public void RemoveFirewallRules()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            RunNetsh($"advfirewall firewall delete rule name=\"{RuleBlockOutboundDns}\"");
            RunNetsh($"advfirewall firewall delete rule name=\"{RuleBlockOutboundDns}-TCP\"");
            RunNetsh($"advfirewall firewall delete rule name=\"{RuleBlockDoT}\"");
            RunNetsh($"advfirewall firewall delete rule name=\"{RuleBlockDoH}\"");

            _logger.LogInformation("Windows Firewall rules removed.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed removing Windows Firewall rules: {Message}", ex.Message);
        }
    }

    private static void RunNetsh(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = args,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proc = Process.Start(psi);
        proc?.WaitForExit(3000);
    }
}

