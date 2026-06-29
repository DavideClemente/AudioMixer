# Mute Feature Design

**Date:** 2026-06-24
**Status:** Approved

## Overview

Add per-channel mute to AudioMixerWin. Each channel card gets a mute toggle button. Pressing the rotary encoder's built-in button (D5 on the Arduino) triggers mute for that knob's channel. Mute uses the Windows `SimpleAudioVolume.Mute` flag — the volume slider stays at its current value and is visually dimmed. Mute state is session-only (not persisted across restarts).

## Serial Protocol

The Arduino sends a new line format when an encoder button is pressed:

| Line | Meaning |
|---|---|
| `knob1:up` | encoder rotated CW (existing) |
| `knob1:down` | encoder rotated CCW (existing) |
| `knob1:press` | encoder button pressed (new) |

## Changes by Layer

### Arduino (`Arduino/arduino/arduino.ino`)

- Add `swPin` field to `EncConfig` struct.
- Configure `knob1` with `swPin = 5` (D5, `INPUT_PULLUP`).
- In `setup()`, call `pinMode(swPin, INPUT_PULLUP)` for each encoder.
- In `loop()`, detect a falling edge on `swPin` with a simple debounce (track previous state per encoder, ~50 ms guard). On press, send `knob1:press\n` over serial.

### SerialManager (`Core/SerialManager.cs`)

- Add `event Action<string>? KnobPressed`.
- In `HandleCommand`, add branch: `if (payload == "press") KnobPressed?.Invoke(knobId);`

### AudioManager (`Core/AudioManager.cs`)

- Add `GetMute(string processName) → bool`:
  - For `MasterVolumeProcessName`: return `_device.AudioEndpointVolume.Mute`.
  - For app sessions: iterate sessions, match by process name, return `session.SimpleAudioVolume.Mute`. Return `false` if not found.
- Add `SetMute(string processName, bool muted)`:
  - For `MasterVolumeProcessName`: set `_device.AudioEndpointVolume.Mute`.
  - For app sessions: iterate sessions, match by process name, set `session.SimpleAudioVolume.Mute = muted`.

### ChannelViewModel (`Core/ViewModels/ChannelViewModel.cs`)

- Add `[ObservableProperty] private bool isMuted` initialized from `audioManager.GetMute(appName)` in the constructor.
- Add `partial void OnIsMutedChanged(bool value)` → calls `_audioManager.SetMute(AppName, value)`.
- Add `[RelayCommand] private void ToggleMute()` → `IsMuted = !IsMuted`.

### MainViewModel (`Core/ViewModels/MainViewModel.cs`)

- In `CreateAndStartSerial()`, subscribe: `serial.KnobPressed += OnKnobPressed`.
- Add `private void OnKnobPressed(string knobId)`:
  - Parse knob index via existing `ParseKnobIndex`.
  - Dispatch to UI thread.
  - Find matching channel by `KnobIndex`.
  - Call `channel.ToggleMuteCommand.Execute(null)`.
  - Log the event via `LogSerial` (respects `DebugSerialEvents`).
- In `CreateAndStartSerial()` teardown (when `Stop()` is called before reconnect), unsubscribe `KnobPressed`.

### KnobCard (`Core/Controls/KnobCard.xaml` / `.xaml.cs`)

- Add a helper method `FormatMuteIcon(bool isMuted) → string` returning `"🔇"` when muted, `"🔊"` when not.
- Add a `Button` in the card's bottom-right:
  - `Command="{x:Bind Channel.ToggleMuteCommand}"`
  - `Content="{x:Bind FormatMuteIcon(Channel.IsMuted), Mode=OneWay}"`
  - Styled as a small icon button (similar size to the existing settings gear button).
- Set the `Slider`'s `Opacity` to bind to `Channel.IsMuted` via a simple converter or x:Bind expression: `Opacity="{x:Bind ConvertMuteToOpacity(Channel.IsMuted), Mode=OneWay}"` returning `0.4` when muted, `1.0` when not.

## Non-Goals

- Mute state is not persisted to settings.
- No sync of mute state from external Windows Audio changes (another app muting the session).
- Master volume mute is supported by `AudioManager` but no encoder button is mapped to it in this design.
