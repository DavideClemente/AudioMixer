# Idle Screen GIF — Device Delivery & Configurable Timeout — Design Spec
_2026-06-30_

Follow-up to [idle-screen-gif-library-design](2026-06-30-idle-screen-gif-library-design.md),
which built the Windows-side GIF library and **deferred** pushing the chosen GIF to the
controller. This spec covers that delivery, plus a user-configurable idle timeout.

## Goals

1. Play the selected idle GIF on the ESP32 GC9A01 (240×240 round) display.
2. Let the user set, from app settings, how long with no knob activity before the
   controller shows its idle screen.

## Key decisions (locked)

- **Preload to flash, play locally.** The 115200 link couldn't stream ~113 KB/frame; even
  after the change below it's a one-time converted upload to LittleFS, then local playback.
- **Baud raised to 921600** (ESP32 build only). The ATmega `mixer_nano` stays at 115200
  (921600 is unreliable on a 16 MHz AVR) and does **not** get the GIF feature — only the
  configurable timeout.
- **Frames are 240×240 RGB565, little-endian**, matching the icon byte order
  (`AudioManager.Bgra64ToRgb565`) so the firmware reuses its existing push path.

## Configurable idle timeout

- `AppSettings.IdleTimeoutSeconds` (default 3) ↔ `SettingsPage` NumberBox (1–3600).
- `MainViewModel` sends `cfg:idle:<ms>` on change and on every post-connect sync
  (`SerialManager.SendIdleTimeout`). Both firmwares parse it in `handleConfigLine`,
  replacing the old hardcoded `IDLE_TIMEOUT_MS`.

## GIF encode (app)

`Core/Services/GifFrameEncoder.cs` — uses System.Drawing (its GIF decoder auto-composites
frames, handling disposal/transparency):
- Each frame scaled uniform-to-fill, centre-cropped to 240×240, packed little-endian RGB565.
- Per-frame delays read from GDI+ tag 0x5100 (1/100 s); 0-delay → 100 ms.
- If the GIF has more frames than the cap, frames are sampled evenly and the dropped frames'
  delays are folded into the kept ones (runtime preserved). Cap = `min(60, free_flash_budget)`.

## Upload protocol (serial, line-based, ACK-paced)

One chunk in flight so the UART/flash never falls behind. App→device, device replies on a
`gif:*` channel consumed only by the uploader (`SerialManager` routes `gif:` lines away from
the knob pipeline).

| App sends | Device replies |
|-----------|----------------|
| `gif:space?` | `gif:space:<freeBytes>` (counts the to-be-overwritten GIF as free) |
| `gif:begin:<count>:<w>:<h>:<delaysCsv>` | `gif:rdy` / `gif:err` (deletes old GIF, opens `/idle.tmp`) |
| `gif:d:<base64>` (≤6144 raw B/chunk) | `gif:ack` / `gif:err` (decodes, appends) |
| `gif:end` | `gif:done` / `gif:err` (verifies size, swaps tmp→`/idle.dat`, reloads) |
| `gif:clear` | `gif:cleared` (removes `/idle.dat`, reverts to built-in idle) |

Failure (timeout/size mismatch) leaves no corruption; on `gif:begin` the old GIF is freed up
front to use its space, so a failed/cancelled upload leaves the built-in animation until retry.

## Firmware playback

`Arduino/mixer/idlegif.{h,cpp}` owns LittleFS storage + the receiver and renders during idle:
- `/idle.dat`: magic `GID1`, count, w, h, delay table, then raw RGB565 frames.
- Playback streams each frame from flash in 16-row bands into a 7.7 KB buffer and `pushImage`s
  them (`setSwapBytes(true)`), so no full-frame RAM buffer is needed.
- `display.cpp` idle path prefers a stored GIF over the built-in breathing ring, and
  re-evaluates if a GIF is added/cleared while already idle. `Serial.setRxBufferSize(8192)`
  precedes `Serial.begin(921600)` so an upload chunk can't overrun the FIFO.

## App UI

Hero card on `IdleScreenPage` gains an upload progress bar, status line, and a
**Send to controller** button (`SyncToDeviceCommand`). Selecting a GIF auto-pushes it;
deleting the active GIF clears it from the device.

## On-device verification (cannot be unit-tested)

1. Flash `Arduino/mixer` (set baud 921600 in app settings to match).
2. **Partition scheme matters:** default 4 MB/1.5 MB-FS holds ~13 frames at 240×240. For more
   frames pick a larger-FS scheme (e.g. *No OTA (2 MB APP / 2 MB SPIFFS)* or a custom
   partition). The app adapts the frame count to whatever `gif:space?` reports.
3. Upload a GIF; watch the progress bar; confirm it plays after the idle timeout elapses.
4. Change the timeout; confirm the controller idles after the new delay.
5. Delete the active GIF; confirm the controller reverts to the built-in animation.

## Out of scope

Multiple-GIF playlists/rotation, per-GIF cropping, and streaming (non-preload) playback.
