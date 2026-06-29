# ESP32 Premium Single-App Display — Design Spec
_2026-06-25_

## Goal

Make the GC9A01 round display show a single, premium-looking screen for whichever knob
is currently being moved: the assigned app's icon, name, and volume on an accent-colored
perimeter arc gauge. Kill the flicker that the current full-screen redraw causes, and add
a calm "breathing" idle animation when no knob is active.

This refines the existing per-knob model (one app on screen at a time, selected by the
active knob) — it does **not** introduce a multi-channel view. It builds on the existing
assign/icon sync protocol from `2026-06-24-esp32-display-sync-design.md`.

---

## User-facing behavior

- **One app at a time.** The ESP32 holds a per-knob table of `{label, icon, accentColor}`.
  When a knob moves, `onKnobChange(idx, value)` draws *that* knob's app. Turn knob 1 →
  Spotify; turn knob 2 → Discord. Never more than one app on screen.
- **Potentiometer caveat:** a pot only registers as "active" when its value changes, so the
  app swaps the moment you start turning a knob. This is the existing routing; no change.
- **Active screen:**
  - Full-perimeter arc gauge, ~82% sweep with the gap at the bottom, filling clockwise with
    volume. Track is dim gray; filled portion is the app accent color; a white tip dot marks
    the current level.
  - 64×64 app icon centered-high.
  - App name (uppercase) below the icon.
  - Big **number-only** volume readout below the name (no `%` symbol — the arc conveys it).
- **Idle (calm / breathing),** after 3 s with no knob movement: dark screen with a slow
  breathing ring, three gently drifting accent dots, and the "AudioMixer / READY" wordmark.
  No app is shown. Only assigned knobs ever drive the active screen.

---

## Accent color (per-app dominant color)

The arc fill, tip dot, name, and number accent use the app's dominant icon color so each app
looks distinct (Spotify green, Discord blurple, …). The ESP32 receives only raw icon pixels,
so the **Windows app computes the color** and sends it.

- `AudioManager` gains `GetIconColor(string processName) -> (byte r, byte g, byte b)` (or a
  packed value), reusing the existing 64×64 `Format32bppArgb` bitmap pipeline.
- Algorithm: average the opaque pixels (alpha > 128), then apply a light saturation/lightness
  guard so near-gray or near-black icons don't produce a muddy accent (clamp very dark/desaturated
  results to a default accent). Cache by process name alongside the existing icon caches.

---

## Protocol change (PC → ESP32)

The `assign` line gains the accent color, placed **before** the app name so an app name can
never break parsing:

```
assign:knob1:1DB954:Spotify\n      (was: assign:knob1:Spotify)
icon:knob1:<base64 RGB565>\n        (unchanged)
vol:knob1:0.42\n                    (unchanged)
```

- Color is 6 hex digits `RRGGBB` (24-bit). The ESP32 converts to RGB565 for `TFT_eSPI`.
- `handleAssignLine` is updated to parse `knobN`, then 6 hex color chars, then the rest as the
  label. If the color field is malformed, fall back to a default accent and still store the label.

No ESP32 → PC protocol changes.

---

## Windows app changes

| File | Change |
|------|--------|
| `Core/AudioManager.cs` | Add `GetIconColor(processName)` + dominant-color computation + cache |
| `Core/SerialManager.cs` | `SendAssignment` gains a color parameter; writes `assign:knobN:RRGGBB:AppName` |
| `Core/ViewModels/MainViewModel.cs` | `SyncChannel` fetches the color and passes it to `SendAssignment` |

`SyncChannel` already runs on connect, on app-name change, and on session/icon update, so the
color rides along on the same triggers — no new sync wiring needed.

---

## ESP32 changes

### Storage (`assignments.h` / `.cpp`)

Add a per-knob accent color to the existing table:

```cpp
extern uint16_t knobColor[MAX_KNOBS];   // RGB565, default accent if none sent
```

`handleAssignLine` parses the new `RRGGBB` field, converts to RGB565, stores it.

### Non-blocking knob loop (`knobs.cpp`)

Replace the blocking `delay(50)` in `knobsLoop()` with `millis()`-based pot sampling (sample
every ~25–30 ms) so the main loop stays free to drive smooth animation every iteration.
Encoder path is already non-blocking.

### Display module (`display.h` / `display.cpp`) — the core rework

Stateful, animation-driven renderer. No `fillScreen` on volume ticks.

**State:**
```cpp
mode            // ACTIVE | IDLE
activeKnob      // which knob's app is shown
targetVol       // latest value from knob
shownVol        // animated value, eases toward targetVol
lastArcFrac     // last arc fraction actually drawn (for incremental arc)
appDirty        // true when the app/knob changed → needs full active redraw
```

**API:**
```cpp
void displaySetup();
void displayShowKnob(int knobIndex, float value);  // set ACTIVE, target app+vol; if app changed, appDirty=true
void displayEnterIdle();                            // set IDLE (replaces old displayIdle as the trigger)
void displayTick();                                 // called every loop(): advance animation, redraw deltas
```

**Rendering rules (flicker-free):**
- **Full active redraw** (only when `appDirty`, i.e. app changed, or ACTIVE↔IDLE transition):
  `fillScreen(black)`, draw the empty track arc, `pushImage` the 64×64 icon, draw the name,
  draw the track. Infrequent, so the one-time clear is invisible.
- **Volume change, same app (every tick while `shownVol != targetVol`):**
  - Arc delta only: if rising, stroke the accent arc from `lastArcFrac → shownVol`; if falling,
    stroke the track color over `shownVol → lastArcFrac` to erase. Then erase the old tip dot
    (small patch) and draw the new tip dot. Update `lastArcFrac`.
  - Number: render digits into a small `TFT_eSprite` (~120×52, ~12.5 KB, black bg) and
    `pushSprite` in one shot — no per-glyph flicker.
- **Idle tick:** redraw the breathing ring in place each frame (same radius, new brightness —
  overwrites itself, no flicker), erase+redraw the three drifting dots, redraw the wordmark with
  a black text background so it clears its own box. Pace at ~30 ms.

**Easing:** `shownVol += (targetVol - shownVol) * k` per tick (k ≈ 0.18), clamped/snapped when
within ~0.005 so it settles. Feels responsive without overshoot.

### `mixer.ino`

- Call `displayTick()` every `loop()` iteration (after `readIncomingSerial()` / `knobsLoop()`).
- Idle handling stays: when `millis() - lastKnobActivity > IDLE_TIMEOUT_MS` and not already idle,
  call `displayEnterIdle()`.
- `onKnobChange` keeps calling `displayShowKnob(idx, value)` to set the target; actual drawing is
  driven by `displayTick()`.

### Memory check

Static: 4×8 KB icons (32 KB) + small color array. Transient: ~12.5 KB number sprite. Comfortable
on the classic ESP32 (~290 KB free heap), no PSRAM required.

---

## Implementation approach for the visual layer

Per the `/frontend-design` request, the **first implementation step is a polished HTML/Canvas
reference prototype** (frontend-design) of the 240×240 screen — exact coordinates, arc geometry,
font sizes, easing constants, and idle timing. The TFT_eSPI code is then a 1:1 port of that
reference, which removes guesswork from the firmware draw calls.

---

## Out of scope

- Mute indicator (the board is not sent mute state today).
- Audio-reactive idle animation (would require the PC to stream a level value).
- Showing multiple channels at once.
- SPIFFS/flash icon storage; baud-rate changes.
```

