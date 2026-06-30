using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using AudioMixerWin.Core.Models;
using AudioMixerWin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AudioMixerWin.Core.Controls;

public sealed partial class AppPickerDialog : ContentDialog
{
    public sealed class KnobOption
    {
        public int Index { get; set; }
        public string Label { get; set; } = "";
    }

    private readonly ChannelViewModel _channel;
    private readonly DispatcherTimer _hideInfoBarTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public ObservableCollection<AudioSession> SelectableSessions { get; }

    public List<KnobOption> KnobOptions { get; }

    public AudioSession? SelectedSession => SessionsList.SelectedItem as AudioSession;

    public int SelectedKnobIndex => (KnobCombo.SelectedItem as KnobOption)?.Index ?? _channel.KnobIndex;

    public AppPickerDialog(ChannelViewModel channel)
    {
        _channel = channel;
        SelectableSessions = new ObservableCollection<AudioSession>(_channel.GetSelectableSessions());
        KnobOptions = _channel.GetSelectableKnobIndices()
            .Select(i => new KnobOption { Index = i, Label = $"Knob {i + 1}" })
            .ToList();
        InitializeComponent();

        KnobCombo.SelectedItem = KnobOptions.FirstOrDefault(o => o.Index == _channel.KnobIndex);

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

        var blocked = _channel.HideSession(session);
        if (blocked is not null)
        {
            HideInfoBar.Message = blocked;
            HideInfoBar.IsOpen = true;
            _hideInfoBarTimer.Stop();
            _hideInfoBarTimer.Start();
        }
        else
        {
            SelectableSessions.Remove(session);
        }
    }
}
