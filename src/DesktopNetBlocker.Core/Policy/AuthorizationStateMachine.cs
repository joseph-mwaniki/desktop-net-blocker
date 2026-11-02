using System;
using System.Text.Json;
using DesktopNetBlocker.Core.Common;
using DesktopNetBlocker.Core.Data;
using DesktopNetBlocker.Core.Models;

namespace DesktopNetBlocker.Core.Policy;

public interface IAuthorizationStateMachine
{
    AuthorizationRequest SubmitRemovalRequest(string domainId);
    AuthorizationRequest SubmitTemporaryUnlockRequest(string domainId);
    AuthorizationRequest SubmitPolicyChangeRequest(string key, string pendingValue);
    AuthorizationRequest ConfirmRequest(string requestId);
    AuthorizationRequest CancelRequest(string requestId, string? reason = null);
    void EvaluateAllActiveRequests();
    void EvaluateActiveTemporaryAccesses();
    (bool IsAllowed, TimeSpan? RemainingCooldown) CanSubmitRequestForDomain(string domainId, RequestType type);
    TimeSpan GetCooldownForCancellationCount(int count);
}

public class AuthorizationStateMachine : IAuthorizationStateMachine
{
    private readonly BlockerDbContext _db;
    private readonly ITimeProvider _timeProvider;
    private readonly object _syncLock = new();

    public AuthorizationStateMachine(BlockerDbContext db, ITimeProvider? timeProvider = null)
    {
        _db = db;
        _timeProvider = timeProvider ?? new DefaultTimeProvider();
    }

    public TimeSpan GetInitialWaitingPeriod()
    {
        string? val = _db.GetPolicySetting("InitialWaitingPeriodDays");
        double days = double.TryParse(val, out double d) ? d : 3.0;
        return TimeSpan.FromDays(days);
    }

    public TimeSpan GetConfirmationDelay()
    {
        string? val = _db.GetPolicySetting("ConfirmationDelayDays");
        double days = double.TryParse(val, out double d) ? d : 3.0;
        return TimeSpan.FromDays(days);
    }

    public TimeSpan GetConfirmationWindow()
    {
        string? val = _db.GetPolicySetting("ConfirmationWindowHours");
        double hours = double.TryParse(val, out double h) ? h : 24.0;
        return TimeSpan.FromHours(hours);
    }

    public TimeSpan GetTemporaryAccessDuration()
    {
        string? val = _db.GetPolicySetting("TemporaryAccessDurationHours");
        double hours = double.TryParse(val, out double h) ? h : 2.0;
        return TimeSpan.FromHours(hours);
    }

    public TimeSpan GetCooldownForCancellationCount(int count)
    {
        if (count <= 1)
        {
            string? val = _db.GetPolicySetting("FirstCancellationCooldownDays");
            double days = double.TryParse(val, out double d) ? d : 7.0;
            return TimeSpan.FromDays(days);
        }
        if (count == 2)
        {
            string? val = _db.GetPolicySetting("SecondCancellationCooldownDays");
            double days = double.TryParse(val, out double d) ? d : 14.0;
            return TimeSpan.FromDays(days);
        }
        else
        {
            string? val = _db.GetPolicySetting("ThirdCancellationCooldownDays");
            double days = double.TryParse(val, out double d) ? d : 30.0;
            return TimeSpan.FromDays(days);
        }
    }

    public (bool IsAllowed, TimeSpan? RemainingCooldown) CanSubmitRequestForDomain(string domainId, RequestType type)
    {
        lock (_syncLock)
        {
            var now = _timeProvider.UtcNow;

            // Rule: Only one pending domain removal request at a time
            if (type == RequestType.Removal)
            {
                var existingActive = _db.GetActiveRequest();
                if (existingActive != null && existingActive.RequestType == RequestType.Removal)
                {
                    return (false, null);
                }
            }

            // Check if domain is under cancellation cooldown
            var latestCancellation = _db.GetLatestCancellationForDomain(domainId);
            if (latestCancellation != null && latestCancellation.NextAllowedRequestAt > now)
            {
                return (false, latestCancellation.NextAllowedRequestAt - now);
            }

            return (true, null);
        }
    }

