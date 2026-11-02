using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using DesktopNetBlocker.Core.Ipc;
using DesktopNetBlocker.Core.Models;
using DesktopNetBlocker.UI.Services;

namespace DesktopNetBlocker.UI.ViewModels;

public class DashboardViewModel : ViewModelBase
{
    private readonly ServiceClient _serviceClient;

    private bool _isEnforcementActive = true;
    public bool IsEnforcementActive
    {
        get => _isEnforcementActive;
        set => SetProperty(ref _isEnforcementActive, value);
    }

    private int _blockedDomainsCount;
    public int BlockedDomainsCount
    {
        get => _blockedDomainsCount;
        set => SetProperty(ref _blockedDomainsCount, value);
    }

    private string _activeRequestSummary = "No pending authorization requests";
    public string ActiveRequestSummary
    {
        get => _activeRequestSummary;
        set => SetProperty(ref _activeRequestSummary, value);
    }

    private bool _hasActiveRequest;
    public bool HasActiveRequest
    {
        get => _hasActiveRequest;
        set => SetProperty(ref _hasActiveRequest, value);
    }

    private string _activeRequestType = string.Empty;
    public string ActiveRequestType
    {
        get => _activeRequestType;
        set => SetProperty(ref _activeRequestType, value);
    }

    private string _activeRequestDomain = string.Empty;
    public string ActiveRequestDomain
    {
        get => _activeRequestDomain;
        set => SetProperty(ref _activeRequestDomain, value);
    }

    private string _serviceStatusText = "Connecting to Background Service...";
    public string ServiceStatusText
    {
        get => _serviceStatusText;
        set => SetProperty(ref _serviceStatusText, value);
    }

    public ObservableCollection<RestrictionEvent> RecentEvents { get; } = [];

    public ICommand RefreshCommand { get; }

    public DashboardViewModel(ServiceClient serviceClient)
    {
        _serviceClient = serviceClient;
        RefreshCommand = new RelayCommand(async () => await RefreshAsync());

        _serviceClient.StatusChanged += OnStatusChanged;
        _serviceClient.ConnectionChanged += OnConnectionChanged;
    }

    private void OnConnectionChanged(bool connected)
    {
        ServiceStatusText = connected ? "Background Service Active & Enforced" : "Background Service Disconnected";
        IsEnforcementActive = connected;
    }

    private void OnStatusChanged(StatusDto? status)
    {
        if (status == null)
        {
            ServiceStatusText = "Background Service Disconnected";
            IsEnforcementActive = false;
            HasActiveRequest = false;
            ActiveRequestSummary = "Service unavailable";
            return;
        }

        IsEnforcementActive = status.IsServiceRunning && status.IsDnsFilteringActive;
        BlockedDomainsCount = status.BlockedDomainsCount;
        ServiceStatusText = "System-Wide Network Enforcement Active";

        if (status.ActiveRequest != null)
        {
            HasActiveRequest = true;
            ActiveRequestType = status.ActiveRequest.RequestType.ToString();
            ActiveRequestDomain = status.ActiveRequest.TargetDomain;
            ActiveRequestSummary = $"{status.ActiveRequest.RequestType} for '{status.ActiveRequest.TargetDomain}' ({status.ActiveRequest.Status})";
        }
        else
        {
            HasActiveRequest = false;
            ActiveRequestType = string.Empty;
            ActiveRequestDomain = string.Empty;
            ActiveRequestSummary = "No pending authorization requests. Blocklist is fully locked.";
        }
    }

    public async Task RefreshAsync()
    {
        await _serviceClient.PollStatusAsync();
        var events = await _serviceClient.GetEventsAsync();
        RecentEvents.Clear();
        foreach (var ev in events)
        {
            RecentEvents.Add(ev);
        }
    }
}

