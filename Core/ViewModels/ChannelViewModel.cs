using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using AudioMixerWin.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;

namespace AudioMixerWin.Core.ViewModels;

public partial class ChannelViewModel : ObservableObject
{
    private readonly AudioManager _audioManager;
    private readonly ObservableCollection<ChannelViewModel> _channels;
    private readonly Action<ChannelViewModel> _onRemove;
    private readonly Action _onSettingsChanged;
    private readonly Func<AudioSession, string?> _onHideSession;

    public int KnobIndex { get; }

    public string KnobLabel => $"Knob {KnobIndex + 1}";

    public ObservableCollection<AudioSession> AvailableSessions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string appName;

    [ObservableProperty]
    private double volume;

    [ObservableProperty]
    private ImageSource? iconSource;

    public string DisplayName => AudioManager.GetDisplayName(AppName);

    public ChannelViewModel(
        int knobIndex,
        string appName,
        AudioManager audioManager,
        ObservableCollection<AudioSession> availableSessions,
        ObservableCollection<ChannelViewModel> channels,
        Action<ChannelViewModel> onRemove,
        Action onSettingsChanged,
        Func<AudioSession, string?> onHideSession)
    {
        KnobIndex = knobIndex;
        _audioManager = audioManager;
        AvailableSessions = availableSessions;
        _channels = channels;
        _onRemove = onRemove;
        _onSettingsChanged = onSettingsChanged;
        _onHideSession = onHideSession;
        this.appName = appName;
        volume = audioManager.GetVolume(appName) * 100;

        AvailableSessions.CollectionChanged += OnAvailableSessionsChanged;
        IconSource = GetSessionIcon(appName);
    }

    partial void OnAppNameChanged(string value)
    {
        Volume = _audioManager.GetVolume(value) * 100;
        IconSource = GetSessionIcon(value);
        _onSettingsChanged();
    }

    private void OnAvailableSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var session = AvailableSessions.FirstOrDefault(s => s.ProcessName.Equals(AppName, StringComparison.OrdinalIgnoreCase));
        if (session is null)
            return; // app not running — keep last-known icon and volume

        IconSource = session.IconSource;
        Volume = _audioManager.GetVolume(AppName) * 100;
    }

    private ImageSource? GetSessionIcon(string appName) =>
        AvailableSessions.FirstOrDefault(s => s.ProcessName.Equals(appName, StringComparison.OrdinalIgnoreCase))?.IconSource;

    partial void OnVolumeChanged(double value) =>
        _audioManager.SetVolume(AppName, (float)(value / 100.0));

    public IEnumerable<AudioSession> GetSelectableSessions()
    {
        var takenByOthers = new HashSet<string>(
            _channels.Where(c => c != this).Select(c => c.AppName),
            StringComparer.OrdinalIgnoreCase);

        return AvailableSessions.Where(s => !takenByOthers.Contains(s.ProcessName));
    }

    public static ChannelViewModel? FindAssignedChannel(IEnumerable<ChannelViewModel> channels, string processName) =>
        channels.FirstOrDefault(c => c.AppName.Equals(processName, StringComparison.OrdinalIgnoreCase));

    public string? HideSession(AudioSession session) => _onHideSession(session);

    public void Remove()
    {
        AvailableSessions.CollectionChanged -= OnAvailableSessionsChanged;
        _onRemove(this);
    }
}
