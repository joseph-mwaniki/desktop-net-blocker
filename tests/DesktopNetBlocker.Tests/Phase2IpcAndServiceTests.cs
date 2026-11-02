using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using DesktopNetBlocker.Core.Common;
using DesktopNetBlocker.Core.Data;
using DesktopNetBlocker.Core.Ipc;
using DesktopNetBlocker.Core.Models;
using DesktopNetBlocker.Core.Policy;
using Xunit;

namespace DesktopNetBlocker.Tests;

public class Phase2IpcAndServiceTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly BlockerDbContext _db;
    private readonly MockTimeProvider _time;
    private readonly AuthorizationStateMachine _machine;
    private readonly string _pipeName;
    private NamedPipeIpcServer? _server;

    public Phase2IpcAndServiceTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"blocker_ipc_test_{Guid.NewGuid():N}.db");
        _db = new BlockerDbContext(_testDbPath);
        _time = new MockTimeProvider();
        _machine = new AuthorizationStateMachine(_db, _time);
        _pipeName = $"TestPipe_{Guid.NewGuid():N}";
    }

    public void Dispose()
    {
        _server?.Stop();
        _server?.Dispose();
        _db.Dispose();
        if (File.Exists(_testDbPath))
        {
            try { File.Delete(_testDbPath); } catch { }
        }
    }

    [Fact]
    public async Task Ipc_Ping_ReturnsPong()
    {
        _server = new NamedPipeIpcServer(_pipeName, msg =>
        {
            if (msg.MessageType == "Ping")
                return Task.FromResult(IpcResponse.Ok("Pong"));
            return Task.FromResult(IpcResponse.Fail("Unknown"));
        });
        _server.Start();

        var client = new NamedPipeIpcClient(_pipeName);
        var resp = await client.SendAsync("Ping");

        Assert.True(resp.Success);
        Assert.Equal("Pong", resp.PayloadJson);
    }

    [Fact]
    public async Task Ipc_AddDomains_PersistsAndActivatesImmediately()
    {
        _server = new NamedPipeIpcServer(_pipeName, msg =>
        {
            if (msg.MessageType == "AddDomains")
            {
                var dto = JsonSerializer.Deserialize<AddDomainsDto>(msg.PayloadJson!);
                var list = new List<BlockedDomain>();
                foreach (var d in dto!.Domains)
                {
                    list.Add(new BlockedDomain { Domain = d, MatchingMode = dto.MatchingMode, Category = dto.Category });
                }
                _db.AddDomains(list);
                return Task.FromResult(IpcResponse.Ok("Added"));
            }
            return Task.FromResult(IpcResponse.Fail("Unknown"));
        });
        _server.Start();

        var client = new NamedPipeIpcClient(_pipeName);
        var payload = new AddDomainsDto
        {
            Domains = ["distraction1.com", "distraction2.org"],
            MatchingMode = MatchingMode.Subdomain,
            Category = "Social"
        };

        var resp = await client.SendAsync("AddDomains", payload);
        Assert.True(resp.Success);

        var active = _db.GetActiveBlockedDomains();
        Assert.Equal(2, active.Count);
    }

    [Fact]
    public void RestartRecovery_RestoresActiveTimersAndExecutesElapsedTransitions()
    {
        // 1. Setup domain and pending removal
        var d = new BlockedDomain { Domain = "addiction.com" };
        _db.AddDomains([d]);
        var domain = _db.GetDomainByName("addiction.com")!;
        var req = _machine.SubmitRemovalRequest(domain.Id);

        // Simulate service shutdown, machine reboot after 4 days
        _time.Advance(TimeSpan.FromDays(4));

        // Create fresh service/state-machine instance on startup
        var recoveredMachine = new AuthorizationStateMachine(_db, _time);
        recoveredMachine.EvaluateAllActiveRequests();

        var recoveredReq = _db.GetRequestById(req.Id)!;
        Assert.Equal(RequestStatus.Confirmation, recoveredReq.Status);
        Assert.NotNull(recoveredReq.ConfirmationDeadline);

        // Domain still actively blocked
        Assert.Single(_db.GetActiveBlockedDomains());
    }
}