    public AuthorizationRequest SubmitRemovalRequest(string domainId)
    {
        lock (_syncLock)
        {
            var domain = _db.GetDomainById(domainId)
                ?? throw new InvalidOperationException($"Domain with ID '{domainId}' not found.");

            if (domain.Status == DomainStatus.Removed)
                throw new InvalidOperationException($"Domain '{domain.Domain}' is already removed.");

            // Check single removal request constraint
            var active = _db.GetActiveRequest();
            if (active != null && active.RequestType == RequestType.Removal)
            {
                throw new InvalidOperationException("Another domain removal request is already pending. Only one removal request is permitted at a time.");
            }

            // Check cooldown
            var (isAllowed, remainingCooldown) = CanSubmitRequestForDomain(domainId, RequestType.Removal);
            if (!isAllowed && remainingCooldown.HasValue)
            {
                throw new InvalidOperationException($"Domain '{domain.Domain}' is currently in cancellation cooldown. Next request allowed in {remainingCooldown.Value.Days}d {remainingCooldown.Value.Hours}h.");
            }

            var now = _timeProvider.UtcNow;
            int priorCancellations = _db.GetCancellationCountForDomain(domainId);

            var req = new AuthorizationRequest
            {
                Id = Guid.NewGuid().ToString(),
                RequestType = RequestType.Removal,
                DomainId = domain.Id,
                TargetDomain = domain.Domain,
                CreatedAt = now,
                InitialDeadline = now.Add(GetInitialWaitingPeriod()),
                Status = RequestStatus.Pending,
                CancellationCount = priorCancellations
            };

            _db.SaveAuthorizationRequest(req);
            _db.RecordEvent("RemovalRequested", domain.Domain, $"Submitted removal request {req.Id}. Initial wait until {req.InitialDeadline:yyyy-MM-dd HH:mm:ss} UTC.");

            return req;
        }
    }

    public AuthorizationRequest SubmitTemporaryUnlockRequest(string domainId)
    {
        lock (_syncLock)
        {
            var domain = _db.GetDomainById(domainId)
                ?? throw new InvalidOperationException($"Domain with ID '{domainId}' not found.");

            if (domain.Status != DomainStatus.Active)
                throw new InvalidOperationException($"Domain '{domain.Domain}' is not actively blocked.");

            var (isAllowed, remainingCooldown) = CanSubmitRequestForDomain(domainId, RequestType.TemporaryUnlock);
            if (!isAllowed && remainingCooldown.HasValue)
            {
                throw new InvalidOperationException($"Domain '{domain.Domain}' is in cancellation cooldown. Next request allowed in {remainingCooldown.Value.Days}d {remainingCooldown.Value.Hours}h.");
            }

            var now = _timeProvider.UtcNow;
            int priorCancellations = _db.GetCancellationCountForDomain(domainId);

            var req = new AuthorizationRequest
            {
                Id = Guid.NewGuid().ToString(),
                RequestType = RequestType.TemporaryUnlock,
                DomainId = domain.Id,
                TargetDomain = domain.Domain,
                CreatedAt = now,
                InitialDeadline = now.Add(GetInitialWaitingPeriod()),
                Status = RequestStatus.Pending,
                CancellationCount = priorCancellations
            };

            _db.SaveAuthorizationRequest(req);
            _db.RecordEvent("TemporaryUnlockRequested", domain.Domain, $"Submitted temporary unlock request {req.Id}. Initial wait until {req.InitialDeadline:yyyy-MM-dd HH:mm:ss} UTC.");

            return req;
        }
    }

