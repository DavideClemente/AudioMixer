# Hidden Sources Management & Duplicate Channel Guard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prevent the same audio process from being assigned to two channels at once, and give the user a central "Manage Sources" UI in Settings to view, hide, and unhide audio sessions — with an in-app warning when the user tries to hide a process that's still assigned to a channel.

**Architecture:** `ChannelViewModel` gains a reference to the shared `Channels` collection and a `GetSelectableSessions()` filter (excludes processes already assigned to *other* channels) that `AppPickerDialog` binds to instead of the raw shared `AvailableSessions`. `MainViewModel.HideSession` gains an assignment guard — it now returns `string?` (a blocked-reason message, or `null` on success) instead of `void` — plus a new `HiddenProcesses` collection and `UnhideProcess` command. Both `AppPickerDialog` and `SettingsPage` gain an `InfoBar` that displays the blocked-reason message, and `SettingsPage` gains a "Manage Sources" section listing visible (Hide) and hidden (Show) processes.

**Tech Stack:** .NET 8, WinUI 3 (Windows App SDK, including the `InfoBar` control), CommunityToolkit.Mvvm, NAudio, System.Text.Json.

**Spec:** `docs/superpowers/specs/2026-06-13-hidden-sources-and-duplicate-guard-design.md`

---

## Important notes for the implementer

- Build command for every "build" step: `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug` (run from the repo root). Expected: `Build succeeded.` / `0 Error(s)`.
- There is no test project. Each task's verification is "build succeeds" plus a short manual run of the app (`dotnet build` produces `bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\AudioMixerWin.exe`, or run via the `AudioMixerWin (Unpackaged)` launch profile in Visual Studio/Rider).
- Settings file to inspect during manual checks: `%LocalAppData%\AudioMixerWin\settings.json`.
- Each task below shows the **full content** of every file it touches, reflecting the cumulative state after that task — you don't need to read earlier tasks to know what a file should contain at this point.

---

### Task 1: View-model changes — sibling-aware filtering, hidden-process tracking, hide guard

**Files:**
- Modify: `Core/ViewModels/ChannelViewModel.cs`
- Modify: `Core/ViewModels/MainViewModel.cs`

- [ ] **Step 1: Update `ChannelViewModel`**

`Core/ViewModels/ChannelViewModel.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using AudioMixerWin.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

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
    private string appName;

    [ObservableProperty]
    private double volume;

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
    }

    partial void OnAppNameChanged(string value)
    {
        Volume = _audioManager.GetVolume(value) * 100;
        _onSettingsChanged();
    }

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

    public void Remove() => _onRemove(this);
}
```

- [ ] **Step 2: Update `MainViewModel`**

`Core/ViewModels/MainViewModel.cs`:

```csharp
using System;
using System.Collections.ObjectModel;
using System.Linq;
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

        foreach (var process in _settings.ExcludedProcesses)
            HiddenProcesses.Add(process);

        foreach (var config in _settings.Channels)
            AddChannelInternal(config.AppName, config.KnobIndex, save: false);

        _serial = CreateAndStartSerial();

        RefreshAvailableSessions();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(RefreshIntervalSeconds) };
        _refreshTimer.Tick += (_, _) => RefreshAvailableSessions();
        _refreshTimer.Start();
    }

    private SerialManager CreateAndStartSerial()
    {
        var serial = new SerialManager(ComPort, BaudRate);
        serial.KnobChanged += OnKnobChanged;

        try
        {
            serial.Start();
            SerialStatus = $"Connected to {ComPort} @ {BaudRate}";
        }
        catch (Exception ex)
        {
            SerialStatus = $"Disconnected: {ex.Message}";
        }

        return serial;
    }

    [RelayCommand]
    private void Reconnect()
    {
        _serial.Stop();
        _serial = CreateAndStartSerial();

        _settings.ComPort = ComPort;
        _settings.BaudRate = BaudRate;
        SettingsService.Save(_settings);
    }

    [RelayCommand]
    private void AddChannel() => AddChannelInternal("Select App");

    private void AddChannelInternal(string appName, int? knobIndex = null, bool save = true)
    {
        var index = knobIndex ?? (Channels.Count == 0 ? 0 : Channels.Max(c => c.KnobIndex) + 1);
        Channels.Add(new ChannelViewModel(index, appName, _audioManager, AvailableSessions, Channels, RemoveChannelInternal, SaveChannels, HideSession));

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

    private void OnKnobChanged(int knobIndex, float normalized)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            var channel = Channels.FirstOrDefault(c => c.KnobIndex == knobIndex);
            if (channel != null)
                channel.Volume = normalized * 100;
        });
    }
}
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug`
Expected: `Build succeeded.` / `0 Error(s)`.

