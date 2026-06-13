# Hidden Sources Management & Duplicate Channel-Assignment Guard — Design

**Date:** 2026-06-13
**Status:** Approved by user, pending implementation plan

## Goals

1. Add a central "Manage Sources" UI in Settings showing currently-visible audio
   sessions (each with a "Hide" action) and previously-hidden process names (each
   with a "Show"/unhide action) — today the only way to hide a process is the
   per-row button in the App Picker, and there's no way to unhide one short of
   editing `settings.json`.
2. Prevent the same process from being assigned to more than one channel: a
   channel's App Picker excludes processes already assigned to *other* channels,
   while still showing the channel's own current selection.
3. Block hiding a process that's currently assigned to any channel (including the
   channel doing the hiding), surfacing an in-app `InfoBar` notification explaining
   why, until the user unassigns it first.

## Current State (recap)

- `MainViewModel.AvailableSessions` is a single shared, live
  `ObservableCollection<AudioSession>`, refreshed on a timer via
  `RefreshAvailableSessions()`, which filters out anything in
  `_settings.ExcludedProcesses` (case-insensitive).
- `ChannelViewModel` holds a reference to this shared collection (passed in via
  constructor) and exposes it as `AvailableSessions` for `AppPickerDialog` to bind
  to directly — the *same* collection instance is shared by every channel and
  every open picker.
- `AppPickerDialog`'s `ListView` binds straight to that shared collection. Its
  per-row "Hide" `Button` calls `_channel.HideSession(session)` →
  `MainViewModel.HideSession(session)`, which unconditionally adds the process
  name to `_settings.ExcludedProcesses`, removes it from `AvailableSessions`, and
  persists — with no check against current channel assignments.
- `SettingsPage` has "Serial Connection" and "Audio Sessions" sections (the latter
  currently only has the refresh-interval `NumberBox`). There is no UI for viewing
  or editing `ExcludedProcesses`.
- Nothing today prevents two channels from ending up with the same `AppName`.

## Section 1 — `ChannelViewModel`: sibling awareness + selectable-sessions filter

Constructor gains a new parameter, the shared `Channels` collection from
`MainViewModel`, stored as `_channels`:

```csharp
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
```

Note `onHideSession` changes from `Action<AudioSession>` to
`Func<AudioSession, string?>` (see Section 2 — the hide guard lives in
`MainViewModel.HideSession`, which now returns a blocked-reason message or `null`
on success).

New members:

```csharp
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
```

`HideSession` becomes a thin passthrough — the guard check lives once, in
`MainViewModel.HideSession`, since `_channels` and `MainViewModel.Channels` are the
same collection instance. Requires adding `using System.Linq;`.

## Section 2 — `MainViewModel`: hidden-processes list, hide guard, unhide

New collection, initialized from persisted settings in the constructor:

```csharp
public ObservableCollection<string> HiddenProcesses { get; } = new();
```

In the constructor, right after `refreshIntervalSeconds = _settings.RefreshIntervalSeconds;`:

```csharp
foreach (var process in _settings.ExcludedProcesses)
    HiddenProcesses.Add(process);
```

`AddChannelInternal` passes `Channels` through to the new constructor parameter:

```csharp
private void AddChannelInternal(string appName, int? knobIndex = null, bool save = true)
{
    var index = knobIndex ?? (Channels.Count == 0 ? 0 : Channels.Max(c => c.KnobIndex) + 1);
    Channels.Add(new ChannelViewModel(index, appName, _audioManager, AvailableSessions, Channels, RemoveChannelInternal, SaveChannels, HideSession));

    if (save)
        SaveChannels();
}
```

`HideSession` changes from `void` to `string?` and gains the assignment guard:

```csharp
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
```

New `[RelayCommand]` for unhiding (no guard needed — unhiding is always allowed):

```csharp
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
```

`RefreshAvailableSessions()` re-adds the process to `AvailableSessions` if it's
currently running (existing diff logic, unchanged).

## Section 3 — `AppPickerDialog`: filtered list + blocked-hide notification

`AvailableSessions` (passthrough property to the shared collection) is replaced by
a constructor-built snapshot:

