using System;
using System.Windows;
using System.Windows.Threading;
using DesktopNetBlocker.UI.Services;
using DesktopNetBlocker.UI.ViewModels;
using DesktopNetBlocker.UI.Views;

namespace DesktopNetBlocker.UI;

public partial class App : Application
{
    private ServiceClient? _serviceClient;
    private MainViewModel? _mainViewModel;
    private MainWindow? _mainWindow;
    private OverlayWindow? _overlayWindow;
    private DispatcherTimer? _uiTimer;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        _serviceClient = new ServiceClient();
        _mainViewModel = new MainViewModel(_serviceClient);

        _mainWindow = new MainWindow(_mainViewModel);
        _overlayWindow = new OverlayWindow(_mainViewModel.OverlayVM);

        // Observe Overlay visibility changes to show/hide the window
        _mainViewModel.OverlayVM.PropertyChanged += (s, args) =>
        {
            if (args.PropertyName == nameof(OverlayViewModel.IsVisible))
            {
                Dispatcher.Invoke(() =>
                {
                    if (_mainViewModel.OverlayVM.IsVisible)
                    {
                        if (!_overlayWindow.IsVisible)
                            _overlayWindow.Show();
                    }
                    else
                    {
                        _overlayWindow.Hide();
                    }
                });
            }
        };

        // 1-second UI tick for live countdowns
        _uiTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _uiTimer.Tick += (s, args) =>
        {
            _mainViewModel.UpdateTimers();
        };
        _uiTimer.Start();

        _mainWindow.Show();
    }
}