(`AppPickerDialog.xaml.cs`'s existing `OnHideClick` calls `_channel.HideSession(session)` as a discarded-result statement — valid C# even though the method now returns `string?`, so the build succeeds without touching that file yet.)

- [ ] **Step 4: Manual check — existing behavior still works**

Run the app. Confirm it launches normally, channels persist as before, and opening a channel's picker still shows the app list. Click "Hide" on a process that is **not** assigned to any channel — confirm it still disappears from the picker immediately (the new guard only blocks assigned processes; `HiddenProcesses`/`InfoBar` aren't wired into the UI yet, so no visible change beyond the existing hide behavior).

- [ ] **Step 5: Commit**

```bash
git add Core/ViewModels/ChannelViewModel.cs Core/ViewModels/MainViewModel.cs
git commit -m "Add duplicate-assignment guard and hidden-process tracking to view models"
```

---

### Task 2: `AppPickerDialog` — filtered list + blocked-hide notification

**Files:**
- Modify: `Core/Controls/AppPickerDialog.xaml`
- Modify: `Core/Controls/AppPickerDialog.xaml.cs`

- [ ] **Step 1: Update `AppPickerDialog.xaml`**

`Core/Controls/AppPickerDialog.xaml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<ContentDialog
    x:Class="AudioMixerWin.Core.Controls.AppPickerDialog"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:models="using:AudioMixerWin.Core.Models"
    xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
    mc:Ignorable="d"
    Title="Select App"
    PrimaryButtonText="Select"
    SecondaryButtonText="Remove Channel"
    CloseButtonText="Cancel">

    <StackPanel Spacing="8">
        <InfoBar x:Name="HideInfoBar" Severity="Warning" IsOpen="False" IsClosable="True" />

        <ListView x:Name="SessionsList" ItemsSource="{x:Bind SelectableSessions}" SelectionMode="Single">
            <ListView.ItemTemplate>
                <DataTemplate x:DataType="models:AudioSession">
                    <Grid ColumnSpacing="8">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*" />
                            <ColumnDefinition Width="Auto" />
                        </Grid.ColumnDefinitions>
                        <TextBlock Text="{x:Bind ProcessName}" VerticalAlignment="Center" />
                        <Button Grid.Column="1" Content="Hide" Tag="{x:Bind}" Click="OnHideClick" />
                    </Grid>
                </DataTemplate>
            </ListView.ItemTemplate>
        </ListView>
    </StackPanel>
</ContentDialog>
```

- [ ] **Step 2: Update `AppPickerDialog.xaml.cs`**

`Core/Controls/AppPickerDialog.xaml.cs`:

```csharp
using System.Collections.ObjectModel;
using AudioMixerWin.Core.Models;
using AudioMixerWin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AudioMixerWin.Core.Controls;

public sealed partial class AppPickerDialog : ContentDialog
{
    private readonly ChannelViewModel _channel;

    public ObservableCollection<AudioSession> SelectableSessions { get; }

    public AudioSession? SelectedSession => SessionsList.SelectedItem as AudioSession;

    public AppPickerDialog(ChannelViewModel channel)
    {
        _channel = channel;
        SelectableSessions = new ObservableCollection<AudioSession>(_channel.GetSelectableSessions());
        InitializeComponent();
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
        }
        else
        {
            SelectableSessions.Remove(session);
        }
    }
}
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug`
Expected: `Build succeeded.` / `0 Error(s)`.

