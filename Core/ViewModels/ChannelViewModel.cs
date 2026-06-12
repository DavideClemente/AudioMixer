using System;
using System.Collections.Generic;
using AudioMixerWin.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioMixerWin.Core.ViewModels;

public partial class ChannelViewModel : ObservableObject
{
    private readonly AudioManager _audioManager;
    private readonly Action<ChannelViewModel> _onRemove;
    private readonly Action _onSettingsChanged;

    public int KnobIndex { get; }

    public string KnobLabel => $"Knob {KnobIndex + 1}";

    [ObservableProperty]
    private string appName;

    [ObservableProperty]
    private double volume;

    public ChannelViewModel(
        int knobIndex,
        string appName,
        AudioManager audioManager,
        Action<ChannelViewModel> onRemove,
        Action onSettingsChanged)
    {
        KnobIndex = knobIndex;
        _audioManager = audioManager;
        _onRemove = onRemove;
        _onSettingsChanged = onSettingsChanged;
        this.appName = appName;
        volume = audioManager.GetVolume(appName) * 100;
    }

    partial void OnAppNameChanged(string value)
    {
        Volume = _audioManager.GetVolume(value) * 100;
        _onSettingsChanged();
    }

    partial void OnVolumeChanged(double value) =>
        _audioManager.SetVolume(AppName, (float)(value / 100.0));

    public List<AudioSession> GetAvailableSessions() => _audioManager.GetSessions();

    public void Remove() => _onRemove(this);
}
