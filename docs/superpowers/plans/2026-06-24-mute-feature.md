# Mute Feature Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add per-channel mute to every KnobCard, triggered by a UI button and by pressing the rotary encoder's built-in push button on the Arduino.

**Architecture:** Mute uses the Windows `SimpleAudioVolume.Mute` flag so volume stays at its current level. `ChannelViewModel` owns `IsMuted` and a `ToggleMuteCommand`. `SerialManager` fires a new `KnobPressed` event on `:press` tokens. `MainViewModel` wires the event to the matching channel's command. Mute state is session-only (not persisted).

**Tech Stack:** WinUI 3 / .NET 8, CommunityToolkit.Mvvm, NAudio.CoreAudioApi, Arduino C++ with `millis()`-based button debounce.

## Global Constraints

- Build command: `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug`
- No test project exists — verification is build success + manual run
- Platform must be passed: `-p:Platform=x64` (no AnyCPU)
- `x:Bind` function-call syntax is already established in `KnobCard.xaml` (`FormatPercent`); follow that pattern

## File Map

| File | Action | Responsibility |
|---|---|---|
| `Core/SerialManager.cs` | Modify | Add `KnobPressed` event, handle `:press` payload |
| `Core/AudioManager.cs` | Modify | Add `GetMute` / `SetMute` for app sessions and master volume |
| `Core/ViewModels/ChannelViewModel.cs` | Modify | Add `IsMuted`, `OnIsMutedChanged`, `ToggleMuteCommand` |
| `Core/ViewModels/MainViewModel.cs` | Modify | Subscribe to `KnobPressed`, implement `OnKnobPressed`, unsubscribe on reconnect |
| `Core/Controls/KnobCard.xaml` | Modify | Add mute button, dim slider when muted |
| `Core/Controls/KnobCard.xaml.cs` | Modify | Add `FormatMuteIcon` and `ConvertMuteToOpacity` helpers |
| `Arduino/arduino/arduino.ino` | Modify | Add `swPin` to `EncConfig`, debounce button, send `:press` |

---

### Task 1: SerialManager — add KnobPressed event

**Files:**
- Modify: `Core/SerialManager.cs`

**Interfaces:**
- Produces: `event Action<string>? KnobPressed` — fired with the raw knob ID string (e.g. `"knob1"`) when payload is `"press"`

- [ ] **Step 1: Add the event declaration and `:press` branch**

Open `Core/SerialManager.cs`. After the existing `KnobDelta` event declaration (line 13), add:

```csharp
public event Action<string>? KnobPressed;
```

In `HandleCommand`, after the `else if (payload == "down")` branch, add:

```csharp
else if (payload == "press")
    KnobPressed?.Invoke(knobId);
```

The full `HandleCommand` method should now read:

```csharp
private void HandleCommand(string cmd)
{
    var parts = cmd.Split(':');
    if (parts.Length != 2)
        return;

    var knobId  = parts[0].Trim();
    var payload = parts[1].Trim();

    if (payload == "up")
        KnobDelta?.Invoke(knobId, +1);
    else if (payload == "down")
        KnobDelta?.Invoke(knobId, -1);
    else if (payload == "press")
        KnobPressed?.Invoke(knobId);
    else if (float.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        KnobChanged?.Invoke(knobId, Math.Clamp(value, 0f, 1f));
}
```

- [ ] **Step 2: Build to verify**

```
dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug
```

Expected: Build succeeded, 0 Error(s).

- [ ] **Step 3: Commit**

```
git add Core/SerialManager.cs
git commit -m "feat: add KnobPressed event to SerialManager for encoder button"
```

---

### Task 2: AudioManager — GetMute / SetMute

**Files:**
- Modify: `Core/AudioManager.cs`

**Interfaces:**
- Consumes: existing `_device`, `MasterVolumeProcessName`, session enumeration pattern already used in `GetVolume`/`SetVolume`
- Produces:
  - `public bool GetMute(string processName)` — returns `false` if session not found
  - `public void SetMute(string processName, bool muted)`

- [ ] **Step 1: Add GetMute**

In `Core/AudioManager.cs`, after the `GetVolume` method, add:

```csharp
public bool GetMute(string processName)
{
    if (processName.Equals(MasterVolumeProcessName, StringComparison.OrdinalIgnoreCase))
        return _device.AudioEndpointVolume.Mute;

    var sessions = _device.AudioSessionManager.Sessions;

    for (var i = 0; i < sessions.Count; i++)
    {
        try
        {
            var session = sessions[i];
            var pid = (int)session.GetProcessID;
            var process = Process.GetProcessById(pid);

            if (process.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
                return session.SimpleAudioVolume.Mute;
        }
        catch { }
    }

    return false;
}
```

- [ ] **Step 2: Add SetMute**

After `GetMute`, add:

