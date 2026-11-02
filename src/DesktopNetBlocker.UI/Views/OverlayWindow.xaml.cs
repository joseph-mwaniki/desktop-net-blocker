using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using DesktopNetBlocker.UI.ViewModels;

namespace DesktopNetBlocker.UI.Views;

public partial class OverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public OverlayWindow(OverlayViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Position window in top right corner of primary working area
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 24;
        Top = workArea.Top + 24;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (OperatingSystem.IsWindows())
        {
            var helper = new WindowInteropHelper(this);
            IntPtr hwnd = helper.Handle;
            if (hwnd != IntPtr.Zero)
            {
                long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
                exStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exStyle));
            }
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}

