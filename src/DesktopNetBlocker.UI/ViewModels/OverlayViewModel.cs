using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using DesktopNetBlocker.Core.Ipc;
using DesktopNetBlocker.Core.Models;
using DesktopNetBlocker.UI.Services;

namespace DesktopNetBlocker.UI.ViewModels;

public class OverlayViewModel : ViewModelBase
{
    private readonly ServiceClient _serviceClient;

    private bool _isVisible;
    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    private string _requestTypeTitle = "DOMAIN REMOVAL REQUEST";
    public string RequestTypeTitle
    {
        get => _requestTypeTitle;
        set => SetProperty(ref _requestTypeTitle, value);
    }

    private string _targetDomain = string.Empty;
    public string TargetDomain
    {
        get => _targetDomain;
        set => SetProperty(ref _targetDomain, value);
    }

    private string _countdownTimer = "00:00:00:00";
    public string CountdownTimer
    {
        get => _countdownTimer;
        set => SetProperty(ref _countdownTimer, value);
    }

    private string _stageDescription = "INITIAL DELAY";
    public string StageDescription
    {
        get => _stageDescription;
        set => SetProperty(ref _stageDescription, value);
    }

    private double _opacity = 0.92;
    public double Opacity
    {
        get => _opacity;
        set => SetProperty(ref _opacity, value);
    }

    private AuthorizationRequest? _activeRequest;

    public ICommand CancelCommand { get; }

    public OverlayViewModel(ServiceClient serviceClient)
    {
        _serviceClient = serviceClient;
        CancelCommand = new RelayCommand(async () => await CancelRequestAsync());

        _serviceClient.StatusChanged += OnStatusChanged;
    }

    private void OnStatusChanged(StatusDto? status)
    {
        _activeRequest = status?.ActiveRequest;
        if (_activeRequest == null)
        {
            IsVisible = false;
            return;
        }

        IsVisible = true;
        RequestTypeTitle = _activeRequest.RequestType switch
        {
            RequestType.Removal => "DOMAIN REMOVAL REQUEST",
            RequestType.TemporaryUnlock => "TEMPORARY UNLOCK REQUEST",
            RequestType.PolicyModification => "POLICY MODIFICATION REQUEST",
            _ => "AUTHORIZATION REQUEST"
        };

        TargetDomain = _activeRequest.TargetDomain;
        StageDescription = _activeRequest.Status switch
        {
            RequestStatus.Pending => "INITIAL WAITING PERIOD",
            RequestStatus.Confirmation => "CONFIRMATION REQUIRED",
            RequestStatus.FinalDelay => "FINAL DELAY IN PROGRESS",
            _ => _activeRequest.Status.ToString()
        };

        UpdateCountdown();
    }

    public void UpdateCountdown()
    {
        if (_activeRequest == null)
        {
            CountdownTimer = "00:00:00:00";
            return;
        }

        var now = DateTimeOffset.UtcNow;
        DateTimeOffset deadline = _activeRequest.Status switch
        {
            RequestStatus.Pending => _activeRequest.InitialDeadline,
            RequestStatus.Confirmation => _activeRequest.ConfirmationDeadline ?? now,
            RequestStatus.FinalDelay => _activeRequest.FinalDeadline ?? now,
            _ => now
        };

        var remaining = deadline - now;
        if (remaining <= TimeSpan.Zero)
        {
            CountdownTimer = "00:00:00:00";
        }
        else
        {
            CountdownTimer = $"{(int)remaining.TotalDays:D2}:{remaining.Hours:D2}:{remaining.Minutes:D2}:{remaining.Seconds:D2}";
        }
    }

    private async Task CancelRequestAsync()
    {
        if (_activeRequest == null) return;

        var result = MessageBox.Show(
            $"Cancel the pending request for '{_activeRequest.TargetDomain}'?\n\n" +
            "The restriction will remain actively enforced, and an escalating cancellation cooldown (7d -> 14d -> 30d) will be applied.",
            "Confirm Cancellation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        // 1. Immediately hide overlay locally
        IsVisible = false;

        // 2. Communicate with background service
        var (success, msg) = await _serviceClient.CancelRequestAsync(_activeRequest.Id, "Cancelled via floating countdown overlay");
        if (!success)
        {
            MessageBox.Show($"Failed to cancel: {msg}", "Cancellation Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

