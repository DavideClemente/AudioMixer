using System;
using System.Collections.ObjectModel;
using System.Linq;
using AudioMixerWin.Core.Models;
using AudioMixerWin.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioMixerWin.Core.ViewModels;

public enum OutputPosition { None, A, B }

public partial class OutputViewModel : ObservableObject
{
    // Segoe MDL2 Assets: E7F6 = Headphone, E7F5 = Speakers.
    private const int HeadphoneGlyph = 0xE7F6;
    private const int SpeakerGlyph = 0xE7F5;

    private readonly AppSettings _settings;
    private readonly OutputManager _output;
    private readonly Action _save;

    // Guards against the change handlers re-entering while RebuildExclusions is
    // reassigning the selections (clearing a combo's list transiently nulls its
    // bound SelectedItem, which would otherwise persist/re-filter mid-rebuild).
    private bool _syncing;

    // Master list of every active endpoint. The two pickers bind to the filtered
    // lists below instead, each of which omits the *other* position's selection
    // so the same device can never be assigned to both positions.
    public ObservableCollection<OutputDevice> Devices { get; } = new();
    public ObservableCollection<OutputDevice> DevicesForA { get; } = new();
    public ObservableCollection<OutputDevice> DevicesForB { get; } = new();

    [ObservableProperty]
    private OutputDevice? selectedDeviceA;

    [ObservableProperty]
    private OutputDevice? selectedDeviceB;

    [ObservableProperty]
    private OutputPosition activePosition;

    [ObservableProperty]
    private string statusText = "";

    public bool IsAActive => ActivePosition == OutputPosition.A;
    public bool IsBActive => ActivePosition == OutputPosition.B;

    public string ADeviceName => SelectedDeviceA?.Name ?? "Not assigned";
    public string BDeviceName => SelectedDeviceB?.Name ?? "Not assigned";

    // Pick the glyph from the assigned device's name rather than the position, so a
    // headphone endpoint always shows the headphone icon whichever slot it's in.
    public string AIconGlyph => GlyphFor(SelectedDeviceA);
    public string BIconGlyph => GlyphFor(SelectedDeviceB);

    private static string GlyphFor(OutputDevice? device)
    {
        if (device is null)
            return char.ConvertFromUtf32(SpeakerGlyph);

        var name = device.Name.ToLowerInvariant();
        var isHeadset = name.Contains("head") || name.Contains("phone")
            || name.Contains("earbud") || name.Contains("earphone") || name.Contains("airpod");
        return char.ConvertFromUtf32(isHeadset ? HeadphoneGlyph : SpeakerGlyph);
    }

    public OutputViewModel(AppSettings settings, OutputManager output, Action save)
    {
        _settings = settings;
        _output = output;
        _save = save;

        LoadDevices();
    }

    // Polled on the UI thread at the same cadence as the mixer's session list, so
    // hot-plugged or removed outputs appear/disappear on their own. Merges in place
    // (rather than clearing) so a steady-state tick doesn't disturb an open dropdown
    // or the current selections — only a real device change touches the lists.
    public void RefreshDevices()
    {
        var current = _output.GetOutputDevices();
        var currentIds = current.Select(d => d.Id).ToHashSet();
        var changed = false;

        for (var i = Devices.Count - 1; i >= 0; i--)
        {
            if (!currentIds.Contains(Devices[i].Id))
            {
                Devices.RemoveAt(i);
                changed = true;
            }
        }

        foreach (var device in current)
        {
            var existing = Devices.FirstOrDefault(d => d.Id == device.Id);
            if (existing is null)
            {
                Devices.Add(device);
                changed = true;
            }
            else
            {
                existing.IsDefault = device.IsDefault;
            }
        }

        // Rebuild the per-position lists only on an actual add/remove; this also
        // drops a selection whose device was unplugged (it's no longer in the list).
        if (changed)
            RebuildExclusions();

        SyncActiveFromDefault();
    }

    private void LoadDevices()
    {
        var current = _output.GetOutputDevices();

        Devices.Clear();
        foreach (var device in current)
            Devices.Add(device);

        // Set the backing fields directly so re-binding the saved selections on
        // load doesn't trip the change handlers (which persist and re-route).
        selectedDeviceA = Devices.FirstOrDefault(d => d.Id == _settings.OutputDeviceAId);
        selectedDeviceB = Devices.FirstOrDefault(d => d.Id == _settings.OutputDeviceBId);
        OnPropertyChanged(nameof(SelectedDeviceA));
        OnPropertyChanged(nameof(SelectedDeviceB));

        RebuildExclusions();
        SyncActiveFromDefault();
    }

