using System;
using System.Collections.Generic;
using DesktopNetBlocker.Core.Models;

namespace DesktopNetBlocker.Core.Ipc;

public class IpcMessage
{
    public string MessageType { get; set; } = string.Empty;
    public string? PayloadJson { get; set; }
}

public class IpcResponse
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? PayloadJson { get; set; }

    public static IpcResponse Ok(string? payloadJson = null) => new() { Success = true, PayloadJson = payloadJson };
    public static IpcResponse Fail(string error) => new() { Success = false, ErrorMessage = error };
}

public class StatusDto
{
    public bool IsServiceRunning { get; set; } = true;
    public bool IsDnsFilteringActive { get; set; } = true;
    public bool IsFirewallEnforced { get; set; } = true;
    public int BlockedDomainsCount { get; set; }
    public AuthorizationRequest? ActiveRequest { get; set; }
    public List<TemporaryAccess> ActiveTemporaryAccesses { get; set; } = [];
    public DateTimeOffset ServerTimeUtc { get; set; } = DateTimeOffset.UtcNow;
}

public class AddDomainsDto
{
    public List<string> Domains { get; set; } = [];
    public MatchingMode MatchingMode { get; set; } = MatchingMode.Subdomain;
    public string Category { get; set; } = "General";
}

public class DomainActionDto
{
    public string DomainId { get; set; } = string.Empty;
}

public class RequestActionDto
{
    public string RequestId { get; set; } = string.Empty;
    public string? Reason { get; set; }
}

public class PolicyChangeDto
{
    public string Key { get; set; } = string.Empty;
    public string NewValue { get; set; } = string.Empty;
}

