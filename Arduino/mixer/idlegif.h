#pragma once
#include <Arduino.h>

// Forward-declare so callers don't need the TFT_eSPI header just to pass the
// display pointer through.
class TFT_eSPI;

// Idle-screen GIF: stores a converted GIF (RGB565 frames) in LittleFS and plays
// it on the round display during idle. Frames are uploaded once from the PC over
// the "gif:*" serial protocol (see SerialManager.UploadIdleGifAsync) and replayed
// locally — the 921600 link can't stream ~113 KB/frame live.

// Mount LittleFS and load any previously stored GIF. `display` is retained for
// rendering during playback. Call once from displaySetup().
void idleGifInit(TFT_eSPI* display);

// Handle one "gif:*" upload-protocol line. Returns true if the line was consumed
// (so the caller can stop trying other handlers).
bool idleGifHandleLine(const char* line);

// True when a valid GIF is stored and ready to play.
bool idleGifAvailable();

// Begin/stop playback around an idle session (open/close the data file).
void idleGifStart(unsigned long now);
void idleGifStop();

// Draw the next frame if its delay has elapsed. No-op if not started/available.
void idleGifTick(unsigned long now);