- [ ] **Step 4: Manual check — duplicate guard + blocked-hide notification**

Run the app. Add two channels (Knob 1, Knob 2) if you don't have at least two already. Assign Knob 1 to some running app X via its picker.

1. Open Knob 2's picker — confirm X does **not** appear in the list.
2. Open Knob 1's picker — confirm X **does** appear (its own current selection).
3. In Knob 1's picker, click "Hide" next to X — confirm an orange `InfoBar` appears saying `Can't hide 'X' — it's assigned to Knob 1. Unassign it first.`, and X remains in the list.
4. In the same picker, click "Hide" next to a different process that isn't assigned to any channel — confirm that row disappears immediately (existing hide behavior still works via `SelectableSessions`).
5. Reassign Knob 1 to a different app, then reopen Knob 2's picker — confirm X now appears as available.

- [ ] **Step 5: Commit**

```bash
git add Core/Controls/AppPickerDialog.xaml Core/Controls/AppPickerDialog.xaml.cs
git commit -m "Filter app picker by channel assignment and warn on blocked hide"
```

---

### Task 3: `SettingsPage` — "Manage Sources" section

**Files:**
- Modify: `Core/Views/SettingsPage.xaml`
- Modify: `Core/Views/SettingsPage.xaml.cs`

- [ ] **Step 1: Update `SettingsPage.xaml`**

`Core/Views/SettingsPage.xaml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Page
    x:Class="AudioMixerWin.Core.Views.SettingsPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:models="using:AudioMixerWin.Core.Models"
    xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
    mc:Ignorable="d">

    <ScrollViewer>
        <StackPanel Padding="24" Spacing="8" MaxWidth="400" HorizontalAlignment="Left">

            <TextBlock Text="Serial Connection" FontSize="20" FontWeight="SemiBold" Margin="0,0,0,8" />

            <TextBlock Text="COM Port" />
            <ComboBox
                IsEditable="True"
                ItemsSource="{x:Bind PortNames}"
                Text="{x:Bind ViewModel.ComPort, Mode=TwoWay}"
                HorizontalAlignment="Stretch" />

            <TextBlock Text="Baud Rate" Margin="0,12,0,0" />
            <ComboBox
                ItemsSource="{x:Bind BaudRates}"
                SelectedItem="{x:Bind ViewModel.BaudRate, Mode=TwoWay}"
                HorizontalAlignment="Stretch" />

            <Button Content="Reconnect" Command="{x:Bind ViewModel.ReconnectCommand}" Margin="0,16,0,0" />

            <TextBlock Text="{x:Bind ViewModel.SerialStatus, Mode=OneWay}" Opacity="0.7" Margin="0,8,0,0" />

            <TextBlock Text="Audio Sessions" FontSize="20" FontWeight="SemiBold" Margin="0,24,0,8" />

            <TextBlock Text="Refresh Interval (seconds)" />
            <NumberBox
                Value="{x:Bind ViewModel.RefreshIntervalSeconds, Mode=TwoWay}"
                Minimum="1"
                Maximum="30"
                SpinButtonPlacementMode="Inline"
                HorizontalAlignment="Stretch" />

            <TextBlock Text="Visible (tap Hide to exclude from app pickers)" Opacity="0.7" Margin="0,16,0,0" />
            <InfoBar x:Name="HideInfoBar" Severity="Warning" IsOpen="False" IsClosable="True" />
            <ListView ItemsSource="{x:Bind ViewModel.AvailableSessions}" MaxHeight="200">
                <ListView.ItemTemplate>
                    <DataTemplate x:DataType="models:AudioSession">
                        <Grid ColumnSpacing="8">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="Auto" />
                            </Grid.ColumnDefinitions>
                            <TextBlock Text="{x:Bind ProcessName}" VerticalAlignment="Center" />
                            <Button Grid.Column="1" Content="Hide" Tag="{x:Bind}" Click="OnHideClick" />
                        </Grid>
                    </DataTemplate>
                </ListView.ItemTemplate>
            </ListView>

            <TextBlock Text="Hidden (tap Show to restore)" Opacity="0.7" Margin="0,12,0,0" />
            <ListView ItemsSource="{x:Bind ViewModel.HiddenProcesses}" MaxHeight="200">
                <ListView.ItemTemplate>
                    <DataTemplate x:DataType="x:String">
                        <Grid ColumnSpacing="8">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="Auto" />
                            </Grid.ColumnDefinitions>
                            <TextBlock Text="{x:Bind}" VerticalAlignment="Center" />
                            <Button Grid.Column="1" Content="Show" Command="{x:Bind ViewModel.UnhideProcessCommand}" CommandParameter="{x:Bind}" />
                        </Grid>
                    </DataTemplate>
                </ListView.ItemTemplate>
            </ListView>

        </StackPanel>
    </ScrollViewer>
</Page>
```