    public AuthorizationRequest SubmitPolicyChangeRequest(string key, string pendingValue)
    {
        lock (_syncLock)
        {
            var now = _timeProvider.UtcNow;

            var req = new AuthorizationRequest
            {
                Id = Guid.NewGuid().ToString(),
                RequestType = RequestType.PolicyModification,
                DomainId = null,
                TargetDomain = $"Policy: {key}",
                CreatedAt = now,
                InitialDeadline = now.Add(GetInitialWaitingPeriod()),
                Status = RequestStatus.Pending,
                CancellationCount = 0,
                PayloadJson = JsonSerializer.Serialize(new { Key = key, NewValue = pendingValue })
            };

            _db.SaveAuthorizationRequest(req);
            _db.RecordEvent("PolicyChangeRequested", null, $"Requested change of setting '{key}' to '{pendingValue}'. Initial wait until {req.InitialDeadline:yyyy-MM-dd HH:mm:ss} UTC.");

            return req;
        }
    }

    public AuthorizationRequest ConfirmRequest(string requestId)
    {
        lock (_syncLock)
        {
            var req = _db.GetRequestById(requestId)
                ?? throw new InvalidOperationException($"Request with ID '{requestId}' not found.");

            var now = _timeProvider.UtcNow;

            if (req.Status != RequestStatus.Confirmation)
            {
                throw new InvalidOperationException($"Request is in status '{req.Status}', but confirmation requires 'Confirmation' status.");
            }

            if (req.ConfirmationDeadline.HasValue && now > req.ConfirmationDeadline.Value)
            {
                // Confirmation deadline expired
                req.Status = RequestStatus.Expired;
                _db.SaveAuthorizationRequest(req);
                _db.RecordEvent("RequestExpired", req.TargetDomain, $"Request {req.Id} expired because confirmation was not provided in time.");
                throw new InvalidOperationException("The confirmation window has expired. The domain remains blocked.");
            }

            req.ConfirmedAt = now;
            req.Status = RequestStatus.FinalDelay;
            req.FinalDeadline = now.Add(GetConfirmationDelay());

            _db.SaveAuthorizationRequest(req);
            _db.RecordEvent("RequestConfirmed", req.TargetDomain, $"Request {req.Id} confirmed. Final delay active until {req.FinalDeadline:yyyy-MM-dd HH:mm:ss} UTC.");

            return req;
        }
    }

    public AuthorizationRequest CancelRequest(string requestId, string? reason = null)
    {
        lock (_syncLock)
        {
            var req = _db.GetRequestById(requestId)
                ?? throw new InvalidOperationException($"Request with ID '{requestId}' not found.");

            if (req.Status is RequestStatus.Authorized or RequestStatus.Cancelled or RequestStatus.Expired)
            {
                throw new InvalidOperationException($"Cannot cancel a request with status '{req.Status}'.");
            }

            var now = _timeProvider.UtcNow;
            req.Status = RequestStatus.Cancelled;
            _db.SaveAuthorizationRequest(req);

            int newCount = 1;
            if (!string.IsNullOrEmpty(req.DomainId))
            {
                newCount = _db.GetCancellationCountForDomain(req.DomainId) + 1;
            }

            TimeSpan cooldown = GetCooldownForCancellationCount(newCount);
            DateTimeOffset nextAllowed = now.Add(cooldown);

            var history = new CancellationHistory
            {
                Id = Guid.NewGuid().ToString(),
                RequestId = req.Id,
                DomainId = req.DomainId,
                TargetDomain = req.TargetDomain,
                CancelledAt = now,
                CancellationCount = newCount,
                ResultingCooldown = cooldown,
                NextAllowedRequestAt = nextAllowed,
                Reason = reason ?? "User requested cancellation"
            };

            _db.AddCancellation(history);
            _db.RecordEvent("RequestCancelled", req.TargetDomain, $"Request {req.Id} cancelled by user. Cooldown of {cooldown.TotalDays} days applied until {nextAllowed:yyyy-MM-dd HH:mm:ss} UTC.");

            return req;
        }
    }

