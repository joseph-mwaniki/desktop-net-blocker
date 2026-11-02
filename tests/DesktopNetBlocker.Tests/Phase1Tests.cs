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

public class MockTimeProvider : ITimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan span)
    {
        UtcNow = UtcNow.Add(span);
    }
}

public class Phase1Tests : IDisposable
{
    private readonly string _testDbPath;
    private readonly BlockerDbContext _db;
    private readonly MockTimeProvider _time;
    private readonly AuthorizationStateMachine _machine;

    public Phase1Tests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"blocker_test_{Guid.NewGuid():N}.db");
        _db = new BlockerDbContext(_testDbPath);
        _time = new MockTimeProvider();
        _machine = new AuthorizationStateMachine(_db, _time);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_testDbPath))
        {
            try { File.Delete(_testDbPath); } catch { }
        }
    }

    [Fact]
    public void InitialBlocklist_MustBeEmpty()
    {
        var domains = _db.GetAllDomains();
        Assert.Empty(domains);

        var active = _db.GetActiveBlockedDomains();
        Assert.Empty(active);
    }

    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("https://www.google.com/search?q=test", "www.google.com")]
    [InlineData("http://sub.domain.org:8080/path/", "sub.domain.org")]
    [InlineData("*.netflix.com", "*.netflix.com")]
    [InlineData("TWITTER.COM", "twitter.com")]
    public void DomainValidator_NormalizesCorrectly(string input, string expected)
    {
        string normalized = DomainValidator.NormalizeDomain(input);
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void DomainValidator_BatchParsing_FiltersDuplicatesAndErrors()
    {
        string batch = "example.com\nhttps://reddit.com/r/all\ninvalid..domain\nexample.com\n*.tiktok.com";
        var (valid, errors) = DomainValidator.ParseBatch(batch);

        Assert.Equal(3, valid.Count);
        Assert.Contains("example.com", valid);
        Assert.Contains("reddit.com", valid);
        Assert.Contains("*.tiktok.com", valid);
        Assert.Single(errors);
    }

    [Fact]
    public void DomainMatcher_ExactAndSubdomainAndWildcard()
    {
        // Exact
        Assert.True(DomainMatcher.Matches("example.com", "example.com", MatchingMode.Exact));
        Assert.False(DomainMatcher.Matches("sub.example.com", "example.com", MatchingMode.Exact));

        // Subdomain
        Assert.True(DomainMatcher.Matches("example.com", "example.com", MatchingMode.Subdomain));
        Assert.True(DomainMatcher.Matches("sub.example.com", "example.com", MatchingMode.Subdomain));
        Assert.True(DomainMatcher.Matches("a.b.example.com", "example.com", MatchingMode.Subdomain));
        Assert.False(DomainMatcher.Matches("notexample.com", "example.com", MatchingMode.Subdomain));

        // Wildcard
        Assert.True(DomainMatcher.Matches("video-streaming.tv", "*streaming*", MatchingMode.Wildcard));
        Assert.False(DomainMatcher.Matches("games.tv", "*streaming*", MatchingMode.Wildcard));
    }

    [Fact]
    public void DomainAddition_ActivatesRestrictionImmediately()
    {
        var domain = new BlockedDomain
        {
            Domain = "distractingwebsite.com",
            MatchingMode = MatchingMode.Subdomain,
            Category = "Social Media"
        };

        _db.AddDomains([domain]);

        var active = _db.GetActiveBlockedDomains();
        Assert.Single(active);
        Assert.Equal("distractingwebsite.com", active[0].Domain);
        Assert.Equal(DomainStatus.Active, active[0].Status);
    }

    [Fact]
    public void RemovalWorkflow_FullSequence_RequiresThreeDaysInitialPlusConfirmationPlusThreeDaysFinal()
    {
        var domain = new BlockedDomain
        {
            Domain = "procrastination.com",
            MatchingMode = MatchingMode.Subdomain
        };
        _db.AddDomains([domain]);
        var added = _db.GetDomainByName("procrastination.com")!;

        // 1. Submit removal request
        var req = _machine.SubmitRemovalRequest(added.Id);
        Assert.Equal(RequestStatus.Pending, req.Status);
        Assert.Equal(_time.UtcNow.AddDays(3), req.InitialDeadline);

        // Domain must still be actively blocked
        Assert.Single(_db.GetActiveBlockedDomains());

        // Attempting premature confirmation throws
        Assert.Throws<InvalidOperationException>(() => _machine.ConfirmRequest(req.Id));

        // 2. Advance time 3 days: Initial wait elapses
        _time.Advance(TimeSpan.FromDays(3));
        _machine.EvaluateAllActiveRequests();

        var evalReq = _db.GetRequestById(req.Id)!;
        Assert.Equal(RequestStatus.Confirmation, evalReq.Status);
        Assert.NotNull(evalReq.ConfirmationDeadline);

        // Domain STILL blocked!
        Assert.Single(_db.GetActiveBlockedDomains());

        // 3. User explicitly confirms
        var confirmed = _machine.ConfirmRequest(req.Id);
        Assert.Equal(RequestStatus.FinalDelay, confirmed.Status);
        Assert.NotNull(confirmed.FinalDeadline);
        Assert.Equal(_time.UtcNow.AddDays(3), confirmed.FinalDeadline.Value);

        // Domain STILL blocked!
        Assert.Single(_db.GetActiveBlockedDomains());

        // 4. Advance time 2.9 days (almost there)
        _time.Advance(TimeSpan.FromDays(2.9));
        _machine.EvaluateAllActiveRequests();
        Assert.Single(_db.GetActiveBlockedDomains());

        // 5. Advance past final deadline: now authorized!
        _time.Advance(TimeSpan.FromHours(3));
        _machine.EvaluateAllActiveRequests();

        var finalReq = _db.GetRequestById(req.Id)!;
        Assert.Equal(RequestStatus.Authorized, finalReq.Status);

        // Now and only now domain is removed from active blocklist
        Assert.Empty(_db.GetActiveBlockedDomains());
        var storedDomain = _db.GetDomainById(added.Id)!;
        Assert.Equal(DomainStatus.Removed, storedDomain.Status);
    }

    [Fact]
    public void SingleRemovalRequestConstraint_BlocksConcurrentRemovalRequests()
    {
        var d1 = new BlockedDomain { Domain = "site1.com" };
        var d2 = new BlockedDomain { Domain = "site2.com" };
        _db.AddDomains([d1, d2]);

        var domain1 = _db.GetDomainByName("site1.com")!;
        var domain2 = _db.GetDomainByName("site2.com")!;

        _machine.SubmitRemovalRequest(domain1.Id);

        // Second removal request must fail
        Assert.Throws<InvalidOperationException>(() => _machine.SubmitRemovalRequest(domain2.Id));
    }

    [Fact]
    public void IgnoredConfirmation_ExpiresRequestAndKeepsDomainBlocked()
    {
        var domain = new BlockedDomain { Domain = "timewaster.net" };
        _db.AddDomains([domain]);
        var added = _db.GetDomainByName("timewaster.net")!;

        var req = _machine.SubmitRemovalRequest(added.Id);

        // Advance past initial wait (3 days)
        _time.Advance(TimeSpan.FromDays(3));
        _machine.EvaluateAllActiveRequests();

        // Advance past confirmation window (24 hours) without confirming
        _time.Advance(TimeSpan.FromHours(25));
        _machine.EvaluateAllActiveRequests();

        var expiredReq = _db.GetRequestById(req.Id)!;
        Assert.Equal(RequestStatus.Expired, expiredReq.Status);

        // Domain remains blocked!
        Assert.Single(_db.GetActiveBlockedDomains());
        var currentDomain = _db.GetDomainById(added.Id)!;
        Assert.Equal(DomainStatus.Active, currentDomain.Status);
    }

    [Fact]
    public void Cancellation_EscalatesCooldown_7d_14d_30d()
    {
        var domain = new BlockedDomain { Domain = "gaming.org" };
        _db.AddDomains([domain]);
        var added = _db.GetDomainByName("gaming.org")!;

        // First cancellation -> 7 days cooldown
        var req1 = _machine.SubmitRemovalRequest(added.Id);
        _time.Advance(TimeSpan.FromDays(1));
        _machine.CancelRequest(req1.Id);

        Assert.Single(_db.GetActiveBlockedDomains());
        var history1 = _db.GetLatestCancellationForDomain(added.Id)!;
        Assert.Equal(TimeSpan.FromDays(7), history1.ResultingCooldown);
        Assert.Equal(_time.UtcNow.AddDays(7), history1.NextAllowedRequestAt);

        // Cannot request during cooldown
        var (allowed, rem) = _machine.CanSubmitRequestForDomain(added.Id, RequestType.Removal);
        Assert.False(allowed);
        Assert.NotNull(rem);
        Assert.Throws<InvalidOperationException>(() => _machine.SubmitRemovalRequest(added.Id));

        // Advance 7.1 days
        _time.Advance(TimeSpan.FromDays(7.1));
        (allowed, _) = _machine.CanSubmitRequestForDomain(added.Id, RequestType.Removal);
        Assert.True(allowed);

        // Second cancellation -> 14 days cooldown
        var req2 = _machine.SubmitRemovalRequest(added.Id);
        _machine.CancelRequest(req2.Id);
        var history2 = _db.GetLatestCancellationForDomain(added.Id)!;
        Assert.Equal(TimeSpan.FromDays(14), history2.ResultingCooldown);

        // Advance 14.1 days
        _time.Advance(TimeSpan.FromDays(14.1));

        // Third cancellation -> 30 days cooldown
        var req3 = _machine.SubmitRemovalRequest(added.Id);
        _machine.CancelRequest(req3.Id);
        var history3 = _db.GetLatestCancellationForDomain(added.Id)!;
        Assert.Equal(TimeSpan.FromDays(30), history3.ResultingCooldown);
    }

    [Fact]
    public void TemporaryUnlock_GrantsAccessForTwoHoursThenRelocksAutomatically()
    {
        var domain = new BlockedDomain { Domain = "newsportal.com" };
        _db.AddDomains([domain]);
        var added = _db.GetDomainByName("newsportal.com")!;

        var req = _machine.SubmitTemporaryUnlockRequest(added.Id);

        // Advance 3 days initial
        _time.Advance(TimeSpan.FromDays(3));
        _machine.EvaluateAllActiveRequests();

        // Confirm
        _machine.ConfirmRequest(req.Id);

        // Advance 3 days final
        _time.Advance(TimeSpan.FromDays(3.1));
        _machine.EvaluateAllActiveRequests();

        // Status is Authorized
        var authReq = _db.GetRequestById(req.Id)!;
        Assert.Equal(RequestStatus.Authorized, authReq.Status);

        // Active temporary access created
        var activeTemp = _db.GetActiveTemporaryAccessList();
        Assert.Single(activeTemp);
        Assert.Equal(added.Id, activeTemp[0].DomainId);

        // Domain is suspended temporary unlock (not removed)
        var updatedDomain = _db.GetDomainById(added.Id)!;
        Assert.Equal(DomainStatus.SuspendedTemporaryUnlock, updatedDomain.Status);

        // Advance 2.1 hours -> temporary access must expire!
        _time.Advance(TimeSpan.FromHours(2.1));
        _machine.EvaluateActiveTemporaryAccesses();

        Assert.Empty(_db.GetActiveTemporaryAccessList());
        var relockedDomain = _db.GetDomainById(added.Id)!;
        Assert.Equal(DomainStatus.Active, relockedDomain.Status);
        Assert.Single(_db.GetActiveBlockedDomains());
    }
}

