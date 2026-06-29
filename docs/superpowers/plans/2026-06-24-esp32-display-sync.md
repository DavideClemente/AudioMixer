# ESP32 Display Sync Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Push knob-to-app assignments and 64×64 app icons from the Windows app to the ESP32 over serial so the GC9A01 display shows the app icon and volume level when a knob is turned.

**Architecture:** The Windows app gains a write path on `SerialManager` and a sync trigger in `MainViewModel`/`ChannelViewModel`. The ESP32 gains an incoming serial reader, a Base64 decoder, per-knob storage, and an updated display renderer that calls `tft.pushImage()`.

**Tech Stack:** C# / WinUI 3 / .NET 8 / System.Drawing (Windows side); Arduino C++ / TFT_eSPI / GC9A01 / ESP32 (firmware side).

## Global Constraints

- Platform target: `net8.0-windows10.0.19041.0`, build with `-p:Platform=x64`.
- No test project exists — verification is done via build success + manual device testing.
- Arduino sketch lives in `Arduino/mixer/`. All new ESP32 files go there.
- Icon size: exactly 64×64 pixels, RGB565, little-endian byte order.
- Serial protocol: text lines, `\n`-terminated. No binary in the stream.
- `knobId` is 1-based in the wire protocol (`knob1`, `knob2`); `KnobIndex` is 0-based in C#.

---

## File Map

| File | Change |
|------|--------|
| `Core/AudioManager.cs` | Add `GetIconRgb565()`, `ConvertIconToRgb565()`, `_iconRgb565Cache` |
| `Core/SerialManager.cs` | Add `SendAssignment()` |
| `Core/ViewModels/ChannelViewModel.cs` | Add `_onSyncNeeded` callback param; call it in `OnAppNameChanged` + `OnAvailableSessionsChanged` |
| `Core/ViewModels/MainViewModel.cs` | Add `SyncAllChannels()`, `SyncChannel()`; call after serial start; pass callback to `ChannelViewModel` |
| `Arduino/mixer/assignments.h` | Declare storage arrays + function prototypes |
| `Arduino/mixer/assignments.cpp` | `base64Decode`, `handleAssignLine`, `handleIconLine` |
| `Arduino/mixer/mixer.ino` | Add incoming line buffer + `readIncomingSerial()`; update `onKnobChange` to pass index |
| `Arduino/mixer/display.h` | Update `displayShowKnob` signature to `(int knobIndex, float value)` |
| `Arduino/mixer/display.cpp` | Render icon with `tft.pushImage()`; include `assignments.h` |

---

## Task 1: AudioManager — icon RGB565 export

**Files:**
- Modify: `Core/AudioManager.cs`

**Interfaces:**
- Produces: `public byte[] GetIconRgb565(string processName)` — returns 8 192 bytes (64×64×2) or empty array if icon unavailable

- [ ] **Step 1: Add the RGB565 cache field and `GetIconRgb565` method**

Open `Core/AudioManager.cs`. Add the cache field next to the existing `_iconCache`:

```csharp
private readonly Dictionary<string, byte[]> _iconRgb565Cache = new(StringComparer.OrdinalIgnoreCase);
```

Add `GetIconRgb565` after `GetIconForProcess`:

