using System;
using System.Collections.Generic;
using System.IO;
using DesktopNetBlocker.Core.Common;
using DesktopNetBlocker.Core.Data;
using DesktopNetBlocker.Core.Models;
using DesktopNetBlocker.Core.Network;
using DesktopNetBlocker.Core.Policy;
using Xunit;

namespace DesktopNetBlocker.Tests;

public class AcceptanceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly BlockerDbContext _db;
    private readonly MockTimeProvider _time;
    private readonly AuthorizationStateMachine _stateMachine;

    public AcceptanceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"blocker_acceptance_{Guid.NewGuid():N}.db");
        _db = new BlockerDbContext(_dbPath);
        _time = new MockTimeProvider();
        _stateMachine = new AuthorizationStateMachine(_db, _time);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { }
        }
    }

    [Fact]
    public void Criterion1_InitialBlocklist_IsEmpty()
    {
        Assert.Empty(_db.GetAllDomains());
        Assert.Empty(_db.GetActiveBlockedDomains());
    }

    [Fact]
    public void Criterion2_And_3_UserCanFreelyAddDomains_ActivatesSystemWideEnforcement()
    {
        var domain = new BlockedDomain
        {
            Domain = "youtube.com",
            MatchingMode = MatchingMode.Subdomain,
            Category = "Video"
        };
        _db.AddDomains([domain]);

        var active = _db.GetActiveBlockedDomains();
        Assert.Single(active);
        Assert.Equal("youtube.com", active[0].Domain);
        Assert.Equal(DomainStatus.Active, active[0].Status);

        // Subdomain matching works immediately
        Assert.True(DomainMatcher.Matches("m.youtube.com", "youtube.com", MatchingMode.Subdomain));
        Assert.True(DomainMatcher.Matches("youtube.com", "youtube.com", MatchingMode.Subdomain));
    }

    [Fact]
    public void Criterion4_NoImmediateRemoval_RequiresDelayedWorkflow()
    {
        var domain = new BlockedDomain { Domain = "instagram.com" };
        _db.AddDomains([domain]);
        var added = _db.GetDomainByName("instagram.com")!;

        // No delete API exists in BlockerDbContext.
        // The only path is submitting a removal request.
        var req = _stateMachine.SubmitRemovalRequest(added.Id);
        Assert.Equal(RequestStatus.Pending, req.Status);

        // Domain remains strictly blocked!
        Assert.Single(_db.GetActiveBlockedDomains());
        var check = _db.GetDomainById(added.Id)!;
        Assert.Equal(DomainStatus.Active, check.Status);
    }

    [Fact]
    public void Criterion5_6_7_ThreeDayInitial_ConfirmationRequired_ThreeDayFinal()
    {
        var domain = new BlockedDomain { Domain = "tiktok.com" };
        _db.AddDomains([domain]);
        var added = _db.GetDomainByName("tiktok.com")!;

        var req = _stateMachine.SubmitRemovalRequest(added.Id);

        // Cannot confirm before initial delay
        Assert.Throws<InvalidOperationException>(() => _stateMachine.ConfirmRequest(req.Id));

        // Advance 3 days: Initial delay finishes -> Confirmation state
        _time.Advance(TimeSpan.FromDays(3));
        _stateMachine.EvaluateAllActiveRequests();

        var stage2 = _db.GetRequestById(req.Id)!;
        Assert.Equal(RequestStatus.Confirmation, stage2.Status);
        Assert.Single(_db.GetActiveBlockedDomains()); // still blocked

        // Explicit confirmation required
        var confirmed = _stateMachine.ConfirmRequest(req.Id);
        Assert.Equal(RequestStatus.FinalDelay, confirmed.Status);
        Assert.Equal(_time.UtcNow.AddDays(3), confirmed.FinalDeadline);
        Assert.Single(_db.GetActiveBlockedDomains()); // still blocked

        // Final delay elapses
        _time.Advance(TimeSpan.FromDays(3.01));
        _stateMachine.EvaluateAllActiveRequests();

        var stage3 = _db.GetRequestById(req.Id)!;
        Assert.Equal(RequestStatus.Authorized, stage3.Status);

        // Only now is domain removed
        Assert.Empty(_db.GetActiveBlockedDomains());
        var removed = _db.GetDomainById(added.Id)!;
        Assert.Equal(DomainStatus.Removed, removed.Status);
    }

    [Fact]
    public void Criterion8_IgnoringConfirmation_AutomaticallyExpires_KeepsDomainBlocked()
    {
        var domain = new BlockedDomain { Domain = "reddit.com" };
        _db.AddDomains([domain]);
        var added = _db.GetDomainByName("reddit.com")!;

        var req = _stateMachine.SubmitRemovalRequest(added.Id);

        // 3 days pass
        _time.Advance(TimeSpan.FromDays(3));
        _stateMachine.EvaluateAllActiveRequests();

        // Confirmation window (24h) passes without confirmation
        _time.Advance(TimeSpan.FromHours(25));
        _stateMachine.EvaluateAllActiveRequests();

        var expired = _db.GetRequestById(req.Id)!;
        Assert.Equal(RequestStatus.Expired, expired.Status);

        // Domain remains blocked!
        Assert.Single(_db.GetActiveBlockedDomains());
        var current = _db.GetDomainById(added.Id)!;
        Assert.Equal(DomainStatus.Active, current.Status);
    }

    [Fact]
    public void Criterion9_10_11_Cancellation_Immediate_AppliesEscalatingCooldown()
    {
        var domain = new BlockedDomain { Domain = "facebook.com" };
        _db.AddDomains([domain]);
        var added = _db.GetDomainByName("facebook.com")!;

        var req = _stateMachine.SubmitRemovalRequest(added.Id);

        // Cancel during pending state
        _time.Advance(TimeSpan.FromHours(12));
        var cancelled = _stateMachine.CancelRequest(req.Id, "Impulse passed");

        Assert.Equal(RequestStatus.Cancelled, cancelled.Status);
        Assert.Single(_db.GetActiveBlockedDomains());

        // 1st cooldown = 7 days
        var (allowed, remaining) = _stateMachine.CanSubmitRequestForDomain(added.Id, RequestType.Removal);
        Assert.False(allowed);
        Assert.NotNull(remaining);
        Assert.True(remaining.Value.TotalDays > 6.9);
    }

    [Fact]
    public void Criterion12_OnlyOneRemovalRequestCanBePendingAtATime()
    {
        var d1 = new BlockedDomain { Domain = "siteA.com" };
        var d2 = new BlockedDomain { Domain = "siteB.com" };
        _db.AddDomains([d1, d2]);

        var id1 = _db.GetDomainByName("siteA.com")!.Id;
        var id2 = _db.GetDomainByName("siteB.com")!.Id;

        _stateMachine.SubmitRemovalRequest(id1);

        // Attempting to submit a second removal request fails
        var ex = Assert.Throws<InvalidOperationException>(() => _stateMachine.SubmitRemovalRequest(id2));
        Assert.Contains("Only one removal request is permitted at a time", ex.Message);
    }

    [Fact]
    public void Criterion13_RestartingWindows_DoesNotResetTimers()
    {
        var domain = new BlockedDomain { Domain = "distraction.com" };
        _db.AddDomains([domain]);
        var added = _db.GetDomainByName("distraction.com")!;

        var req = _stateMachine.SubmitRemovalRequest(added.Id);
        DateTimeOffset recordedDeadline = req.InitialDeadline;

        // Simulate reboot: 2 days have passed in real world
        _time.Advance(TimeSpan.FromDays(2));

        // Reconnect to DB in fresh service instance
        var rebootedMachine = new AuthorizationStateMachine(_db, _time);
        rebootedMachine.EvaluateAllActiveRequests();

        var recoveredReq = _db.GetRequestById(req.Id)!;
        Assert.Equal(RequestStatus.Pending, recoveredReq.Status);
        Assert.Equal(recordedDeadline, recoveredReq.InitialDeadline); // Deadline was NOT reset!

        // Remaining time is exactly 1 day
        var remaining = recoveredReq.InitialDeadline - _time.UtcNow;
        Assert.Equal(TimeSpan.FromDays(1), remaining);
    }

    [Fact]
    public void Criterion16_TemporaryUnlocks_AutomaticallyExpireAfterTwoHours()
    {
        var domain = new BlockedDomain { Domain = "temporarysite.com" };
        _db.AddDomains([domain]);
        var added = _db.GetDomainByName("temporarysite.com")!;

        var req = _stateMachine.SubmitTemporaryUnlockRequest(added.Id);

        // Wait initial 3 days + confirm + wait final 3 days
        _time.Advance(TimeSpan.FromDays(3));
        _stateMachine.EvaluateAllActiveRequests();
        _stateMachine.ConfirmRequest(req.Id);
        _time.Advance(TimeSpan.FromDays(3.01));
        _stateMachine.EvaluateAllActiveRequests();

        // Authorized: Temporary Access granted for 2 hours
        var activeAccess = _db.GetActiveTemporaryAccessList();
        Assert.Single(activeAccess);
        Assert.Equal(added.Id, activeAccess[0].DomainId);

        // Advance 2.5 hours -> access expires, re-locks automatically!
        _time.Advance(TimeSpan.FromHours(2.5));
        _stateMachine.EvaluateActiveTemporaryAccesses();

        Assert.Empty(_db.GetActiveTemporaryAccessList());
        var relocked = _db.GetDomainById(added.Id)!;
        Assert.Equal(DomainStatus.Active, relocked.Status);
        Assert.Single(_db.GetActiveBlockedDomains());
    }
}
