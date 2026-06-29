# ESP32 Premium Single-App Display Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the GC9A01 round display's per-knob screen into a premium, flicker-free single-app view — accent-colored perimeter arc gauge, centered app icon, name, and number-only volume — plus a calm breathing idle animation.

**Architecture:** The Windows app computes each app icon's dominant color and sends it in the `assign` line. The ESP32 stores a per-knob accent color and replaces the full-screen-redraw renderer with a stateful, animation-driven one: full redraw only when the app changes or on idle transitions; volume ticks redraw only the arc delta + a small number sprite. A `displayTick()` called every loop drives smooth easing and the idle animation; the pot loop becomes non-blocking so animation stays smooth.

**Tech Stack:** C# / WinUI 3 / .NET 8 / System.Drawing (Windows); Arduino C++ / TFT_eSPI / GC9A01 / ESP32 (firmware); HTML5 Canvas (design reference prototype).

## Global Constraints

- Platform target: `net8.0-windows10.0.19041.0`; always build with `-p:Platform=x64` (no AnyCPU).
- No test project exists — C# verification is `dotnet build` success + manual smoke test. Firmware verification is Arduino IDE compile + on-device test.
- Arduino sketch lives in `Arduino/mixer/`. All firmware files go there. Arduino code is compiled/flashed only from the Arduino IDE; firmware tasks before the final one are compile-verified in the final firmware task.
- Icon size: exactly 64×64 px, RGB565, little-endian (existing).
- Serial protocol: text lines, `\n`-terminated, no binary in the stream.
- `knobId` is 1-based on the wire (`knob1`); `KnobIndex` is 0-based in C#.
- New `assign` line format: `assign:knobN:RRGGBB:AppName` — color (6 hex digits) **before** the name so app names cannot break parsing.
- Default accent (when no color received): RGB565 `0x065F` (≈ rgb 0,200,255).

---

## File Map

| File | Change |
|------|--------|
| `docs/superpowers/prototypes/esp32-display.html` | Create — frontend-design Canvas reference prototype (locks geometry/colors/easing/timings) |
| `Core/AudioManager.cs` | Add `GetIconColor()`, `ComputeDominantColor()`, `TryGetIconArgb()`, `_iconColorCache` |
| `Core/SerialManager.cs` | `SendAssignment` gains a color parameter; emits `assign:knobN:RRGGBB:AppName` |
| `Core/ViewModels/MainViewModel.cs` | `SyncChannel` fetches color and passes it to `SendAssignment` |
| `Arduino/mixer/assignments.h` | Declare `knobColor[MAX_KNOBS]` |
| `Arduino/mixer/assignments.cpp` | Init `knobColor`; parse `RRGGBB` in `handleAssignLine` |
| `Arduino/mixer/knobs.cpp` | Replace blocking `delay(50)` with `millis()`-based pot sampling |
| `Arduino/mixer/display.h` | New API: `displayShowKnob`, `displayEnterIdle`, `displayTick` |
| `Arduino/mixer/display.cpp` | Stateful animation renderer (arc delta, number sprite, breathing idle) |
| `Arduino/mixer/mixer.ino` | Call `displayTick()` each loop; use `displayEnterIdle()` |

---

## Task 1: Frontend-design reference prototype

Produces the visual source of truth — exact coordinates, arc geometry, fonts, colors, easing, and idle timing — that the firmware ports 1:1. Use the `frontend-design` skill for this task.

**Files:**
- Create: `docs/superpowers/prototypes/esp32-display.html`

