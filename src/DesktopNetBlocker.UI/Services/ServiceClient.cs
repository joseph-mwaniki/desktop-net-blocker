using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DesktopNetBlocker.Core.Ipc;
using DesktopNetBlocker.Core.Models;

namespace DesktopNetBlocker.UI.Services;

public class ServiceClient
{
    private readonly NamedPipeIpcClient _ipc = new();
    private readonly Timer _pollTimer;
    public StatusDto? CurrentStatus { get; private set; }
    public bool IsConnected { get; private set; }

    public event Action<StatusDto?>? StatusChanged;
    public event Action<bool>? ConnectionChanged;

    public ServiceClient()
    {
        _pollTimer = new Timer(async _ => await PollStatusAsync(), null, 500, 1000);
    }

    public async Task<bool> PollStatusAsync()
    {
        try
        {
            var resp = await _ipc.SendAsync("GetStatus", null, timeoutMs: 1500);
            if (resp.Success && !string.IsNullOrEmpty(resp.PayloadJson))
            {
                var status = JsonSerializer.Deserialize<StatusDto>(resp.PayloadJson);
                CurrentStatus = status;
                if (!IsConnected)
                {
                    IsConnected = true;
                    ConnectionChanged?.Invoke(true);
                }
                StatusChanged?.Invoke(CurrentStatus);
                return true;
            }
        }
        catch { }

        if (IsConnected)
        {
            IsConnected = false;
            CurrentStatus = null;
            ConnectionChanged?.Invoke(false);
            StatusChanged?.Invoke(null);
        }
        return false;
    }

    public async Task<List<BlockedDomain>> GetDomainsAsync()
    {
        var resp = await _ipc.SendAsync("GetDomains");
        if (resp.Success && !string.IsNullOrEmpty(resp.PayloadJson))
        {
            return JsonSerializer.Deserialize<List<BlockedDomain>>(resp.PayloadJson) ?? [];
        }
        return [];
    }

    public async Task<(bool Success, string Message)> AddDomainsAsync(List<string> domains, MatchingMode mode, string category)
    {
        var dto = new AddDomainsDto
        {
            Domains = domains,
            MatchingMode = mode,
            Category = category
        };
        var resp = await _ipc.SendAsync("AddDomains", dto);
        await PollStatusAsync();
        return (resp.Success, resp.PayloadJson ?? resp.ErrorMessage ?? "Unknown response");
    }

    public async Task<(bool Success, string Message)> SubmitRemovalAsync(string domainId)
    {
        var dto = new DomainActionDto { DomainId = domainId };
        var resp = await _ipc.SendAsync("SubmitRemoval", dto);
        await PollStatusAsync();
        return (resp.Success, resp.ErrorMessage ?? "Removal request submitted.");
    }

    public async Task<(bool Success, string Message)> SubmitTemporaryUnlockAsync(string domainId)
    {
        var dto = new DomainActionDto { DomainId = domainId };
        var resp = await _ipc.SendAsync("SubmitTemporaryUnlock", dto);
        await PollStatusAsync();
        return (resp.Success, resp.ErrorMessage ?? "Temporary unlock request submitted.");
    }

    public async Task<(bool Success, string Message)> ConfirmRequestAsync(string requestId)
    {
        var dto = new RequestActionDto { RequestId = requestId };
        var resp = await _ipc.SendAsync("ConfirmRequest", dto);
        await PollStatusAsync();
        return (resp.Success, resp.ErrorMessage ?? "Request confirmed. Final delay period started.");
    }

    public async Task<(bool Success, string Message)> CancelRequestAsync(string requestId, string? reason = null)
    {
        var dto = new RequestActionDto { RequestId = requestId, Reason = reason };
        var resp = await _ipc.SendAsync("CancelRequest", dto);
        await PollStatusAsync();
        return (resp.Success, resp.ErrorMessage ?? "Request cancelled. Restriction preserved.");
    }

    public async Task<List<CancellationHistory>> GetCancellationHistoryAsync()
    {
        var resp = await _ipc.SendAsync("GetCancellationHistory");
        if (resp.Success && !string.IsNullOrEmpty(resp.PayloadJson))
        {
            return JsonSerializer.Deserialize<List<CancellationHistory>>(resp.PayloadJson) ?? [];
        }
        return [];
    }

    public async Task<List<RestrictionEvent>> GetEventsAsync()
    {
        var resp = await _ipc.SendAsync("GetEvents");
        if (resp.Success && !string.IsNullOrEmpty(resp.PayloadJson))
        {
            return JsonSerializer.Deserialize<List<RestrictionEvent>>(resp.PayloadJson) ?? [];
        }
        return [];
    }

    public async Task<Dictionary<string, string>> GetSettingsAsync()
    {
        var resp = await _ipc.SendAsync("GetSettings");
        if (resp.Success && !string.IsNullOrEmpty(resp.PayloadJson))
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(resp.PayloadJson) ?? [];
        }
        return [];
    }

    public async Task<(bool Success, string Message)> SubmitPolicyChangeAsync(string key, string newValue)
    {
        var dto = new PolicyChangeDto { Key = key, NewValue = newValue };
        var resp = await _ipc.SendAsync("SubmitPolicyChange", dto);
        await PollStatusAsync();
        return (resp.Success, resp.ErrorMessage ?? "Policy modification request submitted.");
    }
}