    // Repopulates each picker's list to exclude the device chosen for the other
    // position, then restores (or clears, if it collided) each selection. Because
    // a position's own device is never removed from its own list, the bound
    // SelectedItem stays valid. Persists the result so a forced clear sticks.
    private void RebuildExclusions()
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            var keepA = SelectedDeviceA;
            var keepB = SelectedDeviceB;

            DevicesForA.Clear();
            foreach (var d in Devices)
                if (keepB is null || d.Id != keepB.Id)
                    DevicesForA.Add(d);

            DevicesForB.Clear();
            foreach (var d in Devices)
                if (keepA is null || d.Id != keepA.Id)
                    DevicesForB.Add(d);

            SelectedDeviceA = keepA is null ? null : DevicesForA.FirstOrDefault(d => d.Id == keepA.Id);
            SelectedDeviceB = keepB is null ? null : DevicesForB.FirstOrDefault(d => d.Id == keepB.Id);
        }
        finally
        {
            _syncing = false;
        }

        _settings.OutputDeviceAId = SelectedDeviceA?.Id;
        _settings.OutputDeviceBId = SelectedDeviceB?.Id;
        _save();

        OnPropertyChanged(nameof(ADeviceName));
        OnPropertyChanged(nameof(BDeviceName));
        OnPropertyChanged(nameof(AIconGlyph));
        OnPropertyChanged(nameof(BIconGlyph));
    }

    // Tapping a position card (manual override). Re-selecting the position that's
    // already live is a no-op — re-routing to the current default would cause an
    // audible blip for no reason.
    [RelayCommand]
    private void ActivateA()
    {
        if (ActivePosition == OutputPosition.A) return;
        Activate(OutputPosition.A);
    }

    [RelayCommand]
    private void ActivateB()
    {
        if (ActivePosition == OutputPosition.B) return;
        Activate(OutputPosition.B);
    }

    // Driven by the hardware switch. Same no-op guard: if we're already on that
    // position the default already matches, so don't re-route.
    public void ApplySwitchPosition(int position)
    {
        var target = position == 0 ? OutputPosition.A : OutputPosition.B;
        if (ActivePosition == target) return;
        Activate(target);
    }

    private void Activate(OutputPosition position)
    {
        var device = position == OutputPosition.A ? SelectedDeviceA : SelectedDeviceB;
        var label = position == OutputPosition.A ? "A" : "B";

        if (device is null)
        {
            StatusText = $"Assign a device to position {label} first.";
            return;
        }

        if (_output.SetDefault(device.Id))
        {
            ActivePosition = position;
            StatusText = $"Output → {device.Name}";
        }
        else
        {
            StatusText = $"Couldn't switch to {device.Name} — it may be disconnected.";
        }

        RefreshDefaults();
    }

    // Derives the active position from whichever assigned device is currently the
    // system default, so the UI reflects routing changed outside the app too.
    private void SyncActiveFromDefault()
    {
        var def = _output.DefaultDeviceId;
        if (def is not null && def == _settings.OutputDeviceAId)
            ActivePosition = OutputPosition.A;
        else if (def is not null && def == _settings.OutputDeviceBId)
            ActivePosition = OutputPosition.B;
        else
            ActivePosition = OutputPosition.None;

        RefreshDefaults();
    }

    private void RefreshDefaults()
    {
        var def = _output.DefaultDeviceId;
        foreach (var device in Devices)
            device.IsDefault = device.Id == def;
    }

    partial void OnSelectedDeviceAChanged(OutputDevice? value)
    {
        if (_syncing) return;
        RebuildExclusions();
        if (ActivePosition == OutputPosition.A)
            Activate(OutputPosition.A);
    }

    partial void OnSelectedDeviceBChanged(OutputDevice? value)
    {
        if (_syncing) return;
        RebuildExclusions();
        if (ActivePosition == OutputPosition.B)
            Activate(OutputPosition.B);
    }

    partial void OnActivePositionChanged(OutputPosition value)
    {
        OnPropertyChanged(nameof(IsAActive));
        OnPropertyChanged(nameof(IsBActive));
    }
}
