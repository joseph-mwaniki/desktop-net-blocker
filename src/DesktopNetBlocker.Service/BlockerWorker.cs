using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DesktopNetBlocker.Core.Data;
using DesktopNetBlocker.Core.Ipc;
using DesktopNetBlocker.Core.Models;
using DesktopNetBlocker.Core.Network;
using DesktopNetBlocker.Core.Policy;
using DesktopNetBlocker.Service.Network;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DesktopNetBlocker.Service;

public class BlockerWorker : BackgroundService
{
    private readonly ILogger<BlockerWorker> _logger;
    private readonly BlockerDbContext _db;
    private readonly IAuthorizationStateMachine _stateMachine;
    private readonly NetworkEnforcementCoordinator _coordinator;
    private NamedPipeIpcServer? _ipcServer;

    public BlockerWorker(
        ILogger<BlockerWorker> logger,
        BlockerDbContext db,
        IAuthorizationStateMachine stateMachine,
        NetworkEnforcementCoordinator coordinator)
    {
        _logger = logger;
        _db = db;
        _stateMachine = stateMachine;
        _coordinator = coordinator;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Desktop Net Blocker Background Service starting up...");

        // Perform restart recovery
        RecoverStateOnStartup();

        // Start multi-layer network enforcement (DNS proxy, Hosts sync, Firewall rules, Adapter DNS)
        _coordinator.StartEnforcement();

        // Start Named Pipe IPC Server
        _ipcServer = new NamedPipeIpcServer(IpcConstants.PipeName, HandleIpcMessageAsync);
        _ipcServer.Start();
        _logger.LogInformation("Named Pipe IPC Server listening on '{PipeName}'", IpcConstants.PipeName);

        _db.RecordEvent("ServiceStarted", null, "Desktop Net Blocker Background Service started successfully.");

        return base.StartAsync(cancellationToken);
    }

    private void RecoverStateOnStartup()
    {
        try
        {
            _logger.LogInformation("Evaluating persistent state and recovery after restart...");
            _stateMachine.EvaluateAllActiveRequests();
            _stateMachine.EvaluateActiveTemporaryAccesses();

            var active = _db.GetActiveRequest();
            if (active != null)
            {
                _logger.LogInformation("Recovered active request {RequestId} ({Type}) in state {Status} for domain '{Domain}'",
                    active.Id, active.RequestType, active.Status, active.TargetDomain);
            }

            int count = _db.GetActiveBlockedDomains().Count;
            _logger.LogInformation("Restart recovery complete. {Count} domains actively blocked.", count);
            _db.RecordEvent("ServiceRecovered", null, $"Recovered active restrictions after system start. {count} active blocked domains.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during startup restart recovery: {Message}", ex.Message);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BlockerWorker main loop running.");
        int tickCounter = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Run state machine evaluations every second
                _stateMachine.EvaluateAllActiveRequests();
                _stateMachine.EvaluateActiveTemporaryAccesses();

                tickCounter++;
                if (tickCounter % 5 == 0)
                {
                    // Re-sync filtering rules every 5 seconds
                    _coordinator.SyncRules();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during periodic state evaluation: {Message}", ex.Message);
            }

            await Task.Delay(1000, stoppingToken);
        }

        _logger.LogInformation("BlockerWorker stopping.");
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Desktop Net Blocker Background Service shutting down...");
        _ipcServer?.Stop();
        _ipcServer?.Dispose();
        _coordinator.StopEnforcement();
        _db.RecordEvent("ServiceStopped", null, "Desktop Net Blocker Background Service stopped.");
        return base.StopAsync(cancellationToken);
    }