- [ ] **Step 2: Update `SettingsPage.xaml.cs`**

`Core/Views/SettingsPage.xaml.cs`:

```csharp
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
}
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug`
Expected: `Build succeeded.` / `0 Error(s)`.

- [ ] **Step 4: Manual check — Manage Sources UI**

Run the app and open Settings.

1. Confirm the new "Visible" list shows currently-running audio sessions (e.g. start playing audio in a browser if the list is empty), and "Hidden" is empty (assuming a clean settings file).
2. Click "Hide" on a session that is **not** assigned to any channel — confirm it disappears from "Visible" and appears in "Hidden".
3. Click "Show" on that entry in "Hidden" — confirm it disappears from "Hidden" and reappears in "Visible" (since it's still running).
4. Assign a channel to some app X (Mixer page), return to Settings, and click "Hide" next to X in "Visible" — confirm the `InfoBar` warning appears naming that channel (e.g. "Knob 1"), and X stays in "Visible".
5. Restart the app — confirm the "Hidden" list still shows whatever you left hidden in step 2/3, and `%LocalAppData%\AudioMixerWin\settings.json`'s `excludedProcesses` matches.

- [ ] **Step 5: Commit**

```bash
git add Core/Views/SettingsPage.xaml Core/Views/SettingsPage.xaml.cs
git commit -m "Add Manage Sources section to Settings for hiding/unhiding audio sessions"
```

---

### Task 4: Full manual verification pass

**Files:** none (verification only)

- [ ] **Step 1: Build and launch**

Run: `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug`, then launch `bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\AudioMixerWin.exe` (or use the `AudioMixerWin (Unpackaged)` launch profile).

- [ ] **Step 2: Duplicate-assignment guard, end to end**

Create two channels and assign Knob 1 to app X. Confirm Knob 2's picker excludes X while Knob 1's own picker still shows X. Reassign Knob 1 to a different app and confirm X becomes available in Knob 2's picker again. Remove a channel entirely and confirm its previously-assigned app becomes available in the remaining channels' pickers.

- [ ] **Step 3: Hide-blocked notification, both surfaces**

With Knob 1 assigned to app X: in Knob 1's picker, click "Hide" on X and confirm the `InfoBar` warning appears and X is not hidden. In Settings' "Visible" list, click "Hide" on X and confirm the same warning appears there too.

- [ ] **Step 4: Hide/unhide round trip via Settings**

In Settings, hide a process that is **not** assigned to any channel (it moves to "Hidden"). Confirm it no longer appears in any channel's picker. Click "Show" on it in "Hidden" and confirm it reappears in "Visible" and in pickers (assuming it's still running).

- [ ] **Step 5: Persistence round trip**

Hide one process and assign channels to a couple of apps. Close the app entirely, then relaunch it. Confirm: the hidden process still appears in Settings' "Hidden" list, the channel assignments are unchanged, and the duplicate guard still applies correctly to the restored channels (re-run a quick check from Step 2).

- [ ] **Step 6: Reconnect regression check**

On the Settings page, click "Reconnect". Open `%LocalAppData%\AudioMixerWin\settings.json` and confirm `channels` and `excludedProcesses` are still present and unchanged.

- [ ] **Step 7: Final commit (if any fixes were needed)**

If verification surfaced any issues, fix them and commit as a follow-up. No additional commit is needed if all checks pass as-is.