```csharp
public byte[] GetIconRgb565(string processName)
{
    if (_iconRgb565Cache.TryGetValue(processName, out var cached))
        return cached;

    byte[] result = Array.Empty<byte>();
    try
    {
        var procs = Process.GetProcessesByName(processName);
        if (procs.Length > 0)
        {
            var path = procs[0].MainModule?.FileName;
            if (path is not null)
            {
                using var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is not null)
                    result = ConvertIconToRgb565(icon);
            }
        }
    }
    catch { }

    _iconRgb565Cache[processName] = result;
    return result;
}

private static byte[] ConvertIconToRgb565(Icon icon)
{
    const int size = 64;
    using var bmp = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    using var g = System.Drawing.Graphics.FromImage(bmp);
    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
    using var srcBmp = icon.ToBitmap();
    g.DrawImage(srcBmp, 0, 0, size, size);

    var data = bmp.LockBits(
        new System.Drawing.Rectangle(0, 0, size, size),
        System.Drawing.Imaging.ImageLockMode.ReadOnly,
        System.Drawing.Imaging.PixelFormat.Format32bppArgb);

    try
    {
        var raw = new byte[data.Stride * size];
        Marshal.Copy(data.Scan0, raw, 0, raw.Length);

        var rgb565 = new byte[size * size * 2];
        for (int i = 0; i < size * size; i++)
        {
            int o = i * 4;
            // Format32bppArgb in memory: B, G, R, A
            byte b = raw[o];
            byte grn = raw[o + 1];
            byte r = raw[o + 2];
            byte a = raw[o + 3];
            // Alpha-blend onto black
            r = (byte)(r * a / 255);
            grn = (byte)(grn * a / 255);
            b = (byte)(b * a / 255);

            ushort px = (ushort)(((r >> 3) << 11) | ((grn >> 2) << 5) | (b >> 3));
            rgb565[i * 2]     = (byte)(px & 0xFF);   // LSB first
            rgb565[i * 2 + 1] = (byte)(px >> 8);
        }
        return rgb565;
    }
    finally
    {
        bmp.UnlockBits(data);
    }
}
```

- [ ] **Step 2: Build to verify no compile errors**

```powershell
dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug
```

Expected: build succeeds with 0 errors.

- [ ] **Step 3: Commit**

```powershell
git add Core/AudioManager.cs
git commit -m "feat: add GetIconRgb565 — extract and convert process icon to 64x64 RGB565"
```

---

## Task 2: SerialManager — write path

**Files:**
- Modify: `Core/SerialManager.cs`

**Interfaces:**
- Consumes: nothing new
- Produces: `public void SendAssignment(int knobIndex, string appName, byte[] iconRgb565)`

- [ ] **Step 1: Add `SendAssignment` to `SerialManager`**

Open `Core/SerialManager.cs`. Add the method after `Stop()`:

```csharp
public void SendAssignment(int knobIndex, string appName, byte[] iconRgb565)
{
    if (!_port.IsOpen) return;
    try
    {
        var knobId = $"knob{knobIndex + 1}";
        _port.WriteLine($"assign:{knobId}:{appName}");
        if (iconRgb565.Length > 0)
            _port.WriteLine($"icon:{knobId}:{Convert.ToBase64String(iconRgb565)}");
    }
    catch { }
}
```

- [ ] **Step 2: Build**

```powershell
dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug
```

Expected: 0 errors.

- [ ] **Step 3: Commit**

```powershell
git add Core/SerialManager.cs
git commit -m "feat: add SerialManager.SendAssignment — write assign+icon lines to ESP32"
```

---

## Task 3: ChannelViewModel — sync callback

**Files:**
- Modify: `Core/ViewModels/ChannelViewModel.cs`

**Interfaces:**
- Consumes: new constructor parameter `Action<ChannelViewModel> onSyncNeeded`
- Produces: constructor signature updated; `_onSyncNeeded(this)` called on app-name or icon change

- [ ] **Step 1: Add `_onSyncNeeded` field and update constructor**

Open `Core/ViewModels/ChannelViewModel.cs`.

Add private field after `_onHideSession`:
```csharp
private readonly Action<ChannelViewModel> _onSyncNeeded;
```

Update the constructor signature — add `Action<ChannelViewModel> onSyncNeeded` as the last parameter:
```csharp
public ChannelViewModel(
    int knobIndex,
    string appName,
    AudioManager audioManager,
    ObservableCollection<AudioSession> availableSessions,
    ObservableCollection<ChannelViewModel> channels,
    Action<ChannelViewModel> onRemove,
    Action onSettingsChanged,
    Func<AudioSession, string?> onHideSession,
    Action<ChannelViewModel> onSyncNeeded)
```

Add assignment in the constructor body after `_onHideSession = onHideSession;`:
```csharp
_onSyncNeeded = onSyncNeeded;
```

- [ ] **Step 2: Call `_onSyncNeeded` in `OnAppNameChanged`**

Replace the existing `OnAppNameChanged`:
```csharp
partial void OnAppNameChanged(string value)
{
    Volume = _audioManager.GetVolume(value) * 100;
    IsMuted = _audioManager.GetMute(value);
    IconSource = GetSessionIcon(value);
    _onSettingsChanged();
    _onSyncNeeded(this);
}
```

