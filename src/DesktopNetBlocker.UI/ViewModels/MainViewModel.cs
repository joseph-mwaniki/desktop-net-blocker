using System;
using System.Windows.Input;
using DesktopNetBlocker.UI.Services;

namespace DesktopNetBlocker.UI.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly ServiceClient _serviceClient;

    public DashboardViewModel DashboardVM { get; }
    public BlocklistViewModel BlocklistVM { get; }
    public AuthorizationViewModel AuthorizationVM { get; }
    public SettingsViewModel SettingsVM { get; }
    public OverlayViewModel OverlayVM { get; }

    private object _currentView;
    public object CurrentView
    {
        get => _currentView;
        set => SetProperty(ref _currentView, value);
    }

    private string _activeTabName = "Dashboard";
    public string ActiveTabName
    {
        get => _activeTabName;
        set => SetProperty(ref _activeTabName, value);
    }

    public ICommand NavigateDashboardCommand { get; }
    public ICommand NavigateBlocklistCommand { get; }
    public ICommand NavigateAuthorizationCommand { get; }
    public ICommand NavigateSettingsCommand { get; }

    public MainViewModel(ServiceClient serviceClient)
    {
        _serviceClient = serviceClient;

        DashboardVM = new DashboardViewModel(_serviceClient);
        BlocklistVM = new BlocklistViewModel(_serviceClient);
        AuthorizationVM = new AuthorizationViewModel(_serviceClient);
        SettingsVM = new SettingsViewModel(_serviceClient);
        OverlayVM = new OverlayViewModel(_serviceClient);

        _currentView = DashboardVM;

        NavigateDashboardCommand = new RelayCommand(async () =>
        {
            CurrentView = DashboardVM;
            ActiveTabName = "Dashboard";
            await DashboardVM.RefreshAsync();
        });

        NavigateBlocklistCommand = new RelayCommand(async () =>
        {
            CurrentView = BlocklistVM;
            ActiveTabName = "Blocklist";
            await BlocklistVM.LoadDomainsAsync();
        });

        NavigateAuthorizationCommand = new RelayCommand(async () =>
        {
            CurrentView = AuthorizationVM;
            ActiveTabName = "Authorization";
            await AuthorizationVM.RefreshAsync();
        });

        NavigateSettingsCommand = new RelayCommand(async () =>
        {
            CurrentView = SettingsVM;
            ActiveTabName = "Settings";
            await SettingsVM.LoadSettingsAsync();
        });
    }

    public void UpdateTimers()
    {
        AuthorizationVM.UpdateCountdownDisplay();
        OverlayVM.UpdateCountdown();
    }
}

