using System;

namespace DesktopNetBlocker.Core.Models;

public class BlockedDomain
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Domain { get; set; } = string.Empty;
    public MatchingMode MatchingMode { get; set; } = MatchingMode.Subdomain;
    public string Category { get; set; } = "General";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DomainStatus Status { get; set; } = DomainStatus.Active;
    public int PolicyVersion { get; set; } = 1;
}

public class AuthorizationRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public RequestType RequestType { get; set; } = RequestType.Removal;
    public string? DomainId { get; set; }
    public string TargetDomain { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset InitialDeadline { get; set; }
    public DateTimeOffset? ConfirmationDeadline { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public DateTimeOffset? FinalDeadline { get; set; }
    public RequestStatus Status { get; set; } = RequestStatus.Pending;
    public int CancellationCount { get; set; } = 0;
    public string? PayloadJson { get; set; }
}

public class CancellationHistory
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string RequestId { get; set; } = string.Empty;
    public string? DomainId { get; set; }
    public string TargetDomain { get; set; } = string.Empty;
    public DateTimeOffset CancelledAt { get; set; } = DateTimeOffset.UtcNow;
    public int CancellationCount { get; set; } = 1;
    public TimeSpan ResultingCooldown { get; set; }
    public DateTimeOffset NextAllowedRequestAt { get; set; }
    public string? Reason { get; set; }
}

public class TemporaryAccess
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string DomainId { get; set; } = string.Empty;
    public string TargetDomain { get; set; } = string.Empty;
    public DateTimeOffset GrantedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public TemporaryAccessStatus Status { get; set; } = TemporaryAccessStatus.Active;
}

public class PolicySetting
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string? PendingValue { get; set; }
    public DateTimeOffset? EffectiveAt { get; set; }
    public string Status { get; set; } = "Active";
}

public class RestrictionEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string EventType { get; set; } = string.Empty;
    public string? Domain { get; set; }
    public string? Details { get; set; }
}