- [ ] **Step 3: Call `_onSyncNeeded` in `OnAvailableSessionsChanged`**

Replace the existing `OnAvailableSessionsChanged`:
```csharp
private void OnAvailableSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
{
    var session = AvailableSessions.FirstOrDefault(s => s.ProcessName.Equals(AppName, StringComparison.OrdinalIgnoreCase));
    if (session is null)
        return;

    IconSource = session.IconSource;
    Volume = _audioManager.GetVolume(AppName) * 100;
    IsMuted = _audioManager.GetMute(AppName);
    _onSyncNeeded(this);
}
```

- [ ] **Step 4: Build (will fail — MainViewModel call site not updated yet)**

```powershell
dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug
```

Expected: 1 error at the `new ChannelViewModel(...)` call site in `MainViewModel.cs` — missing argument. This is expected; Task 4 fixes it.

---

## Task 4: MainViewModel — sync orchestration

**Files:**
- Modify: `Core/ViewModels/MainViewModel.cs`

**Interfaces:**
- Consumes: `AudioManager.GetIconRgb565(string)`, `SerialManager.SendAssignment(int, string, byte[])`, updated `ChannelViewModel` constructor

- [ ] **Step 1: Add `SyncChannel` and `SyncAllChannels`**

Open `Core/ViewModels/MainViewModel.cs`. Add after `SaveChannels()`:

```csharp
private void SyncChannel(ChannelViewModel ch)
{
    var icon = _audioManager.GetIconRgb565(ch.AppName);
    _serial.SendAssignment(ch.KnobIndex, ch.AppName, icon);
}

private void SyncAllChannels()
{
    foreach (var ch in Channels)
        SyncChannel(ch);
}
```

- [ ] **Step 2: Call `SyncAllChannels` after serial connects**

In the constructor, change:
```csharp
_serial = CreateAndStartSerial();
```
to:
```csharp
_serial = CreateAndStartSerial();
SyncAllChannels();
```

In `Reconnect()`, change:
```csharp
_serial = CreateAndStartSerial();
```
to:
```csharp
_serial = CreateAndStartSerial();
SyncAllChannels();
```

- [ ] **Step 3: Pass `SyncChannel` to `ChannelViewModel` constructor**

In `AddChannelInternal`, update the `Channels.Add(...)` call — add `SyncChannel` as the final argument:

```csharp
Channels.Add(new ChannelViewModel(
    index, appName, _audioManager, AvailableSessions, Channels,
    RemoveChannelInternal, SaveChannels, HideSession, SyncChannel));
```

- [ ] **Step 4: Build**

```powershell
dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug
```

Expected: 0 errors.

- [ ] **Step 5: Manual smoke test**

Run the app with the ESP32 connected. Open the Arduino IDE Serial Monitor (set to 115200, no line ending). When the app launches you should see lines like:
```
assign:knob1:Spotify
icon:knob1:AAEC...  (long base64 string)
assign:knob2:Discord
icon:knob2:AAEC...
```

Change an app assignment in the UI — a new `assign:` + `icon:` pair should appear in the monitor.

- [ ] **Step 6: Commit**

```powershell
git add Core/ViewModels/MainViewModel.cs Core/ViewModels/ChannelViewModel.cs
git commit -m "feat: sync knob assignments and icons to ESP32 on connect and on change"
```

---

## Task 5: ESP32 — assignment storage + Base64 decoder

**Files:**
- Create: `Arduino/mixer/assignments.h`
- Create: `Arduino/mixer/assignments.cpp`

**Interfaces:**
- Produces:
  - `extern char knobLabel[MAX_KNOBS][32]`
  - `extern uint16_t knobIcon[MAX_KNOBS][ICON_PIXELS]`
  - `extern bool knobHasIcon[MAX_KNOBS]`
  - `bool handleAssignLine(const char* line)` — returns true if the line was an `assign:` message
  - `bool handleIconLine(const char* line)` — returns true if the line was an `icon:` message

- [ ] **Step 1: Create `assignments.h`**

