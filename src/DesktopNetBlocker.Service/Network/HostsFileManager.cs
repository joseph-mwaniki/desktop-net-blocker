using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DesktopNetBlocker.Core.Models;
using Microsoft.Extensions.Logging;

namespace DesktopNetBlocker.Service.Network;

public class HostsFileManager
{
    private const string BeginMarker = "# BEGIN DESKTOP_NET_BLOCKER";
    private const string EndMarker = "# END DESKTOP_NET_BLOCKER";
    private readonly ILogger _logger;
    private readonly string _hostsFilePath;

    public HostsFileManager(ILogger logger, string? customHostsPath = null)
    {
        _logger = logger;
        if (!string.IsNullOrEmpty(customHostsPath))
        {
            _hostsFilePath = customHostsPath;
        }
        else if (OperatingSystem.IsWindows())
        {
            string systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            _hostsFilePath = Path.Combine(systemRoot, "System32", "drivers", "etc", "hosts");
        }
        else
        {
            _hostsFilePath = "/etc/hosts";
        }
    }

    public void SyncHostsFile(List<BlockedDomain> activeBlockedDomains, HashSet<string>? allowedTempDomains = null)
    {
        try
        {
            if (!File.Exists(_hostsFilePath))
            {
                _logger.LogWarning("Hosts file not found at: {Path}", _hostsFilePath);
                return;
            }

            string content = File.ReadAllText(_hostsFilePath);
            int beginIdx = content.IndexOf(BeginMarker, StringComparison.Ordinal);
            int endIdx = content.IndexOf(EndMarker, StringComparison.Ordinal);

            string prefix = content;
            string suffix = string.Empty;

            if (beginIdx >= 0 && endIdx >= 0 && endIdx > beginIdx)
            {
                prefix = content[..beginIdx].TrimEnd();
                suffix = content[(endIdx + EndMarker.Length)..].TrimStart();
            }

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                sb.AppendLine(prefix);
                sb.AppendLine();
            }

            sb.AppendLine(BeginMarker);
            sb.AppendLine("# Managed by Desktop Net Blocker. Do not edit manually.");

            foreach (var domain in activeBlockedDomains)
            {
                if (allowedTempDomains != null && allowedTempDomains.Contains(domain.Domain))
                    continue;

                string clean = domain.Domain.Trim();
                if (clean.StartsWith("*."))
                    clean = clean[2..];

                sb.AppendLine($"0.0.0.0 {clean}");
                sb.AppendLine($":: {clean}");
                if (!clean.StartsWith("www."))
                {
                    sb.AppendLine($"0.0.0.0 www.{clean}");
                    sb.AppendLine($":: www.{clean}");
                }
            }

            sb.AppendLine(EndMarker);

            if (!string.IsNullOrWhiteSpace(suffix))
            {
                sb.AppendLine();
                sb.Append(suffix);
            }

            File.WriteAllText(_hostsFilePath, sb.ToString());
            _logger.LogInformation("Hosts file synchronized with {Count} blocked entries.", activeBlockedDomains.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to update hosts file: {Message}", ex.Message);
        }
    }

    public void RemoveManagedEntries()
    {
        try
        {
            if (!File.Exists(_hostsFilePath))
                return;

            string content = File.ReadAllText(_hostsFilePath);
            int beginIdx = content.IndexOf(BeginMarker, StringComparison.Ordinal);
            int endIdx = content.IndexOf(EndMarker, StringComparison.Ordinal);

            if (beginIdx >= 0 && endIdx >= 0 && endIdx > beginIdx)
            {
                string prefix = content[..beginIdx].TrimEnd();
                string suffix = content[(endIdx + EndMarker.Length)..].TrimStart();

                string cleanContent = prefix + (string.IsNullOrWhiteSpace(suffix) ? "" : "\n" + suffix);
                File.WriteAllText(_hostsFilePath, cleanContent);
                _logger.LogInformation("Removed Desktop Net Blocker entries from hosts file.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to remove managed entries from hosts file: {Message}", ex.Message);
        }
    }
}