    public void EvaluateAllActiveRequests()
    {
        lock (_syncLock)
        {
            var active = _db.GetActiveRequest();
            if (active == null)
                return;

            var now = _timeProvider.UtcNow;

            switch (active.Status)
            {
                case RequestStatus.Pending:
                    if (now >= active.InitialDeadline)
                    {
                        active.Status = RequestStatus.Confirmation;
                        active.ConfirmationDeadline = now.Add(GetConfirmationWindow());
                        _db.SaveAuthorizationRequest(active);
                        _db.RecordEvent("ConfirmationReady", active.TargetDomain, $"Initial waiting period elapsed for request {active.Id}. Confirmation required before {active.ConfirmationDeadline:yyyy-MM-dd HH:mm:ss} UTC.");
                    }
                    break;

                case RequestStatus.Confirmation:
                    if (active.ConfirmationDeadline.HasValue && now >= active.ConfirmationDeadline.Value)
                    {
                        active.Status = RequestStatus.Expired;
                        _db.SaveAuthorizationRequest(active);
                        _db.RecordEvent("RequestExpired", active.TargetDomain, $"Confirmation ignored for request {active.Id}. Request expired; domain remains blocked.");
                    }
                    break;

                case RequestStatus.FinalDelay:
                    if (active.FinalDeadline.HasValue && now >= active.FinalDeadline.Value)
                    {
                        // Authorized! Execute action
                        active.Status = RequestStatus.Authorized;
                        _db.SaveAuthorizationRequest(active);
                        ExecuteAuthorizedAction(active, now);
                    }
                    break;
            }
        }
    }

    private void ExecuteAuthorizedAction(AuthorizationRequest req, DateTimeOffset now)
    {
        switch (req.RequestType)
        {
            case RequestType.Removal:
                if (!string.IsNullOrEmpty(req.DomainId))
                {
                    _db.UpdateDomainStatus(req.DomainId, DomainStatus.Removed);
                    _db.RecordEvent("DomainRemoved", req.TargetDomain, $"Authorized removal completed for request {req.Id}. Restriction lifted.");
                }
                break;

            case RequestType.TemporaryUnlock:
                if (!string.IsNullOrEmpty(req.DomainId))
                {
                    _db.UpdateDomainStatus(req.DomainId, DomainStatus.SuspendedTemporaryUnlock);
                    var duration = GetTemporaryAccessDuration();
                    var temp = new TemporaryAccess
                    {
                        Id = Guid.NewGuid().ToString(),
                        DomainId = req.DomainId,
                        TargetDomain = req.TargetDomain,
                        GrantedAt = now,
                        ExpiresAt = now.Add(duration),
                        Status = TemporaryAccessStatus.Active
                    };
                    _db.AddTemporaryAccess(temp);
                    _db.RecordEvent("TemporaryAccessGranted", req.TargetDomain, $"Temporary access granted for {duration.TotalHours} hours until {temp.ExpiresAt:yyyy-MM-dd HH:mm:ss} UTC.");
                }
                break;

            case RequestType.PolicyModification:
                if (!string.IsNullOrEmpty(req.PayloadJson))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(req.PayloadJson);
                        if (doc.RootElement.TryGetProperty("Key", out var kProp) &&
                            doc.RootElement.TryGetProperty("NewValue", out var vProp))
                        {
                            string key = kProp.GetString()!;
                            string val = vProp.GetString()!;
                            _db.SetPolicySetting(key, val);
                            _db.RecordEvent("PolicySettingApplied", null, $"Policy setting '{key}' updated to '{val}' after authorized waiting period.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _db.RecordEvent("PolicySettingFailed", null, $"Failed applying policy setting: {ex.Message}");
                    }
                }
                break;
        }
    }

    public void EvaluateActiveTemporaryAccesses()
    {
        lock (_syncLock)
        {
            var activeList = _db.GetActiveTemporaryAccessList();
            var now = _timeProvider.UtcNow;

            foreach (var access in activeList)
            {
                if (now >= access.ExpiresAt)
                {
                    access.Status = TemporaryAccessStatus.Expired;
                    _db.UpdateTemporaryAccessStatus(access.Id, TemporaryAccessStatus.Expired);
                    _db.UpdateDomainStatus(access.DomainId, DomainStatus.Active);
                    _db.RecordEvent("TemporaryAccessExpired", access.TargetDomain, $"Temporary access expired for domain '{access.TargetDomain}'. Domain is automatically blocked again.");
                }
            }
        }
    }
}