```cpp
#pragma once
#include <Arduino.h>

static const int MAX_KNOBS   = 4;
static const int ICON_W      = 64;
static const int ICON_H      = 64;
static const int ICON_PIXELS = ICON_W * ICON_H;

extern char     knobLabel  [MAX_KNOBS][32];
extern uint16_t knobIcon   [MAX_KNOBS][ICON_PIXELS];
extern bool     knobHasIcon[MAX_KNOBS];

bool handleAssignLine(const char* line);
bool handleIconLine  (const char* line);
```

- [ ] **Step 2: Create `assignments.cpp`**

```cpp
#include "assignments.h"

char     knobLabel  [MAX_KNOBS][32]          = {};
uint16_t knobIcon   [MAX_KNOBS][ICON_PIXELS] = {};
bool     knobHasIcon[MAX_KNOBS]              = {};

static int b64Val(char c) {
  if (c >= 'A' && c <= 'Z') return c - 'A';
  if (c >= 'a' && c <= 'z') return c - 'a' + 26;
  if (c >= '0' && c <= '9') return c - '0' + 52;
  if (c == '+') return 62;
  if (c == '/') return 63;
  return -1;
}

static int base64Decode(const char* src, uint8_t* dst, int dstLen) {
  int out = 0;
  while (src[0] && src[1] && src[2] && src[3]) {
    int v0 = b64Val(src[0]), v1 = b64Val(src[1]);
    int v2 = b64Val(src[2]), v3 = b64Val(src[3]);
    if (v0 < 0 || v1 < 0) break;
    if (out < dstLen) dst[out++] = (uint8_t)((v0 << 2) | (v1 >> 4));
    if (src[2] != '=' && v2 >= 0 && out < dstLen)
      dst[out++] = (uint8_t)(((v1 & 0xF) << 4) | (v2 >> 2));
    if (src[3] != '=' && v3 >= 0 && out < dstLen)
      dst[out++] = (uint8_t)(((v2 & 0x3) << 6) | v3);
    src += 4;
  }
  return out;
}

// Parse "assign:knob1:AppName"
bool handleAssignLine(const char* line) {
  if (strncmp(line, "assign:", 7) != 0) return false;
  const char* rest = line + 7;                    // "knob1:AppName"
  const char* colon = strchr(rest, ':');
  if (!colon || strncmp(rest, "knob", 4) != 0) return false;

  int idx = atoi(rest + 4) - 1;                  // 1-based → 0-based
  if (idx < 0 || idx >= MAX_KNOBS) return false;

  strncpy(knobLabel[idx], colon + 1, 31);
  knobLabel[idx][31] = '\0';
  return true;
}

// Parse "icon:knob1:<base64>"
bool handleIconLine(const char* line) {
  if (strncmp(line, "icon:", 5) != 0) return false;
  const char* rest = line + 5;                    // "knob1:<base64>"
  const char* colon = strchr(rest, ':');
  if (!colon || strncmp(rest, "knob", 4) != 0) return false;

  int idx = atoi(rest + 4) - 1;
  if (idx < 0 || idx >= MAX_KNOBS) return false;

  int decoded = base64Decode(colon + 1, (uint8_t*)knobIcon[idx], ICON_PIXELS * 2);
  knobHasIcon[idx] = (decoded == ICON_PIXELS * 2);
  return true;
}
```

- [ ] **Step 3: Verify it compiles (will be confirmed in Task 6 full build)**

No Arduino compile command outside the IDE; compilation is verified as part of Task 6.

---

## Task 6: ESP32 — incoming serial reader in `mixer.ino`

**Files:**
- Modify: `Arduino/mixer/mixer.ino`

**Interfaces:**
- Consumes: `handleAssignLine`, `handleIconLine` from `assignments.h`
- Consumes: updated `displayShowKnob(int knobIndex, float value)` from Task 7
- Note: the knob callback signature (`const char* id, float value`) stays the same; index is parsed inline

- [ ] **Step 1: Add `#include "assignments.h"` and the incoming line buffer**

Replace the current `mixer.ino` with:

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

