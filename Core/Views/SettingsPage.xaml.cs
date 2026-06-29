using System;
using AudioMixerWin.Core.Models;
using AudioMixerWin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AudioMixerWin.Core.Views;

public sealed partial class SettingsPage : Page
{
    private readonly DispatcherTimer _hideInfoBarTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public MainViewModel ViewModel { get; }

    public ComPortInfo[] PortInfos { get; } = ComPortInfo.GetPorts();

    public int[] BaudRates { get; } = { 9600, 19200, 38400, 57600, 115200 };

    public SettingsPage(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        _hideInfoBarTimer.Tick += (_, _) =>
        {
            _hideInfoBarTimer.Stop();
            HideInfoBar.IsOpen = false;
        };
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
            _hideInfoBarTimer.Stop();
            _hideInfoBarTimer.Start();
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
