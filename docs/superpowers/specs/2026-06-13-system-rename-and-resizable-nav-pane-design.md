# System Channel Rename & Resizable Navigation Pane — Design

**Date:** 2026-06-13
**Status:** Approved by user, pending implementation plan

## Goals

1. Wherever the "System Volume" master-volume channel is shown to the user (channel
   card title, app picker, Settings' "Manage Sources" lists), display it as
   "🔊 System" instead of "System Volume". The internal identifier
   (`AudioManager.MasterVolumeProcessName = "System Volume"`, used for
   `GetVolume`/`SetVolume` matching and persisted in `settings.json` as a channel's
   `AppName`) is unchanged.
2. Make the `NavigationView` pane in `MainWindow.xaml` (the "Mixer"/"Settings"
   sidebar) horizontally resizable by dragging its right edge, within a 200–400px
   range, with the chosen width persisted across restarts.

## Current State (recap)

- `AudioManager.GetSessions()` builds `AudioSession` objects with `ProcessName` and
  `DisplayName` set to the same value (`process.ProcessName` for real processes,
  `MasterVolumeProcessName` for the synthetic master-volume entry). `DisplayName`
  is therefore currently redundant.
- `KnobCard.xaml` binds its title to `Channel.AppName` (a `ChannelViewModel`
  `[ObservableProperty]` that holds the raw process name and is used directly for
  `AudioManager.GetVolume`/`SetVolume` and persisted via `ChannelConfig.AppName`).
- `AppPickerDialog.xaml` and `SettingsPage.xaml`'s "Visible" `ListView` both bind
  their row text to `{x:Bind ProcessName}`. `SettingsPage.xaml`'s "Hidden"
  `ListView` binds directly to `{x:Bind}` on `ObservableCollection<string>
  HiddenProcesses` (raw process names).
- `MainWindow.xaml` is a `Window` containing a single `NavigationView` with
  `MenuItems` ("Mixer") and the built-in "Settings" footer item, hosting
  `ContentFrame`. `OpenPaneLength` is left at its default (320px) and has no resize
  affordance.
- `AppSettings` (`Core/Services/AppSettings.cs`) persists `ComPort`, `BaudRate`,
  `RefreshIntervalSeconds`, `Channels`, `ExcludedProcesses` via `SettingsService`.
  `MainViewModel` mirrors several of these as `[ObservableProperty]`s with
  `On...Changed` partial methods that save back to `AppSettings`.

## Section 1 — "🔊 System" display name

### `AudioManager.cs` (global namespace, no `namespace` declaration)

Add a static helper next to `MasterVolumeProcessName`:

```csharp
public const string MasterVolumeProcessName = "System Volume";

public static string GetDisplayName(string processName) =>
    processName.Equals(MasterVolumeProcessName, StringComparison.OrdinalIgnoreCase)
        ? "🔊 System"
        : processName;
```

In `GetSessions()`, change both places that set `DisplayName`:

```csharp
result.Add(new AudioSession
{
    ProcessName = process.ProcessName,
    DisplayName = GetDisplayName(process.ProcessName), // was: process.ProcessName
    Volume = session.SimpleAudioVolume.Volume,
    IconSource = GetIconForProcess(process),
});
...
result.Add(new AudioSession
{
    ProcessName = MasterVolumeProcessName,
    DisplayName = GetDisplayName(MasterVolumeProcessName), // was: MasterVolumeProcessName
    Volume = GetMasterVolume(),
});
```

(`GetDisplayName` is a no-op for every real process — `process.ProcessName` never
equals `"System Volume"` — so this is safe to apply unconditionally.)

### `ChannelViewModel.cs`

```csharp
[ObservableProperty]
[NotifyPropertyChangedFor(nameof(DisplayName))]
private string appName;

public string DisplayName => AudioManager.GetDisplayName(AppName);
```

(`AudioManager` is already referenced unqualified elsewhere in this file, e.g. the
`_audioManager` field — it lives in the global namespace, so no new `using` is
needed.) `[NotifyPropertyChangedFor]` comes from `CommunityToolkit.Mvvm.ComponentModel`,
already imported.

### `KnobCard.xaml`

```xml
<TextBlock
    Text="{x:Bind Channel.DisplayName, Mode=OneWay}"
    ...
```
(was `Channel.AppName`)

### `AppPickerDialog.xaml`

```xml
<TextBlock Text="{x:Bind DisplayName}" VerticalAlignment="Center" />
```
(was `{x:Bind ProcessName}`, inside the `SelectableSessions` `DataTemplate
x:DataType="models:AudioSession"`)

### `SettingsPage.xaml`

"Visible" list (`AvailableSessions`, same `AudioSession` template):

```xml
<TextBlock Text="{x:Bind DisplayName}" VerticalAlignment="Center" />
```

"Hidden" list (`HiddenProcesses`, `ObservableCollection<string>`) — add a new
converter since there's no `AudioSession.DisplayName` to bind to:

`Core/Converters/ProcessNameDisplayConverter.cs` (new file, new
`AudioMixerWin.Core.Converters` namespace):

```csharp
using System;
using Microsoft.UI.Xaml.Data;

namespace AudioMixerWin.Core.Converters;

public class ProcessNameDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is string processName ? AudioManager.GetDisplayName(processName) : value;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
```

`SettingsPage.xaml`: add the namespace and resource, apply to the Hidden list's
`TextBlock`:

```xml
<Page ...
    xmlns:converters="using:AudioMixerWin.Core.Converters">

    <Page.Resources>
        <converters:ProcessNameDisplayConverter x:Key="ProcessNameDisplay" />
    </Page.Resources>

    ...
    <TextBlock Text="{x:Bind Path=., Converter={StaticResource ProcessNameDisplay}}" VerticalAlignment="Center" />
```

## Section 2 — Resizable navigation pane

### `AppSettings.cs`

```csharp
public double NavPaneWidth { get; set; } = 320;
```

### `MainViewModel.cs`

```csharp
[ObservableProperty]
private double navPaneWidth;
```

Initialize in the constructor alongside the other settings-backed fields:

```csharp
navPaneWidth = _settings.NavPaneWidth;
```

Add a partial method that persists, mirroring `OnRefreshIntervalSecondsChanged`:

```csharp
partial void OnNavPaneWidthChanged(double value)
{
    _settings.NavPaneWidth = value;
    SettingsService.Save(_settings);
}
```

### `MainWindow.xaml`

Wrap the existing `NavigationView` and a new splitter `Border` in a `Grid`:

```xml
<Grid>
    <NavigationView
        x:Name="NavView"
        IsBackButtonVisible="Collapsed"
        SelectionChanged="OnNavSelectionChanged">
        <!-- existing MenuItems / Frame, unchanged -->
    </NavigationView>

    <Border
        x:Name="PaneSplitter"
        Width="6"
        HorizontalAlignment="Left"
        Background="Transparent"
        PointerEntered="OnSplitterPointerEntered"
        PointerExited="OnSplitterPointerExited"
        PointerPressed="OnSplitterPointerPressed"
        PointerMoved="OnSplitterPointerMoved"
        PointerReleased="OnSplitterPointerReleased" />
</Grid>
```

The `Border`'s `Background` toggles between `Transparent` and a subtle highlight
brush on `PointerEntered`/`PointerExited` (while not dragging) to indicate it's
draggable.

### `MainWindow.xaml.cs`

```csharp
private const double MinPaneWidth = 200;
private const double MaxPaneWidth = 400;
private bool _isDraggingSplitter;
private double _dragStartX;
private double _dragStartWidth;

public MainWindow()
{
    InitializeComponent();

    _mainPage = new MainPage(ViewModel);
    _settingsPage = new SettingsPage(ViewModel);
    ContentFrame.Content = _mainPage;

    NavView.OpenPaneLength = ViewModel.NavPaneWidth;
    PositionSplitter(ViewModel.NavPaneWidth);
}

private void PositionSplitter(double paneWidth) =>
    PaneSplitter.Margin = new Thickness(paneWidth - PaneSplitter.Width / 2, 0, 0, 0);

private void OnSplitterPointerEntered(object sender, PointerRoutedEventArgs e) =>
    PaneSplitter.Background = new SolidColorBrush(Colors.Gray) { Opacity = 0.3 };

private void OnSplitterPointerExited(object sender, PointerRoutedEventArgs e)
{
    if (!_isDraggingSplitter)
        PaneSplitter.Background = new SolidColorBrush(Colors.Transparent);
}

private void OnSplitterPointerPressed(object sender, PointerRoutedEventArgs e)
{
    _isDraggingSplitter = true;
    _dragStartX = e.GetCurrentPoint(Content).Position.X;
    _dragStartWidth = NavView.OpenPaneLength;
    PaneSplitter.CapturePointer(e.Pointer);
}

private void OnSplitterPointerMoved(object sender, PointerRoutedEventArgs e)
{
    if (!_isDraggingSplitter)
        return;

    var currentX = e.GetCurrentPoint(Content).Position.X;
    var newWidth = Math.Clamp(_dragStartWidth + (currentX - _dragStartX), MinPaneWidth, MaxPaneWidth);
    NavView.OpenPaneLength = newWidth;
    PositionSplitter(newWidth);
}

private void OnSplitterPointerReleased(object sender, PointerRoutedEventArgs e)
{
    _isDraggingSplitter = false;
    PaneSplitter.ReleasePointerCapture(e.Pointer);
    PaneSplitter.Background = new SolidColorBrush(Colors.Transparent);
    ViewModel.NavPaneWidth = NavView.OpenPaneLength;
}
```

No custom resize cursor — `Border` doesn't expose `ProtectedCursor` to
`MainWindow`'s code-behind without a custom control subclass, and the hover
highlight is sufficient affordance for this small change.

## Edge Cases

- **Pane in compact/collapsed mode** (`NavigationView` toggled to icon-only via the
  hamburger button): `OpenPaneLength` still applies once the pane is reopened; the
  splitter remains at its last position and is harmless to drag while collapsed
  (it just changes the width the pane will reopen to).
- **Window narrower than `MinPaneWidth` + content minimum**: not specifically
  guarded — `NavigationView`'s own `PaneDisplayMode="Auto"` responsive collapse for
  narrow windows is unaffected.
- **A real process literally named "System Volume"**: not a valid Windows process
  name, not guarded.
- **`HiddenProcesses` containing `"System Volume"`**: if a user ever hides the
  master-volume entry, the Hidden list shows "🔊 System" via the new converter,
  consistent with everywhere else it's displayed.

## Verification (manual)

No test project exists (per `CLAUDE.md`).

1. `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug` succeeds.
2. Assign a channel to the system volume entry — its card title reads "🔊 System"
   (not "System Volume"), and its slider still controls the OS master volume.
3. Open that channel's app picker — the system entry is listed as "🔊 System".
4. Settings → "Manage Sources" — the system entry shows "🔊 System" in the Visible
   list (or Hidden list, if hidden).
5. Drag the nav-pane splitter left/right — the "Mixer"/"Settings" pane resizes
   live, clamped to 200–400px.
6. Restart the app — the pane reopens at the last dragged width.
