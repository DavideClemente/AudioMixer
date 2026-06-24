# ESP32 Display Sync — Design Spec
_2026-06-24_

## Goal

When the user turns a physical knob, the GC9A01 round TFT display on the ESP32 shows the assigned app's icon and the current volume level. The Windows app is the source of truth for knob-to-app assignments and pushes icon data to the ESP32 over the existing serial connection.

---

## Protocol (PC → ESP32)

Two text lines are sent per knob, in order, whenever an assignment is set or changes:

```
assign:knob1:Spotify\n
icon:knob1:<base64-encoded RGB565 bytes>\n
```

- `knobId` matches the existing 1-based ESP32 convention (`knob1`, `knob2`, …).
- The icon payload is a single Base64 string encoding a 64×64 RGB565 bitmap (8 192 bytes raw → ~10 924 Base64 chars).
- The `assign` line always precedes its `icon` line so the ESP32 can associate label and pixels atomically.

**Trigger conditions:**
- On serial connect: PC sends all current channel assignments.
- When a channel's `AppName` changes: PC resends that knob's assignment + icon.
- When a channel's `IconSource` updates (audio session re-appears): PC resends that knob's icon.

No new messages are needed ESP32 → PC; the existing `knob1:0.42`, `knob1:up/down`, `knob1:press` protocol is unchanged.

---

## Windows App Changes

### `AudioManager` — new method `GetIconRgb565(string processName) -> byte[]`

Reuses the existing `System.Drawing.Bitmap` pipeline already in `ConvertIconToBitmapImage`:
1. Extract icon via `Icon.ExtractAssociatedIcon(path)`.
2. Resize to 64×64 using `Graphics.DrawImage`.
3. Lock bits in `Format32bppArgb`.
4. Convert each pixel: `r5 = r >> 3; g6 = g >> 2; b5 = b >> 3; rgb565 = (r5 << 11) | (g6 << 5) | b5`.
5. Write both bytes little-endian (LSB first) — matches ESP32's native `uint16_t` layout so the decoded byte array can be `memcpy`-ed directly into the `uint16_t` icon buffer for `tft.pushImage()`.
6. Cache result by process name alongside the existing `_iconCache`.
7. Return an empty `byte[]` for processes whose icon can't be extracted (ESP32 will show a placeholder).

### `SerialManager` — new method `SendAssignment(int knobIndex, string appName, byte[] iconRgb565)`

- Writes `assign:knob{knobIndex+1}:{appName}\n`.
- Writes `icon:knob{knobIndex+1}:{Convert.ToBase64String(iconRgb565)}\n`.
- Guard: only writes if `_port.IsOpen`. Fire-and-forget (no acknowledgement expected).

### `MainViewModel` — sync logic

- `SyncAllChannels()`: iterates `Channels`, calls `SendAssignment` for each. Called after `serial.Start()` succeeds.
- `SyncChannel(ChannelViewModel ch)`: called from two places:
  - `ChannelViewModel.OnAppNameChanged` callback (add a new `Action<ChannelViewModel>` delegate, similar to the existing `_onSettingsChanged`).
  - `ChannelViewModel.OnAvailableSessionsChanged` when `IconSource` updates for the matched session.

---

## ESP32 Changes

### Storage (`mixer.ino` / new `assignments.h`)

```cpp
static const int MAX_KNOBS = 4;
static const int ICON_W    = 64;
static const int ICON_H    = 64;

char     knobLabel[MAX_KNOBS][32];
uint16_t knobIcon [MAX_KNOBS][ICON_W * ICON_H];
bool     knobHasIcon[MAX_KNOBS];
```

~32 KB of SRAM for icons (ESP32 has ~320 KB available).

### Serial read loop

Added to `mixer.ino` loop (or a `serialLoop()` helper):

- Read incoming lines from `Serial`.
- Parse `assign:knob{n}:{label}` → store in `knobLabel[n-1]`.
- Parse `icon:knob{n}:{base64}` → Base64-decode into `knobIcon[n-1]`, set `knobHasIcon[n-1] = true`.
- A small Base64 decode function handles the ~10 KB payload in chunks as it arrives.

### `display.cpp` — updated `displayShowKnob`

New signature: `void displayShowKnob(int knobIndex, float value)`

Layout on the 240×240 round screen:
- Icon: 64×64, centered at (120, 95) — drawn with `tft.pushImage()`.
- App name (label): centered text below icon at y=145.
- Volume %: large text at y=175.
- Volume bar: 160×8 rect at y=200.
- If `!knobHasIcon[knobIndex]`: skip icon, shift text up.

---

## Baud Rate

The current default is 115 200. A single 64×64 icon takes ~0.95 s to transfer at that rate. For 2 knobs on startup that's ~2 s — acceptable. If more knobs are added or latency becomes noticeable, bumping to 921 600 in both `AppSettings` and `knobs.cpp` is a straightforward follow-up.

---

## Out of Scope

- Acknowledgement / retry if icon transfer is corrupted.
- SPIFFS/flash storage for icons (RAM is sufficient for ≤4 knobs).
- Encoder absolute-position tracking on the ESP32 (the pot flow already provides absolute 0–1 values).