```csharp
public ObservableCollection<AudioSession> SelectableSessions { get; }

public AppPickerDialog(ChannelViewModel channel)
{
    _channel = channel;
    SelectableSessions = new ObservableCollection<AudioSession>(_channel.GetSelectableSessions());
    InitializeComponent();
}
```

`OnHideClick` handles the guard result:

```csharp
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
```

XAML: wrap the existing `ListView` in a `StackPanel` with an `InfoBar` above it,
and rebind `ItemsSource` from `AvailableSessions` to `SelectableSessions`:

```xml
<ContentDialog
    x:Class="AudioMixerWin.Core.Controls.AppPickerDialog"
    ...
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

`SelectedSession => SessionsList.SelectedItem as AudioSession?` is unchanged.

## Section 4 — `SettingsPage`: "Manage Sources" UI

`MainViewModel.HideSession` becomes `public` so `SettingsPage`'s code-behind can
call it directly (mirroring `AppPickerDialog`'s pattern rather than using
`[RelayCommand]`, since the blocked-reason return value needs to drive the
`InfoBar`).

Wrap the page's `StackPanel` in a `ScrollViewer` (new content below may exceed the
window height) and add `xmlns:models="using:AudioMixerWin.Core.Models"`:

```xml
<Page ... xmlns:models="using:AudioMixerWin.Core.Models">
    <ScrollViewer>
        <StackPanel Padding="24" Spacing="8" MaxWidth="400" HorizontalAlignment="Left">

            <!-- ... existing Serial Connection section unchanged ... -->

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

Code-behind (`SettingsPage.xaml.cs`) gains the same `OnHideClick` pattern as
`AppPickerDialog`, plus the `using` statements for `AudioSession`,
`Microsoft.UI.Xaml`, and `Microsoft.UI.Xaml.Controls` (the latter two are likely
already present):

```csharp
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
```

## Edge Cases

- **Hiding an app assigned to a channel** (from either the picker or Settings) is
  blocked; the `InfoBar` names the offending channel via `KnobLabel` (e.g. "Knob
  1"). The user must reassign that channel to a different app (or remove it) before
  the process can be hidden.
- **Unhiding a process that isn't currently running**: it leaves `HiddenProcesses`
  immediately but won't appear in `AvailableSessions`/pickers until the periodic
  refresh detects it running again — consistent with existing `AvailableSessions`
  behavior.
- **Pre-existing duplicate assignments** (two channels already sharing an `AppName`
  from before this change, or a hand-edited `settings.json`): out of scope. The new
  filter only affects what's offered going forward; no retroactive migration.
- **Freeing an assignment**: reassigning or removing a channel makes its old
  `AppName` selectable again next time any picker is opened, since
  `GetSelectableSessions()` is recomputed fresh on each `AppPickerDialog`
  construction.
- **Empty `SelectableSessions`**: if every running app is claimed by other
  channels, the picker's list is simply empty; Cancel / Remove Channel remain
  available — same as today's empty-list case.
- All process-name comparisons use `StringComparer.OrdinalIgnoreCase` /
  `StringComparison.OrdinalIgnoreCase`, consistent with existing code.

## Verification (manual)

No test project exists (per `CLAUDE.md`); this is tightly coupled to live NAudio
sessions and file I/O. Verify manually after implementation:

1. `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug` succeeds.
2. Settings → "Visible" list shows running sessions; clicking "Hide" on one *not*
   assigned to any channel moves it to "Hidden" and removes it from all pickers.
3. Settings → clicking "Hide" on a session that *is* assigned to a channel shows
   the `InfoBar` naming that channel, and the session stays in "Visible".
4. Settings → "Show" on a hidden entry moves it back to "Visible" (once running)
   and it reappears in pickers.
5. Mixer → assign Channel 1 to app X. Channel 2's picker no longer offers X.
   Channel 1's own picker still offers X (its current selection).
6. Mixer → in Channel 1's picker, clicking "Hide" on X (its own assignment) shows
   the `InfoBar` instead of hiding it.
7. Reassign Channel 1 away from X, then hide X from Settings — now allowed. X
   reappears in Channel 2's picker as available (still excluded as hidden until
   unhidden).
8. Restart app → hidden list and channel assignments persist correctly.