```csharp
public void SetMute(string processName, bool muted)
{
    if (processName.Equals(MasterVolumeProcessName, StringComparison.OrdinalIgnoreCase))
    {
        _device.AudioEndpointVolume.Mute = muted;
        return;
    }

    var sessions = _device.AudioSessionManager.Sessions;

    for (var i = 0; i < sessions.Count; i++)
    {
        try
        {
            var session = sessions[i];
            var pid = (int)session.GetProcessID;
            var process = Process.GetProcessById(pid);

            if (process.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
                session.SimpleAudioVolume.Mute = muted;
        }
        catch { }
    }
}
```

- [ ] **Step 3: Build to verify**

```
dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug
```

Expected: Build succeeded, 0 Error(s).

- [ ] **Step 4: Commit**

```
git add Core/AudioManager.cs
git commit -m "feat: add GetMute/SetMute to AudioManager"
```

---

### Task 3: ChannelViewModel — IsMuted and ToggleMuteCommand

**Files:**
- Modify: `Core/ViewModels/ChannelViewModel.cs`

**Interfaces:**
- Consumes: `AudioManager.GetMute(string)`, `AudioManager.SetMute(string, bool)` (from Task 2)
- Produces:
  - `public bool IsMuted` — observable property (CommunityToolkit auto-generates from `isMuted`)
  - `public IRelayCommand ToggleMuteCommand` — generated by `[RelayCommand]`

- [ ] **Step 1: Add isMuted observable property**

In `Core/ViewModels/ChannelViewModel.cs`, after the `[ObservableProperty] private ImageSource? iconSource;` field declaration, add:

```csharp
[ObservableProperty]
private bool isMuted;
```

- [ ] **Step 2: Initialize isMuted in the constructor**

In the constructor, after `volume = audioManager.GetVolume(appName) * 100;`, add:

```csharp
isMuted = audioManager.GetMute(appName);
```

- [ ] **Step 3: Add OnIsMutedChanged partial**

After the existing `partial void OnVolumeChanged` method, add:

```csharp
partial void OnIsMutedChanged(bool value) =>
    _audioManager.SetMute(AppName, value);
```

- [ ] **Step 4: Add ToggleMuteCommand**

After `OnIsMutedChanged`, add:

```csharp
[RelayCommand]
private void ToggleMute() => IsMuted = !IsMuted;
```

- [ ] **Step 5: Build to verify**

```
dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug
```

Expected: Build succeeded, 0 Error(s).

- [ ] **Step 6: Commit**

```
git add Core/ViewModels/ChannelViewModel.cs
git commit -m "feat: add IsMuted and ToggleMuteCommand to ChannelViewModel"
```

---

### Task 4: MainViewModel — wire KnobPressed to channel toggle

**Files:**
- Modify: `Core/ViewModels/MainViewModel.cs`

**Interfaces:**
- Consumes: `SerialManager.KnobPressed` event (from Task 1), `ChannelViewModel.ToggleMuteCommand` (from Task 3), existing `ParseKnobIndex`, `LogSerial`, `_dispatcherQueue`

- [ ] **Step 1: Subscribe to KnobPressed in CreateAndStartSerial**

In `CreateAndStartSerial()`, after `serial.KnobDelta += OnKnobDelta;`, add:

```csharp
serial.KnobPressed += OnKnobPressed;
```

- [ ] **Step 2: Unsubscribe on reconnect**

In the `Reconnect()` relay command method, before `_serial.Stop();`, add:

```csharp
_serial.KnobPressed -= OnKnobPressed;
```

The `Reconnect` method should now read:

```csharp
[RelayCommand]
private void Reconnect()
{
    _serial.KnobPressed -= OnKnobPressed;
    _serial.Stop();
    _serial = CreateAndStartSerial();

    _settings.ComPort = ComPort;
    _settings.BaudRate = BaudRate;
    SettingsService.Save(_settings);
}
```

- [ ] **Step 3: Implement OnKnobPressed**

After the `OnKnobDelta` method, add:

```csharp
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
```

- [ ] **Step 4: Build to verify**

```
dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug
```

Expected: Build succeeded, 0 Error(s).

- [ ] **Step 5: Commit**

```
git add Core/ViewModels/MainViewModel.cs
git commit -m "feat: wire KnobPressed event to channel ToggleMute in MainViewModel"
```

---

### Task 5: KnobCard — mute button UI

**Files:**
- Modify: `Core/Controls/KnobCard.xaml.cs`
- Modify: `Core/Controls/KnobCard.xaml`

**Interfaces:**
- Consumes: `ChannelViewModel.IsMuted` (bool), `ChannelViewModel.ToggleMuteCommand`
- Produces: visible mute icon button on each card; slider dimmed to 40% opacity when muted

- [ ] **Step 1: Add helper methods to KnobCard.xaml.cs**

