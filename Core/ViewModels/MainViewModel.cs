using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AudioMixerWin.Core.Models;
using AudioMixerWin.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AudioMixerWin.Core.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AudioManager _audioManager;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _refreshTimer;
    private SerialManager _serial;

    [ObservableProperty]
    private string comPort;

    [ObservableProperty]
    private int baudRate;

    [ObservableProperty]
    private string serialStatus = "Not connected";

    [ObservableProperty]
    private double refreshIntervalSeconds;

    [ObservableProperty]
    private double navPaneWidth;

    [ObservableProperty]
    private InputMode inputMode;

    [ObservableProperty]
    private double encoderStepPercent;

    [ObservableProperty]
    private bool debugSerialEvents;

    [ObservableProperty]
    private int draggedChannelIndex = -1;

    [ObservableProperty]
    private int targetDropIndex = -1;

    [ObservableProperty]
    private bool isDragging;

    [ObservableProperty]
    private double dragDropIndicatorY = -1;

    public ObservableCollection<string> SerialLog { get; } = new();

    public ObservableCollection<ChannelViewModel> Channels { get; } = new();

    public ObservableCollection<AudioSession> AvailableSessions { get; } = new();

    public ObservableCollection<string> HiddenProcesses { get; } = new();

    public MainViewModel()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _audioManager = new AudioManager();
        _settings = SettingsService.Load();

        comPort = _settings.ComPort;
        baudRate = _settings.BaudRate;
        refreshIntervalSeconds = _settings.RefreshIntervalSeconds;
        navPaneWidth = _settings.NavPaneWidth;
        inputMode = _settings.InputMode;
        encoderStepPercent = _settings.EncoderStepPercent;
        debugSerialEvents = _settings.DebugSerialEvents;

        foreach (var process in _settings.ExcludedProcesses)
            HiddenProcesses.Add(process);

        // Serial must be created before channels are added: AddChannelInternal
        // reads _serial.IsConnected to seed each channel's connected state.
        _serial = CreateAndStartSerial();

        foreach (var config in _settings.Channels)
            AddChannelInternal(config.AppName, config.KnobIndex, save: false);

        _ = Task.Run(async () =>
        {
            await Task.Delay(2000);
            _dispatcherQueue.TryEnqueue(SyncAllChannels);
        });

        RefreshAvailableSessions();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(RefreshIntervalSeconds) };
        _refreshTimer.Tick += (_, _) => RefreshAvailableSessions();
        _refreshTimer.Start();
    }

    private SerialManager CreateAndStartSerial()
    {
        var serial = new SerialManager(ComPort, BaudRate);
        serial.KnobChanged += OnKnobChanged;
        serial.KnobDelta += OnKnobDelta;
        serial.KnobPressed += OnKnobPressed;

        try
        {
            serial.Start();
            SerialStatus = $"Connected to {ComPort} @ {BaudRate}";
        }
        catch (Exception ex)
        {
            SerialStatus = $"Disconnected: {ex.Message}";
        }

        UpdateChannelsSerialState(serial.IsConnected);
        return serial;
    }

    [RelayCommand]
    private void Reconnect()
    {
        _serial.KnobChanged -= OnKnobChanged;
        _serial.KnobDelta -= OnKnobDelta;
        _serial.KnobPressed -= OnKnobPressed;
        _serial.Stop();
        _serial = CreateAndStartSerial();
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000);
            _dispatcherQueue.TryEnqueue(SyncAllChannels);
        });

        _settings.ComPort = ComPort;
        _settings.BaudRate = BaudRate;
        SettingsService.Save(_settings);
    }

    [RelayCommand]
    private void AddChannel() => AddChannelInternal("Select App");

    private void AddChannelInternal(string appName, int? knobIndex = null, bool save = true)
    {
        var index = knobIndex ?? (Channels.Count == 0 ? 0 : Channels.Max(c => c.KnobIndex) + 1);
        var ch = new ChannelViewModel(index, appName, _audioManager, AvailableSessions, Channels, RemoveChannelInternal, SaveChannels, HideSession, SyncChannel);
        ch.IsSerialConnected = _serial.IsConnected;
        Channels.Add(ch);

        if (save)
            SaveChannels();
    }

    private void RemoveChannelInternal(ChannelViewModel channel)
    {
        Channels.Remove(channel);
        SaveChannels();
    }

    private void SaveChannels()
    {
        _settings.Channels = Channels
            .Select(c => new ChannelConfig { KnobIndex = c.KnobIndex, AppName = c.AppName })
            .ToList();

        SettingsService.Save(_settings);
    }

    private void UpdateChannelsSerialState(bool connected)
    {
        foreach (var ch in Channels)
            ch.IsSerialConnected = connected;
    }

    private void SyncChannel(ChannelViewModel ch)
    {
        var color = _audioManager.GetIconColor(ch.AppName);
        var icon = _audioManager.GetIconRgb565(ch.AppName);
        _serial.SendAssignment(ch.KnobIndex, ch.AppName, color, icon);
        _serial.SendVolume(ch.KnobIndex, _audioManager.GetVolume(ch.AppName));
    }

    private void SyncAllChannels()
    {
        foreach (var ch in Channels)
            SyncChannel(ch);
    }

    internal void SwapChannels(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || toIndex < 0 || fromIndex >= Channels.Count || toIndex >= Channels.Count || fromIndex == toIndex)
            return;

        // Exchange the two grid squares; every other channel stays put.
        var from = Channels[fromIndex];
        Channels[fromIndex] = Channels[toIndex];
        Channels[toIndex] = from;
        SaveChannels();
    }

    public string? HideSession(AudioSession session)
    {
        var assigned = ChannelViewModel.FindAssignedChannel(Channels, session.ProcessName);
        if (assigned is not null)
            return $"Can't hide '{session.ProcessName}' — it's assigned to {assigned.KnobLabel}. Unassign it first.";

        if (!_settings.ExcludedProcesses.Contains(session.ProcessName, StringComparer.OrdinalIgnoreCase))
            _settings.ExcludedProcesses.Add(session.ProcessName);

        if (!HiddenProcesses.Contains(session.ProcessName, StringComparer.OrdinalIgnoreCase))
            HiddenProcesses.Add(session.ProcessName);

        AvailableSessions.Remove(session);
        SettingsService.Save(_settings);
        return null;
    }

    [RelayCommand]
    private void UnhideProcess(string processName)
    {
        _settings.ExcludedProcesses.RemoveAll(p => p.Equals(processName, StringComparison.OrdinalIgnoreCase));

        for (var i = HiddenProcesses.Count - 1; i >= 0; i--)
        {
            if (HiddenProcesses[i].Equals(processName, StringComparison.OrdinalIgnoreCase))
                HiddenProcesses.RemoveAt(i);
        }

        SettingsService.Save(_settings);
        RefreshAvailableSessions();
    }

    private void RefreshAvailableSessions()
    {
        var current = _audioManager.GetSessions()
            .Where(s => !_settings.ExcludedProcesses.Contains(s.ProcessName, StringComparer.OrdinalIgnoreCase))
            .ToList();

        for (var i = AvailableSessions.Count - 1; i >= 0; i--)
        {
            if (!current.Any(s => s.ProcessName.Equals(AvailableSessions[i].ProcessName, StringComparison.OrdinalIgnoreCase)))
                AvailableSessions.RemoveAt(i);
        }

        foreach (var session in current)
        {
            if (!AvailableSessions.Any(s => s.ProcessName.Equals(session.ProcessName, StringComparison.OrdinalIgnoreCase)))
                AvailableSessions.Add(session);
        }
    }

    partial void OnRefreshIntervalSecondsChanged(double value)
    {
        _refreshTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, value));
        _settings.RefreshIntervalSeconds = (int)value;
        SettingsService.Save(_settings);
    }

    partial void OnNavPaneWidthChanged(double value)
    {
        _settings.NavPaneWidth = value;
        SettingsService.Save(_settings);
    }

    public int InputModeIndex
    {
        get => (int)InputMode;
        set => InputMode = (InputMode)value;
    }

    public Microsoft.UI.Xaml.Visibility IsRotaryEncoder =>
        InputMode == InputMode.RotaryEncoder
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    partial void OnInputModeChanged(InputMode value)
    {
        _settings.InputMode = value;
        SettingsService.Save(_settings);
        OnPropertyChanged(nameof(InputModeIndex));
        OnPropertyChanged(nameof(IsRotaryEncoder));
    }

    partial void OnEncoderStepPercentChanged(double value)
    {
        _settings.EncoderStepPercent = (float)value;
        SettingsService.Save(_settings);
    }

    partial void OnDebugSerialEventsChanged(bool value)
    {
        _settings.DebugSerialEvents = value;
        SettingsService.Save(_settings);
        if (!value) SerialLog.Clear();
    }

    private void LogSerial(string message)
    {
        if (!DebugSerialEvents) return;
        _dispatcherQueue.TryEnqueue(() =>
        {
            SerialLog.Insert(0, $"[{DateTime.Now:HH:mm:ss.fff}] {message}");
            while (SerialLog.Count > 50)
                SerialLog.RemoveAt(SerialLog.Count - 1);
        });
    }

    // Arduino sends 1-based IDs like "knob1", "knob2"; channels are stored 0-based.
    private static int? ParseKnobIndex(string knobId)
    {
        var digits = new string(knobId.SkipWhile(c => !char.IsDigit(c)).ToArray());
        return int.TryParse(digits, out var n) ? n - 1 : null;
    }

    private void OnKnobChanged(string knobId, float normalized)
    {
        if (ParseKnobIndex(knobId) is not int index)
        {
            LogSerial($"{knobId} → {normalized:P0} [no index parsed]");
            return;
        }
        _dispatcherQueue.TryEnqueue(() =>
        {
            var channel = Channels.FirstOrDefault(c => c.KnobIndex == index);
            LogSerial(channel != null
                ? $"{knobId} → {normalized:P0} (index={index}, app={channel.AppName})"
                : $"{knobId} → {normalized:P0} [no channel at index {index}, channels={string.Join(",", Channels.Select(c => c.KnobIndex))}]");
            if (channel != null)
            {
                channel.Volume = normalized * 100;
                _serial.SendVolume(index, (float)(channel.Volume / 100.0));
            }
        });
    }

    private void OnKnobDelta(string knobId, int delta)
    {
        if (ParseKnobIndex(knobId) is not int index)
        {
            LogSerial($"{knobId} → {(delta > 0 ? "up" : "down")} [no index parsed]");
            return;
        }
        _dispatcherQueue.TryEnqueue(() =>
        {
            var channel = Channels.FirstOrDefault(c => c.KnobIndex == index);
            if (channel == null)
            {
                LogSerial($"{knobId} → {(delta > 0 ? "up" : "down")} [no channel at index {index}, channels={string.Join(",", Channels.Select(c => c.KnobIndex))}]");
                return;
            }
            var before = channel.Volume;
            var next = Math.Clamp(before + delta * EncoderStepPercent, 0, 100);
            channel.Volume = next;
            // Echo the resulting absolute level back so the device gauge tracks it.
            // (Encoders only send relative up/down; without this the display has
            // no source for the true volume — see knobs.cpp.)
            _serial.SendVolume(index, (float)(next / 100.0));
            var actual = _audioManager.GetVolume(channel.AppName) * 100;
            LogSerial($"{knobId} → {(delta > 0 ? "up" : "down")} | {before:F0}% → {next:F0}% (audio={actual:F0}%)");
        });
    }

    private void OnKnobPressed(string knobId)
    {
        if (ParseKnobIndex(knobId) is not int index)
        {
            LogSerial($"{knobId} → press [no index parsed]");
            return;
        }
        _dispatcherQueue.TryEnqueue(() =>
        {
            var channel = Channels.FirstOrDefault(c => c.KnobIndex == index);
            if (channel == null)
            {
                LogSerial($"{knobId} → press [no channel at index {index}]");
                return;
            }
            channel.ToggleMuteCommand.Execute(null);
            LogSerial($"{knobId} → press | {channel.AppName} muted={channel.IsMuted}");
        });
    }
}
