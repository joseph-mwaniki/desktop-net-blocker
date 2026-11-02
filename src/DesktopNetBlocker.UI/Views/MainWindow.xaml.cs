using System.Windows;
using DesktopNetBlocker.UI.ViewModels;

namespace DesktopNetBlocker.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}

