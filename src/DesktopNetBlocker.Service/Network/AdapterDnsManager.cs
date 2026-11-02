using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DesktopNetBlocker.Service.Network;

public class AdapterDnsManager
{
    private readonly ILogger _logger;

    public AdapterDnsManager(ILogger logger)
    {
        _logger = logger;
    }

    public void ConfigureLoopbackDns()
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogInformation("Skipping adapter DNS configuration on non-Windows environment.");
            return;
        }

        try
        {
            // PowerShell command to set DNS to 127.0.0.1 on all active physical interfaces
            string script = @"
                Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -and $_.Virtual -ne $true } | ForEach-Object {
                    Set-DnsClientServerAddress -InterfaceIndex $_.InterfaceIndex -ServerAddresses ('127.0.0.1', '::1') -ErrorAction SilentlyContinue
                }
            ";
            RunPowerShell(script);
            _logger.LogInformation("Configured active network adapters to use local loopback DNS (127.0.0.1, ::1).");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed configuring adapter DNS: {Message}", ex.Message);
        }
    }

    public void RestoreDefaultDns()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            // Restore automatic DNS via DHCP
            string script = @"
                Get-NetAdapter | Where-Object { $_.Virtual -ne $true } | ForEach-Object {
                    Set-DnsClientServerAddress -InterfaceIndex $_.InterfaceIndex -ResetServerAddresses -ErrorAction SilentlyContinue
                }
            ";
            RunPowerShell(script);
            _logger.LogInformation("Restored network adapters to default DHCP DNS.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed restoring adapter DNS: {Message}", ex.Message);
        }
    }

    private static void RunPowerShell(string script)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proc = Process.Start(psi);
        proc?.WaitForExit(5000);
    }
}

