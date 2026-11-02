using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using DesktopNetBlocker.Core.Ipc;
using DesktopNetBlocker.Core.Models;
using DesktopNetBlocker.UI.Services;

namespace DesktopNetBlocker.UI.ViewModels;

public class AuthorizationViewModel : ViewModelBase
{
    private readonly ServiceClient _serviceClient;

    private AuthorizationRequest? _activeRequest;
    public AuthorizationRequest? ActiveRequest
    {
        get => _activeRequest;
        set
        {
            if (SetProperty(ref _activeRequest, value))
            {
                OnPropertyChanged(nameof(HasActiveRequest));
                OnPropertyChanged(nameof(CanConfirm));
                OnPropertyChanged(nameof(StatusHeader));
                UpdateCountdownDisplay();
            }
        }
    }

    public bool HasActiveRequest => ActiveRequest != null;
    public bool CanConfirm => ActiveRequest?.Status == RequestStatus.Confirmation;

    public string StatusHeader => ActiveRequest switch
    {
        null => "No Pending Authorization Requests",
        { Status: RequestStatus.Pending } => "STAGE 1: INITIAL WAITING PERIOD",
        { Status: RequestStatus.Confirmation } => "STAGE 2: CONFIRMATION REQUIRED",
        { Status: RequestStatus.FinalDelay } => "STAGE 3: FINAL DELAY IN PROGRESS",
        _ => $"STATUS: {ActiveRequest.Status}"
    };

    private string _countdownText = "00:00:00:00";
    public string CountdownText
    {
        get => _countdownText;
        set => SetProperty(ref _countdownText, value);
    }

    private string _targetDeadlineText = string.Empty;
    public string TargetDeadlineText
    {
        get => _targetDeadlineText;
        set => SetProperty(ref _targetDeadlineText, value);
    }

    public ObservableCollection<CancellationHistory> CancellationHistories { get; } = [];

    public ICommand ConfirmCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RefreshCommand { get; }

    public AuthorizationViewModel(ServiceClient serviceClient)
    {
        _serviceClient = serviceClient;

        ConfirmCommand = new RelayCommand(async () => await ConfirmAsync(), () => CanConfirm);
        CancelCommand = new RelayCommand(async () => await CancelAsync(), () => HasActiveRequest);
        RefreshCommand = new RelayCommand(async () => await RefreshAsync());

        _serviceClient.StatusChanged += OnStatusChanged;
    }

    private void OnStatusChanged(StatusDto? status)
    {
        ActiveRequest = status?.ActiveRequest;
        UpdateCountdownDisplay();
    }

    public void UpdateCountdownDisplay()
    {
        if (ActiveRequest == null)
        {
            CountdownText = "00:00:00:00";
            TargetDeadlineText = "No active countdown";
            return;
        }

        var now = DateTimeOffset.UtcNow;
        DateTimeOffset target;

        switch (ActiveRequest.Status)
        {
            case RequestStatus.Pending:
                target = ActiveRequest.InitialDeadline;
                TargetDeadlineText = $"Initial Wait Ends: {target.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
                break;

            case RequestStatus.Confirmation:
                target = ActiveRequest.ConfirmationDeadline ?? now;
                TargetDeadlineText = $"Confirmation Expires: {target.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
                break;

            case RequestStatus.FinalDelay:
                target = ActiveRequest.FinalDeadline ?? now;
                TargetDeadlineText = $"Final Authorization: {target.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
                break;

            default:
                CountdownText = "00:00:00:00";
                TargetDeadlineText = ActiveRequest.Status.ToString();
                return;
        }

        var remaining = target - now;
        if (remaining <= TimeSpan.Zero)
        {
            CountdownText = "00:00:00:00";
        }
        else
        {
            CountdownText = $"{(int)remaining.TotalDays:D2}:{remaining.Hours:D2}:{remaining.Minutes:D2}:{remaining.Seconds:D2}";
        }
    }

    private async Task ConfirmAsync()
    {
        if (ActiveRequest == null || !CanConfirm) return;

        var result = MessageBox.Show(
            $"Confirm authorization request for '{ActiveRequest.TargetDomain}'?\n\n" +
            "Confirming will initiate the 3-day final delay period. The domain remains BLOCKED during this delay.",
            "Confirm Authorization",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        var (success, message) = await _serviceClient.ConfirmRequestAsync(ActiveRequest.Id);
        if (success)
        {
            MessageBox.Show("Request confirmed. The final waiting period has begun.", "Confirmed", MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync();
        }
        else
        {
            MessageBox.Show($"Failed to confirm: {message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task CancelAsync()
    {
        if (ActiveRequest == null) return;

        var result = MessageBox.Show(
            $"Cancel the authorization request for '{ActiveRequest.TargetDomain}'?\n\n" +
            "WARNING: Cancelling will immediately return the domain to the fully blocked state and trigger an escalating cooldown on future requests for this domain (7d -> 14d -> 30d).",
            "Cancel Request & Apply Cooldown",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        var (success, message) = await _serviceClient.CancelRequestAsync(ActiveRequest.Id, "User cancelled via Authorization Dashboard");
        if (success)
        {
            MessageBox.Show("Request cancelled. The domain restriction is preserved and a cancellation cooldown has been applied.", "Cancelled", MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync();
        }
        else
        {
            MessageBox.Show($"Failed to cancel: {message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public async Task RefreshAsync()
    {
        await _serviceClient.PollStatusAsync();
        var histories = await _serviceClient.GetCancellationHistoryAsync();
        CancellationHistories.Clear();
        foreach (var h in histories)
        {
            CancellationHistories.Add(h);
        }
    }
}