In `Core/Controls/KnobCard.xaml.cs`, after the existing `public string FormatPercent(double volume)` method, add:

```csharp
public string FormatMuteIcon(bool isMuted) => isMuted ? "🔇" : "🔊";

public double ConvertMuteToOpacity(bool isMuted) => isMuted ? 0.4 : 1.0;
```

- [ ] **Step 2: Add the mute button to KnobCard.xaml**

In `Core/Controls/KnobCard.xaml`, inside the `<Grid>`, add a second `Button` after the existing settings gear button:

```xml
<Button
    HorizontalAlignment="Right"
    VerticalAlignment="Bottom"
    Content="{x:Bind FormatMuteIcon(Channel.IsMuted), Mode=OneWay}"
    Command="{x:Bind Channel.ToggleMuteCommand}"
    FontSize="20" />
```

- [ ] **Step 3: Dim the slider when muted**

Find the `<Slider>` element in `Core/Controls/KnobCard.xaml` and add an `Opacity` binding:

```xml
<Slider
    Value="{x:Bind Channel.Volume, Mode=TwoWay}"
    Minimum="0"
    Maximum="100"
    Height="32"
    Opacity="{x:Bind ConvertMuteToOpacity(Channel.IsMuted), Mode=OneWay}" />
```

- [ ] **Step 4: Build to verify**

```
dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug
```

Expected: Build succeeded, 0 Error(s).

- [ ] **Step 5: Run and manually verify**

Launch the app. Confirm each channel card shows a 🔊 button. Click it — the icon should switch to 🔇, the slider should dim to 40% opacity, and the app's audio should be muted in Windows Volume Mixer. Click again to unmute.

- [ ] **Step 6: Commit**

```
git add Core/Controls/KnobCard.xaml Core/Controls/KnobCard.xaml.cs
git commit -m "feat: add mute button to KnobCard with dimmed slider when muted"
```

---

### Task 6: Arduino — button press detection on D5

**Files:**
- Modify: `Arduino/arduino/arduino.ino`

This task only applies when `USE_ENCODER == 1`. Changes are scoped to the encoder `#if` block.

- [ ] **Step 1: Add swPin to EncConfig**

Replace the `EncConfig` struct definition:

```cpp
struct EncConfig {
  const char* id;
  int clkPin;
  int dtPin;
  int swPin;
};
```

- [ ] **Step 2: Update the encoders array with swPin = 5**

```cpp
EncConfig encoders[] = {
  { "knob1", 17, 16, 5 },
  // { "knob2", 19, 18, X },
};
```

- [ ] **Step 3: Add debounce state arrays**

After the existing `volatile` arrays (`encDelta`, `encState`), add:

```cpp
uint8_t  swLastState[sizeof(encoders) / sizeof(encoders[0])];
unsigned long swLastDebounce[sizeof(encoders) / sizeof(encoders[0])];
```

- [ ] **Step 4: Initialize button state in setup()**

In `setup()`, inside the `for` loop that configures encoder pins, add after the existing `attachInterrupt` calls:

```cpp
pinMode(encoders[i].swPin, INPUT_PULLUP);
swLastState[i] = HIGH;
swLastDebounce[i] = 0;
```

- [ ] **Step 5: Poll button in loop()**

In `loop()`, after the existing encoder delta block (the `for` loop that reads and clears `encDelta`), add:

```cpp
unsigned long now = millis();
for (int i = 0; i < NUM_ENCODERS; i++) {
  uint8_t swState = digitalRead(encoders[i].swPin);
  if (swState == LOW && swLastState[i] == HIGH && (now - swLastDebounce[i]) > 50) {
    Serial.print(encoders[i].id);
    Serial.println(":press");
    swLastDebounce[i] = now;
  }
  swLastState[i] = swState;
}
```

- [ ] **Step 6: Verify in Arduino IDE / Serial Monitor**

Upload to the board. Open Serial Monitor at 115200 baud. Press the encoder button — you should see `knob1:press` printed once per press (no repeats while held, no bouncing).

- [ ] **Step 7: Commit**

```
git add Arduino/arduino/arduino.ino
git commit -m "feat: detect encoder button press on swPin D5, send knob1:press"
```

---

## End-to-End Manual Test

After all tasks are complete:

1. Upload updated Arduino firmware.
2. Launch the app and connect to the COM port.
3. Assign an app (e.g. Spotify) to Knob 1.
4. Press the encoder button — Spotify should mute, KnobCard icon should switch to 🔇, slider dims.
5. Press again — Spotify unmutes, icon back to 🔊, slider full opacity.
6. Click the 🔊 button on a card with the mouse — same mute/unmute behavior.
7. Enable Debug Serial Events in Settings — confirm `knob1 → press | Spotify muted=True` appears in the log.