**Interfaces:**
- Produces (as an HTML comment block at the top of the file, the "constants table" the firmware task consumes):
  - `CX, CY` (center), `ARC_R` (arc radius), `ARC_W` (arc stroke width)
  - `A0` (arc start angle, degrees, 0 = 6 o'clock, clockwise), `SWEEP` (total sweep degrees)
  - icon box `(x, y, 64, 64)`, name baseline `y`, number center `(x, y)` and font/size
  - ease factor `k`, animation timestep ms, idle frame ms, idle breath period ms
  - default accent `#0,200,255` / `0x065F`

- [ ] **Step 1: Invoke the frontend-design skill and build the prototype**

Build a single self-contained HTML file rendering a 240×240 round screen on `<canvas>`, matching the approved design:
- Perimeter arc gauge: ~82% sweep, gap centered at the bottom (6 o'clock). Dim gray track, accent-colored fill, white tip dot at the current level.
- 64×64 app icon centered-high; app name (uppercase) below; big **number-only** volume below that (no `%`).
- Accent color per app (hardcode a few sample apps: Spotify `#1DB954`, Discord `#5865F2`, Chrome `#E8453C`).
- Buttons to: turn the knob (animate volume), switch app (re-intro arc from 0), go idle.
- Idle: calm breathing ring + three drifting accent dots + "AudioMixer / READY" wordmark.
- Easing: `shown += (target - shown) * k` per animation tick.

At the top of the file, embed the constants table (HTML comment) listing every value above with the final numbers chosen.

- [ ] **Step 2: Verify visually**

Open `docs/superpowers/prototypes/esp32-display.html` in a browser. Confirm: arc fills/empties smoothly, tip dot sits on the arc, switching apps re-introduces the arc from 0 and swaps icon/name/color, idle breathes calmly. Adjust constants until it looks right, then finalize the constants table.

- [ ] **Step 3: Commit**

```powershell
git add docs/superpowers/prototypes/esp32-display.html
git commit -m "docs: frontend-design reference prototype for ESP32 premium display"
```

---

## Task 2: AudioManager — dominant icon color

**Files:**
- Modify: `Core/AudioManager.cs`

**Interfaces:**
- Produces: `public (byte R, byte G, byte B) GetIconColor(string processName)` — dominant brand color of the app icon, or default `(0, 200, 255)` when unavailable.

- [ ] **Step 1: Add the color cache field**

Open `Core/AudioManager.cs`. Add next to the existing icon caches:

```csharp
private readonly Dictionary<string, (byte R, byte G, byte B)> _iconColorCache = new(StringComparer.OrdinalIgnoreCase);
private static readonly (byte R, byte G, byte B) DefaultAccent = (0, 200, 255);
```

- [ ] **Step 2: Add `GetIconColor`, `TryGetIconArgb`, and `ComputeDominantColor`**

Add these methods (place after `GetIconRgb565`):

```csharp
public (byte R, byte G, byte B) GetIconColor(string processName)
{
    if (_iconColorCache.TryGetValue(processName, out var cached))
        return cached;

    var result = DefaultAccent;
    try
    {
        if (TryGetIconArgb(processName, out var argb))
            result = ComputeDominantColor(argb);
    }
    catch { }

    _iconColorCache[processName] = result;
    return result;
}

// Returns the 64x64 icon as raw BGRA bytes (Format32bppArgb memory order: B,G,R,A).
private static bool TryGetIconArgb(string processName, out byte[] argb)
{
    argb = Array.Empty<byte>();
    var procs = Process.GetProcessesByName(processName);
    if (procs.Length == 0) return false;

    var path = procs[0].MainModule?.FileName;
    if (path is null) return false;

    using var icon = Icon.ExtractAssociatedIcon(path);
    if (icon is null) return false;

    const int size = 64;
    using var bmp = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    using (var g = System.Drawing.Graphics.FromImage(bmp))
    {
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        using var srcBmp = icon.ToBitmap();
        g.DrawImage(srcBmp, 0, 0, size, size);
    }

    var data = bmp.LockBits(
        new System.Drawing.Rectangle(0, 0, size, size),
        System.Drawing.Imaging.ImageLockMode.ReadOnly,
        System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    try
    {
        argb = new byte[data.Stride * size];
        Marshal.Copy(data.Scan0, argb, 0, argb.Length);
    }
    finally { bmp.UnlockBits(data); }
    return true;
}

// Coarse-histogram dominant color: skip transparent/near-gray/near-dark pixels,
// bucket the rest (3 bits/channel), return the average of the most populated bucket.
private static (byte R, byte G, byte B) ComputeDominantColor(byte[] argb)
{
    var counts = new Dictionary<int, int>();
    var sums = new Dictionary<int, (long R, long G, long B, int N)>();
    int pixels = argb.Length / 4;

    for (int i = 0; i < pixels; i++)
    {
        int o = i * 4;
        byte b = argb[o], g = argb[o + 1], r = argb[o + 2], a = argb[o + 3];
        if (a < 128) continue;

        int max = Math.Max(r, Math.Max(g, b));
        int min = Math.Min(r, Math.Min(g, b));
        int sat = max == 0 ? 0 : (max - min) * 255 / max;
        if (sat < 40 || max < 40) continue; // skip gray and very dark pixels

        int key = ((r >> 5) << 6) | ((g >> 5) << 3) | (b >> 5);
        counts.TryGetValue(key, out int c);
        counts[key] = c + 1;
        sums.TryGetValue(key, out var s);
        sums[key] = (s.R + r, s.G + g, s.B + b, s.N + 1);
    }

    if (counts.Count == 0) return DefaultAccent;

    int best = counts.OrderByDescending(kv => kv.Value).First().Key;
    var bs = sums[best];
    return ((byte)(bs.R / bs.N), (byte)(bs.G / bs.N), (byte)(bs.B / bs.N));
}
```

If `System.Linq` is not already imported in this file, add `using System.Linq;` at the top.

- [ ] **Step 3: Build to verify no compile errors**

```powershell
dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug
```

Expected: build succeeds with 0 errors.

- [ ] **Step 4: Commit**

```powershell
git add Core/AudioManager.cs
git commit -m "feat: AudioManager.GetIconColor — extract dominant brand color from app icon"
```

---

## Task 3: SerialManager + MainViewModel — send accent color

**Files:**
- Modify: `Core/SerialManager.cs`
- Modify: `Core/ViewModels/MainViewModel.cs`

**Interfaces:**
- Consumes: `AudioManager.GetIconColor(string)` (Task 2), existing `AudioManager.GetIconRgb565(string)`.
- Produces: `public void SendAssignment(int knobIndex, string appName, (byte R, byte G, byte B) color, byte[] iconRgb565)`.

- [ ] **Step 1: Update `SendAssignment` to include the color**

Open `Core/SerialManager.cs`. Replace the existing `SendAssignment` method:

```csharp
public void SendAssignment(int knobIndex, string appName, (byte R, byte G, byte B) color, byte[] iconRgb565)
{
    if (!_port.IsOpen) return;
    try
    {
        var knobId = $"knob{knobIndex + 1}";
        var hex = $"{color.R:X2}{color.G:X2}{color.B:X2}";
        _port.WriteLine($"assign:{knobId}:{hex}:{appName}");
        if (iconRgb565.Length > 0)
            _port.WriteLine($"icon:{knobId}:{Convert.ToBase64String(iconRgb565)}");
    }
    catch { }
}
```

- [ ] **Step 2: Update `SyncChannel` to fetch and pass the color**

Open `Core/ViewModels/MainViewModel.cs`. Replace the existing `SyncChannel` method:

```csharp
private void SyncChannel(ChannelViewModel ch)
{
    var color = _audioManager.GetIconColor(ch.AppName);
    var icon = _audioManager.GetIconRgb565(ch.AppName);
    _serial.SendAssignment(ch.KnobIndex, ch.AppName, color, icon);
}
```

- [ ] **Step 3: Build**

```powershell
dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug
```

Expected: 0 errors.

- [ ] **Step 4: Manual smoke test**

Run the app with the ESP32 connected (or any serial monitor on the COM port, 115200, no line ending). Confirm the assign lines now carry a color, e.g.:

```
assign:knob1:1DB954:Spotify
icon:knob1:AAEC...
```

- [ ] **Step 5: Commit**

```powershell
git add Core/SerialManager.cs Core/ViewModels/MainViewModel.cs
git commit -m "feat: send per-app accent color to ESP32 in assign line"
```

---

## Task 4: ESP32 — store and parse accent color

**Files:**
- Modify: `Arduino/mixer/assignments.h`
- Modify: `Arduino/mixer/assignments.cpp`

**Interfaces:**
- Produces: `extern uint16_t knobColor[MAX_KNOBS]` (RGB565, defaults to `0x065F`); `handleAssignLine` now parses `assign:knobN:RRGGBB:AppName`.

- [ ] **Step 1: Declare `knobColor` in `assignments.h`**

Open `Arduino/mixer/assignments.h`. Add after the `knobHasIcon` declaration:

```cpp
extern uint16_t knobColor[MAX_KNOBS];   // accent color, RGB565
```

- [ ] **Step 2: Define and default `knobColor` in `assignments.cpp`**

Open `Arduino/mixer/assignments.cpp`. Add next to the other array definitions:

```cpp
uint16_t knobColor[MAX_KNOBS] = { 0x065F, 0x065F, 0x065F, 0x065F };  // default accent
```

- [ ] **Step 3: Replace `handleAssignLine` to parse the color field**

Replace the existing `handleAssignLine` with:

```cpp
// Parse "assign:knob1:RRGGBB:AppName"
bool handleAssignLine(const char* line) {
  if (strncmp(line, "assign:", 7) != 0) return false;
  const char* p = line + 7;                       // "knob1:RRGGBB:AppName"
  if (strncmp(p, "knob", 4) != 0) return false;

  int idx = atoi(p + 4) - 1;                       // 1-based -> 0-based
  if (idx < 0 || idx >= MAX_KNOBS) return false;

  const char* c1 = strchr(p, ':');                 // after "knobN"
  if (!c1) return false;
  const char* colorStr = c1 + 1;                   // "RRGGBB:AppName"
  const char* c2 = strchr(colorStr, ':');          // after color
  if (!c2) return false;

  if (c2 - colorStr >= 6) {
    char hex[7];
    memcpy(hex, colorStr, 6);
    hex[6] = '\0';
    long rgb = strtol(hex, nullptr, 16);
    uint8_t r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
    knobColor[idx] = ((r & 0xF8) << 8) | ((g & 0xFC) << 3) | (b >> 3);
  }

  strncpy(knobLabel[idx], c2 + 1, 31);
  knobLabel[idx][31] = '\0';
  return true;
}
```

- [ ] **Step 4: Verify (compilation confirmed in Task 7)**

No standalone Arduino compile here; verified as part of the Task 7 full build.

---

## Task 5: ESP32 — non-blocking pot sampling

**Files:**
- Modify: `Arduino/mixer/knobs.cpp`

**Interfaces:**
- No signature changes. Removes the blocking `delay(50)` so the main loop can animate every iteration; pots are sampled every ~25 ms via `millis()`.

- [ ] **Step 1: Replace the blocking pot loop body**

Open `Arduino/mixer/knobs.cpp`. In `knobsLoop()`, replace the entire `#else` (potentiometer) branch — currently ending in `delay(50);` — with:

```cpp
#else
  static unsigned long lastSample = 0;
  if (millis() - lastSample < 25) return;
  lastSample = millis();

  for (int i = 0; i < NUM_POTS; i++) {
    float val = analogRead(pots[i].pin) / 4095.0f;
    smoothed[i] = smoothed[i] * 0.85f + val * 0.15f;

    if (abs(smoothed[i] - lastSent[i]) >= 0.01f) {
      Serial.print(pots[i].id);
      Serial.print(":");
      Serial.println(smoothed[i], 2);
      lastSent[i] = smoothed[i];
      if (s_cb) s_cb(pots[i].id, smoothed[i]);
    }
  }
#endif
```

Note: the `return` early-exits `knobsLoop()`; this is the last statement in the function so the encoder branch (compiled out when `USE_ENCODER == 0`) is unaffected.

- [ ] **Step 2: Verify (compilation confirmed in Task 7)**

Verified as part of the Task 7 full build.

---

## Task 6: ESP32 — stateful animation renderer

**Files:**
- Modify: `Arduino/mixer/display.h`
- Modify: `Arduino/mixer/display.cpp`

**Interfaces:**
- Consumes: `knobLabel`, `knobIcon`, `knobHasIcon`, `knobColor`, `MAX_KNOBS`, `ICON_W`, `ICON_H` from `assignments.h`.
- Produces:
  - `void displaySetup()`
  - `void displayShowKnob(int knobIndex, float value)` — set ACTIVE; target app + volume
  - `void displayEnterIdle()` — switch to IDLE
  - `void displayTick()` — call every loop: advance animation, redraw only what changed

Use the constants finalized in the Task 1 prototype; the values below are the defaults from the design and can be nudged to match the prototype.

- [ ] **Step 1: Replace `display.h`**

```cpp
#pragma once

void displaySetup();
void displayShowKnob(int knobIndex, float value);
void displayEnterIdle();
void displayTick();
```

- [ ] **Step 2: Replace `display.cpp`**

```cpp
#include "display.h"
#include "assignments.h"
#include <TFT_eSPI.h>
#include <SPI.h>
#include <math.h>

static TFT_eSPI tft;
static TFT_eSprite numSpr = TFT_eSprite(&tft);

// ── Geometry (match Task 1 prototype) ────────────────────────────────────────
static const int   CX = 120, CY = 120;
static const int   ARC_R = 112;          // arc radius
static const int   ARC_W = 12;           // arc stroke width
static const float A0    = 32.4f;        // start angle (deg, 0 = 6 o'clock, CW)
static const float SWEEP = 295.2f;       // total sweep (deg) ≈ 82%
static const uint16_t TRACK = 0x18E3;    // dim gray track

// ── State ────────────────────────────────────────────────────────────────────
enum Mode { ACTIVE, IDLE };
static Mode   mode       = IDLE;
static int    activeKnob = -1;
static float  targetVol  = 0.0f;
static float  shownVol   = 0.0f;
static float  lastArcFrac= 0.0f;
static int    lastPct    = -1;
static bool   appDirty   = false;
static bool   idleDirty  = true;

static unsigned long lastAnimMs = 0;
static unsigned long lastIdleMs = 0;
static const unsigned long ANIM_DT = 16;   // active animation timestep
static const unsigned long IDLE_DT = 30;   // idle frame interval

// Idle dot tracking (erase previous positions)
static int prevDotX[3] = {-100,-100,-100};
static int prevDotY[3] = {-100,-100,-100};

// Convert a 0=6 o'clock, clockwise angle (deg) to screen coords on radius r.
// NOTE: verify orientation on device; if the tip dot is mirrored, flip the
// sign of the sin() term.
static void arcPoint(float angDeg, int r, int& x, int& y) {
  float a = angDeg * 0.01745329f;          // deg -> rad
  x = CX - (int)(r * sinf(a));
  y = CY + (int)(r * cosf(a));
}

static uint16_t accent() {
  if (activeKnob >= 0 && activeKnob < MAX_KNOBS) return knobColor[activeKnob];
  return 0x065F;
}

// ── Active screen ─────────────────────────────────────────────────────────────
static void drawTipDot(float frac, uint16_t color) {
  int x, y;
  arcPoint(A0 + SWEEP * frac, ARC_R, x, y);
  tft.fillCircle(x, y, ARC_W / 2, color);
}

static void fullActiveRedraw() {
  tft.fillScreen(TFT_BLACK);

  // Empty track arc
  tft.drawSmoothArc(CX, CY, ARC_R + ARC_W / 2, ARC_R - ARC_W / 2,
                    (uint32_t)A0, (uint32_t)(A0 + SWEEP), TRACK, TFT_BLACK, true);

  // Icon (only when present)
  if (activeKnob >= 0 && activeKnob < MAX_KNOBS && knobHasIcon[activeKnob]) {
    tft.pushImage(CX - ICON_W / 2, 40, ICON_W, ICON_H, knobIcon[activeKnob]);
  }

  // App name
  const char* label = (activeKnob >= 0 && activeKnob < MAX_KNOBS && knobLabel[activeKnob][0])
                      ? knobLabel[activeKnob] : "---";
  tft.setTextDatum(MC_DATUM);
  tft.setTextColor(TFT_WHITE, TFT_BLACK);
  tft.setTextFont(2);
  tft.setTextSize(1);
  tft.drawString(label, CX, 120);

  // Number sprite gets (re)drawn by the animation step
  lastArcFrac = 0.0f;
  shownVol    = 0.0f;     // re-intro the arc from 0
  lastPct     = -1;
}

static void drawNumber(int pct, uint16_t color) {
  numSpr.fillSprite(TFT_BLACK);
  numSpr.setTextDatum(MC_DATUM);
  numSpr.setTextColor(color, TFT_BLACK);
  numSpr.setTextFont(4);
  numSpr.setTextSize(2);
  numSpr.drawNumber(pct, 60, 27);
  numSpr.pushSprite(CX - 60, 150);
}

static void animateActive() {
  // Ease shownVol toward targetVol
  shownVol += (targetVol - shownVol) * 0.18f;
  if (fabsf(targetVol - shownVol) < 0.005f) shownVol = targetVol;

  uint16_t col = accent();

  // Arc delta only
  if (shownVol > lastArcFrac + 0.0005f) {
    tft.drawSmoothArc(CX, CY, ARC_R + ARC_W / 2, ARC_R - ARC_W / 2,
                      (uint32_t)(A0 + SWEEP * lastArcFrac),
                      (uint32_t)(A0 + SWEEP * shownVol),
                      col, TFT_BLACK, false);
  } else if (shownVol < lastArcFrac - 0.0005f) {
    tft.drawSmoothArc(CX, CY, ARC_R + ARC_W / 2, ARC_R - ARC_W / 2,
                      (uint32_t)(A0 + SWEEP * shownVol),
                      (uint32_t)(A0 + SWEEP * lastArcFrac),
                      TRACK, TFT_BLACK, false);
  }

  // Move tip dot: erase old, draw new
  drawTipDot(lastArcFrac, (shownVol >= lastArcFrac) ? col : TRACK);
  drawTipDot(shownVol, TFT_WHITE);
  lastArcFrac = shownVol;

  // Number only when the integer percent changed
  int pct = (int)(shownVol * 100.0f + 0.5f);
  if (pct != lastPct) {
    drawNumber(pct, col);
    lastPct = pct;
  }
}

// ── Idle screen ───────────────────────────────────────────────────────────────
static void idleEnterRedraw() {
  tft.fillScreen(TFT_BLACK);
  tft.setTextDatum(MC_DATUM);
  tft.setTextColor(0xAD7F, TFT_BLACK);   // soft blue-white
  tft.setTextFont(2);
  tft.setTextSize(1);
  tft.drawString("AudioMixer", CX, CY - 6);
  tft.setTextColor(TRACK, TFT_BLACK);
  tft.setTextFont(1);
  tft.drawString("READY", CX, CY + 18);
  for (int i = 0; i < 3; i++) { prevDotX[i] = prevDotY[i] = -100; }
}

static void animateIdle(unsigned long now) {
  float t = now / 1000.0f;
  float breath = 0.5f + 0.5f * sinf(t * (6.2832f / 1.8f));   // ~1.8s period

  // Breathing ring (redraw same radius, brightness via color scale)
  uint8_t lvl = (uint8_t)(6 + 18 * breath);                  // 0..31 channel
  uint16_t ring = (lvl << 11) | ((lvl + 8) << 6) | 0x1F;     // bluish
  tft.drawCircle(CX, CY, 100, ring);
  tft.drawCircle(CX, CY, 101, ring);

  // Drifting dots
  for (int i = 0; i < 3; i++) {
    if (prevDotX[i] > -50) tft.fillCircle(prevDotX[i], prevDotY[i], 3, TFT_BLACK);
    float a = t * 0.9f + i * 2.094f;
    int x = CX + (int)(78 * cosf(a));
    int y = CY + (int)(78 * sinf(a));
    tft.fillCircle(x, y, 3, 0x6B5F);
    prevDotX[i] = x; prevDotY[i] = y;
  }
}

// ── Public API ────────────────────────────────────────────────────────────────
void displaySetup() {
  tft.init();
  tft.setRotation(0);
  numSpr.setColorDepth(16);
  numSpr.createSprite(120, 54);
  idleDirty = true;
  mode = IDLE;
}

void displayShowKnob(int knobIndex, float value) {
  if (knobIndex < 0 || knobIndex >= MAX_KNOBS) return;
  if (mode != ACTIVE || knobIndex != activeKnob) {
    activeKnob = knobIndex;
    appDirty = true;
  }
  mode = ACTIVE;
  targetVol = constrain(value, 0.0f, 1.0f);
}

void displayEnterIdle() {
  if (mode == IDLE) return;
  mode = IDLE;
  idleDirty = true;
}

void displayTick() {
  unsigned long now = millis();
  if (mode == ACTIVE) {
    if (appDirty) { fullActiveRedraw(); appDirty = false; }
    if (now - lastAnimMs >= ANIM_DT) {
      lastAnimMs = now;
      if (shownVol != targetVol || lastPct < 0) animateActive();
    }
  } else { // IDLE
    if (idleDirty) { idleEnterRedraw(); idleDirty = false; }
    if (now - lastIdleMs >= IDLE_DT) {
      lastIdleMs = now;
      animateIdle(now);
    }
  }
}
```

- [ ] **Step 3: Verify (compilation confirmed in Task 7)**

Verified as part of the Task 7 full build.

---

## Task 7: ESP32 — wire `displayTick()` into the loop + flash & test

**Files:**
- Modify: `Arduino/mixer/mixer.ino`

**Interfaces:**
- Consumes: `displayShowKnob`, `displayEnterIdle`, `displayTick` from `display.h`.

- [ ] **Step 1: Update `mixer.ino` to drive the renderer each loop**

Replace the `loop()` and update the idle call. The full updated `mixer.ino`:

```cpp
#include "knobs.h"
#include "display.h"
#include "assignments.h"

static const unsigned long IDLE_TIMEOUT_MS = 3000;
static unsigned long lastKnobActivity = 0;
static bool isIdle = true;

// Buffer for lines arriving from the PC (icon lines are ~11 KB)
static char  inLine[12000];
static int   inPos = 0;

static void handleVolumeLine(const char* line) {
  if (strncmp(line, "vol:", 4) != 0) return;
  const char* rest = line + 4;                // "knob1:0.42"
  if (strncmp(rest, "knob", 4) != 0) return;
  const char* colon = strchr(rest, ':');
  if (!colon) return;
  int idx = atoi(rest + 4) - 1;
  if (idx < 0 || idx >= MAX_KNOBS) return;
  float v = atof(colon + 1);
  displayShowKnob(idx, v);
  lastKnobActivity = millis();
  isIdle = false;
}

void readIncomingSerial() {
  while (Serial.available() > 0) {
    char c = (char)Serial.read();
    if (c == '\n' || c == '\r') {
      if (inPos > 0) {
        inLine[inPos] = '\0';
        handleAssignLine(inLine);
        handleIconLine(inLine);
        handleVolumeLine(inLine);
        inPos = 0;
      }
    } else if (inPos < (int)sizeof(inLine) - 1) {
      inLine[inPos++] = c;
    }
  }
}

void onKnobChange(const char* id, float value) {
  int idx = -1;
  if (strncmp(id, "knob", 4) == 0)
    idx = atoi(id + 4) - 1;   // "knob1" -> 0

  if (idx >= 0 && idx < MAX_KNOBS)
    displayShowKnob(idx, value);

  lastKnobActivity = millis();
  isIdle = false;
}

void setup() {
  displaySetup();
  knobsSetup(onKnobChange);   // knobsSetup calls Serial.begin(115200)
  lastKnobActivity = millis();
}

void loop() {
  readIncomingSerial();
  knobsLoop();

  if (!isIdle && millis() - lastKnobActivity > IDLE_TIMEOUT_MS) {
    displayEnterIdle();
    isIdle = true;
  }

  displayTick();
}
```

- [ ] **Step 2: Compile + upload from the Arduino IDE**

1. Open `Arduino/mixer/mixer.ino` in the Arduino IDE.
2. Confirm tabs: `mixer`, `knobs`, `display`, `assignments`, `User_Setup`.
3. Compile. Expected: 0 errors (this build verifies Tasks 4, 5, 6, 7 together).
4. Upload to the ESP32.

- [ ] **Step 3: End-to-end on-device test**

1. Launch the Windows app with the ESP32 connected.
2. Display starts on the breathing idle screen ("AudioMixer / READY") — ring pulses, dots drift, no flicker.
3. Turn knob 1 → the assigned app's icon, name, and accent-colored arc appear; the arc sweeps up to the current volume; the number reads correctly.
4. Keep turning → the arc and number track smoothly with **no full-screen black flash** (the original flicker is gone).
5. Turn knob 2 → the screen switches to that app's icon/name/color (arc re-introduces from 0).
6. Stop for 3 s → returns to the breathing idle screen.
7. If the tip dot appears mirrored relative to the filled arc, flip the sign of the `sinf` term in `arcPoint()` and re-upload (noted in Task 6).

- [ ] **Step 4: Commit**

```powershell
git add Arduino/mixer/assignments.h Arduino/mixer/assignments.cpp Arduino/mixer/knobs.cpp Arduino/mixer/display.h Arduino/mixer/display.cpp Arduino/mixer/mixer.ino
git commit -m "feat: ESP32 premium single-app display — arc gauge, accent color, flicker-free, breathing idle"
```

---

## Self-Review

**Spec coverage:**
- One app at a time selected by active knob → Task 6 (`displayShowKnob`/state) + Task 7 (routing). ✓
- Perimeter arc gauge + icon + name + number-only → Task 6 (`fullActiveRedraw`, `animateActive`, `drawNumber`). ✓
- Per-app dominant accent color → Task 2 (compute), Task 3 (send), Task 4 (parse/store), Task 6 (use). ✓
- Protocol `assign:knobN:RRGGBB:AppName` → Task 3 (emit), Task 4 (parse). ✓
- Flicker fix (no per-tick `fillScreen`, arc delta + number sprite) → Task 6. ✓
- Calm breathing idle → Task 6 (`animateIdle`/`idleEnterRedraw`), Task 7 (trigger). ✓
- Non-blocking pot loop for smooth animation → Task 5. ✓
- frontend-design reference prototype → Task 1. ✓
- Only assigned knobs shown → existing behavior preserved (knobs only fire for wired pots; display only shows the active knob). ✓
- Out of scope (mute, audio-reactive idle, multi-channel) → not implemented. ✓

**Placeholder scan:** No TBD/TODO; every code step has complete code. The one device-dependent item (arc orientation) has an explicit verify-and-flip instruction, not a placeholder.

**Type consistency:** `GetIconColor` returns `(byte R, byte G, byte B)` in Tasks 2/3; `SendAssignment(int, string, (byte,byte,byte), byte[])` consistent in Tasks 3; `knobColor[MAX_KNOBS]` (`uint16_t`, RGB565) consistent across Tasks 4/6; `displayShowKnob`/`displayEnterIdle`/`displayTick` consistent across Tasks 6/7.