void readIncomingSerial() {
  while (Serial.available() > 0) {
    char c = (char)Serial.read();
    if (c == '\n' || c == '\r') {
      if (inPos > 0) {
        inLine[inPos] = '\0';
        handleAssignLine(inLine);
        handleIconLine(inLine);
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
    idx = atoi(id + 4) - 1;   // "knob1" → 0

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
    displayIdle();
    isIdle = true;
  }
}
```

---

## Task 7: ESP32 — display with icon rendering

**Files:**
- Modify: `Arduino/mixer/display.h`
- Modify: `Arduino/mixer/display.cpp`

**Interfaces:**
- Consumes: `knobLabel`, `knobIcon`, `knobHasIcon`, `MAX_KNOBS` from `assignments.h`
- Produces: `void displayShowKnob(int knobIndex, float value)` — renders icon + label + volume

- [ ] **Step 1: Update `display.h`**

Replace `display.h` with:

```cpp
#pragma once

void displaySetup();
void displayShowKnob(int knobIndex, float value);
void displayIdle();
```

- [ ] **Step 2: Update `display.cpp`**

Replace `display.cpp` with:

```cpp
#include "display.h"
#include "assignments.h"
#include <TFT_eSPI.h>
#include <SPI.h>

static TFT_eSPI tft;

static const int CX = 120;
static const int CY = 120;

void displaySetup() {
  tft.init();
  tft.setRotation(0);
  displayIdle();
}

void displayIdle() {
  tft.fillScreen(TFT_BLACK);
  tft.drawCircle(CX, CY, 115, 0x4208);
  tft.setTextDatum(MC_DATUM);
  tft.setTextColor(0x8410, TFT_BLACK);
  tft.setTextSize(1);
  tft.drawString("AudioMixer", CX, CY);
}

void displayShowKnob(int knobIndex, float value) {
  float v = constrain(value, 0.0f, 1.0f);

  tft.fillScreen(TFT_BLACK);
  tft.drawCircle(CX, CY, 115, TFT_WHITE);

  tft.setTextDatum(MC_DATUM);

  if (knobIndex >= 0 && knobIndex < MAX_KNOBS && knobHasIcon[knobIndex]) {
    // Icon: 64x64 centred at (120, 72) — top-left = (88, 40)
    tft.pushImage(88, 40, ICON_W, ICON_H, knobIcon[knobIndex]);

    // Label below icon
    tft.setTextColor(TFT_WHITE, TFT_BLACK);
    tft.setTextSize(2);
    tft.drawString(knobLabel[knobIndex], CX, 122);
  } else {
    // No icon — label higher up
    const char* label = (knobIndex >= 0 && knobIndex < MAX_KNOBS && knobLabel[knobIndex][0])
                        ? knobLabel[knobIndex] : "---";
    tft.setTextColor(TFT_WHITE, TFT_BLACK);
    tft.setTextSize(2);
    tft.drawString(label, CX, 100);
  }

  // Volume percentage
  char pct[8];
  snprintf(pct, sizeof(pct), "%d%%", (int)(v * 100));
  tft.setTextSize(3);
  tft.setTextColor(TFT_CYAN, TFT_BLACK);
  tft.drawString(pct, CX, 158);

  // Volume bar — 160×8 centred horizontally at y=190
  const int BAR_W = 160, BAR_H = 8;
  int bx = CX - BAR_W / 2;
  tft.drawRect(bx, 190, BAR_W, BAR_H, TFT_WHITE);
  tft.fillRect(bx + 1, 191, (int)((BAR_W - 2) * v), BAR_H - 2, TFT_CYAN);
}
```

- [ ] **Step 3: Flash and test end-to-end**

1. Open `Arduino/mixer/mixer.ino` in Arduino IDE.
2. Verify the tabs show: `mixer`, `knobs`, `display`, `assignments`, `User_Setup`.
3. Compile + upload to the ESP32.
4. Launch the Windows app with the ESP32 connected.
5. Expected on display: idle screen ("AudioMixer"), then after ~1s the sync lines arrive and nothing visual changes yet.
6. Turn knob 1 — the display should show the assigned app's icon (64×64), its name, and the volume bar.
7. Let the knob go idle for 3 s — the display returns to the "AudioMixer" idle screen.
8. Change the assigned app in the Windows UI — turn the knob again and verify the new app's icon appears.

- [ ] **Step 4: Commit everything**

```powershell
git add Arduino/mixer/assignments.h Arduino/mixer/assignments.cpp Arduino/mixer/mixer.ino Arduino/mixer/display.h Arduino/mixer/display.cpp
git commit -m "feat: ESP32 receives app assignments and icons, displays them on GC9A01"
```
