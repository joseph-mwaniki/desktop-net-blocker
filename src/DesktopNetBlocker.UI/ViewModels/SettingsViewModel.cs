using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using DesktopNetBlocker.UI.Services;

namespace DesktopNetBlocker.UI.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private readonly ServiceClient _serviceClient;

    private string _initialWaitingDays = "3";
    public string InitialWaitingDays
    {
        get => _initialWaitingDays;
        set => SetProperty(ref _initialWaitingDays, value);
    }

    private string _confirmationDelayDays = "3";
    public string ConfirmationDelayDays
    {
        get => _confirmationDelayDays;
        set => SetProperty(ref _confirmationDelayDays, value);
    }

    private string _temporaryAccessHours = "2";
    public string TemporaryAccessHours
    {
        get => _temporaryAccessHours;
        set => SetProperty(ref _temporaryAccessHours, value);
    }

    private double _overlayOpacity = 0.92;
    public double OverlayOpacity
    {
        get => _overlayOpacity;
        set => SetProperty(ref _overlayOpacity, value);
    }

    private string _overlayPosition = "TopRight";
    public string OverlayPosition
    {
        get => _overlayPosition;
        set => SetProperty(ref _overlayPosition, value);
    }

    private bool _overlayEnabled = true;
    public bool OverlayEnabled
    {
        get => _overlayEnabled;
        set => SetProperty(ref _overlayEnabled, value);
    }

    public List<string> AvailablePositions { get; } = ["TopRight", "BottomRight", "TopLeft", "BottomLeft"];

    public ICommand RequestPolicyChangeCommand { get; }
    public ICommand SaveAppearanceSettingsCommand { get; }
    public ICommand RefreshCommand { get; }

    public SettingsViewModel(ServiceClient serviceClient)
    {
        _serviceClient = serviceClient;

        RequestPolicyChangeCommand = new RelayCommand(async (param) => await RequestPolicyChangeAsync(param as string));
        SaveAppearanceSettingsCommand = new RelayCommand(SaveAppearanceSettings);
        RefreshCommand = new RelayCommand(async () => await LoadSettingsAsync());
    }

    public async Task LoadSettingsAsync()
    {
        var settings = await _serviceClient.GetSettingsAsync();
        if (settings.TryGetValue("InitialWaitingPeriodDays", out var initDays))
            InitialWaitingDays = initDays;
        if (settings.TryGetValue("ConfirmationDelayDays", out var confDays))
            ConfirmationDelayDays = confDays;
        if (settings.TryGetValue("TemporaryAccessDurationHours", out var tempHrs))
            TemporaryAccessHours = tempHrs;
        if (settings.TryGetValue("OverlayOpacity", out var op) && double.TryParse(op, out double dOp))
            OverlayOpacity = dOp;
        if (settings.TryGetValue("OverlayPosition", out var pos))
            OverlayPosition = pos;
        if (settings.TryGetValue("OverlayEnabled", out var en) && bool.TryParse(en, out bool bEn))
            OverlayEnabled = bEn;
    }

    private void SaveAppearanceSettings()
    {
        // Appearance settings (opacity, position) can be adjusted locally for desktop comfort
        MessageBox.Show("Overlay appearance settings saved.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task RequestPolicyChangeAsync(string? settingKey)
    {
        if (string.IsNullOrEmpty(settingKey)) return;

        string newValue = settingKey switch
        {
            "InitialWaitingPeriodDays" => InitialWaitingDays,
            "ConfirmationDelayDays" => ConfirmationDelayDays,
            "TemporaryAccessDurationHours" => TemporaryAccessHours,
            _ => string.Empty
        };

        var result = MessageBox.Show(
            $"Requesting a policy modification to '{settingKey}' = '{newValue}'.\n\n" +
            "SECURITY NOTICE: Modification of authorization policies cannot take effect immediately! " +
            "It requires the full 3-day delayed authorization workflow to prevent bypassing restrictions.\n\n" +
            "Do you wish to submit this policy modification request?",
            "Policy Change Requires Delayed Authorization",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        var (success, msg) = await _serviceClient.SubmitPolicyChangeAsync(settingKey, newValue);
        if (success)
        {
            MessageBox.Show("Policy modification request submitted. A 3-day waiting countdown has started.", "Request Submitted", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show($"Failed to submit policy modification: {msg}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

