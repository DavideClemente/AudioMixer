using System.IO.Ports;
using AudioMixerWin.Core.Models;
using AudioMixerWin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AudioMixerWin.Core.Views;

public sealed partial class SettingsPage : Page
{
    public MainViewModel ViewModel { get; }

    public string[] PortNames { get; } = SerialPort.GetPortNames();

    public int[] BaudRates { get; } = { 9600, 19200, 38400, 57600, 115200 };

    public SettingsPage(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    private void OnHideClick(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not AudioSession session)
            return;

        var blocked = ViewModel.HideSession(session);
        if (blocked is not null)
        {
            HideInfoBar.Message = blocked;
            HideInfoBar.IsOpen = true;
        }
    }

    // x:Bind to ViewModel.UnhideProcessCommand from inside this DataTemplate crashes XamlCompiler, so invoke it via Tag/Click instead.
    private void OnShowClick(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not string processName)
            return;

        ViewModel.UnhideProcessCommand.Execute(processName);
    }
}