    private Task<IpcResponse> HandleIpcMessageAsync(IpcMessage msg)
    {
        try
        {
            switch (msg.MessageType)
            {
                case "Ping":
                    return Task.FromResult(IpcResponse.Ok("Pong"));

                case "GetStatus":
                {
                    int blockedCount = _db.GetActiveBlockedDomains().Count;
                    var activeRequest = _db.GetActiveRequest();
                    var tempAccesses = _db.GetActiveTemporaryAccessList();

                    var status = new StatusDto
                    {
                        IsServiceRunning = true,
                        IsDnsFilteringActive = true,
                        IsFirewallEnforced = true,
                        BlockedDomainsCount = blockedCount,
                        ActiveRequest = activeRequest,
                        ActiveTemporaryAccesses = tempAccesses,
                        ServerTimeUtc = DateTimeOffset.UtcNow
                    };
                    return Task.FromResult(IpcResponse.Ok(JsonSerializer.Serialize(status)));
                }

                case "GetDomains":
                {
                    var domains = _db.GetAllDomains();
                    return Task.FromResult(IpcResponse.Ok(JsonSerializer.Serialize(domains)));
                }

                case "AddDomains":
                {
                    if (string.IsNullOrEmpty(msg.PayloadJson))
                        return Task.FromResult(IpcResponse.Fail("Missing payload for AddDomains"));

                    var dto = JsonSerializer.Deserialize<AddDomainsDto>(msg.PayloadJson);
                    if (dto == null || dto.Domains.Count == 0)
                        return Task.FromResult(IpcResponse.Fail("No domains provided to add."));

                    var toAdd = new List<BlockedDomain>();
                    var errors = new List<string>();

                    foreach (string raw in dto.Domains)
                    {
                        string normalized = DomainValidator.NormalizeDomain(raw);
                        if (DomainValidator.IsValidDomain(normalized, out string? err))
                        {
                            toAdd.Add(new BlockedDomain
                            {
                                Domain = normalized,
                                MatchingMode = dto.MatchingMode,
                                Category = string.IsNullOrWhiteSpace(dto.Category) ? "General" : dto.Category.Trim(),
                                CreatedAt = DateTimeOffset.UtcNow,
                                Status = DomainStatus.Active
                            });
                        }
                        else
                        {
                            errors.Add(err ?? $"Invalid domain: {raw}");
                        }
                    }

                    if (toAdd.Count > 0)
                    {
                        _db.AddDomains(toAdd);
                        _coordinator.SyncRules();
                    }

                    string summary = $"Added {toAdd.Count} domain(s)." + (errors.Count > 0 ? $" Skipped {errors.Count} invalid entries." : "");
                    return Task.FromResult(IpcResponse.Ok(summary));
                }

                case "SubmitRemoval":
                {
                    if (string.IsNullOrEmpty(msg.PayloadJson))
                        return Task.FromResult(IpcResponse.Fail("Missing payload for SubmitRemoval"));

                    var dto = JsonSerializer.Deserialize<DomainActionDto>(msg.PayloadJson);
                    if (dto == null || string.IsNullOrEmpty(dto.DomainId))
                        return Task.FromResult(IpcResponse.Fail("Invalid domain ID."));

                    var req = _stateMachine.SubmitRemovalRequest(dto.DomainId);
                    return Task.FromResult(IpcResponse.Ok(JsonSerializer.Serialize(req)));
                }

                case "SubmitTemporaryUnlock":
                {
                    if (string.IsNullOrEmpty(msg.PayloadJson))
                        return Task.FromResult(IpcResponse.Fail("Missing payload for SubmitTemporaryUnlock"));

                    var dto = JsonSerializer.Deserialize<DomainActionDto>(msg.PayloadJson);
                    if (dto == null || string.IsNullOrEmpty(dto.DomainId))
                        return Task.FromResult(IpcResponse.Fail("Invalid domain ID."));

                    var req = _stateMachine.SubmitTemporaryUnlockRequest(dto.DomainId);
                    return Task.FromResult(IpcResponse.Ok(JsonSerializer.Serialize(req)));
                }

                case "ConfirmRequest":
                {
                    if (string.IsNullOrEmpty(msg.PayloadJson))
                        return Task.FromResult(IpcResponse.Fail("Missing payload for ConfirmRequest"));

                    var dto = JsonSerializer.Deserialize<RequestActionDto>(msg.PayloadJson);
                    if (dto == null || string.IsNullOrEmpty(dto.RequestId))
                        return Task.FromResult(IpcResponse.Fail("Invalid request ID."));

                    var req = _stateMachine.ConfirmRequest(dto.RequestId);
                    return Task.FromResult(IpcResponse.Ok(JsonSerializer.Serialize(req)));
                }

                case "CancelRequest":
                {
                    if (string.IsNullOrEmpty(msg.PayloadJson))
                        return Task.FromResult(IpcResponse.Fail("Missing payload for CancelRequest"));

                    var dto = JsonSerializer.Deserialize<RequestActionDto>(msg.PayloadJson);
                    if (dto == null || string.IsNullOrEmpty(dto.RequestId))
                        return Task.FromResult(IpcResponse.Fail("Invalid request ID."));

                    var req = _stateMachine.CancelRequest(dto.RequestId, dto.Reason);
                    _coordinator.SyncRules();
                    return Task.FromResult(IpcResponse.Ok(JsonSerializer.Serialize(req)));
                }

                case "GetCancellationHistory":
                {
                    var history = _db.GetCancellationHistory();
                    return Task.FromResult(IpcResponse.Ok(JsonSerializer.Serialize(history)));
                }

                case "GetEvents":
                {
                    var events = _db.GetRecentEvents(100);
                    return Task.FromResult(IpcResponse.Ok(JsonSerializer.Serialize(events)));
                }

                case "GetSettings":
                {
                    var settings = _db.GetPolicySettings();
                    return Task.FromResult(IpcResponse.Ok(JsonSerializer.Serialize(settings)));
                }

                case "SubmitPolicyChange":
                {
                    if (string.IsNullOrEmpty(msg.PayloadJson))
                        return Task.FromResult(IpcResponse.Fail("Missing payload for SubmitPolicyChange"));

                    var dto = JsonSerializer.Deserialize<PolicyChangeDto>(msg.PayloadJson);
                    if (dto == null || string.IsNullOrEmpty(dto.Key))
                        return Task.FromResult(IpcResponse.Fail("Invalid setting key."));

                    var req = _stateMachine.SubmitPolicyChangeRequest(dto.Key, dto.NewValue);
                    return Task.FromResult(IpcResponse.Ok(JsonSerializer.Serialize(req)));
                }

                default:
                    return Task.FromResult(IpcResponse.Fail($"Unknown message type '{msg.MessageType}'."));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception handling IPC message '{MessageType}': {Message}", msg.MessageType, ex.Message);
            return Task.FromResult(IpcResponse.Fail(ex.Message));
        }
    }
}
