#include "display.h"
#include "assignments.h"
#include "mute_icon.h"
#include "idlegif.h"
#include <TFT_eSPI.h>
#include <SPI.h>
#include <math.h>

static TFT_eSPI    tft;
static TFT_eSprite numSpr  = TFT_eSprite(&tft);
static bool        numSprOK = false;

// ── Geometry (match prototype CONSTANTS TABLE) ────────────────────────────────
// CX=120, CY=120, ARC_R=108, ARC_W=9, ARC_A0=32.4°, SWEEP=295.2°
// 0° = 6 o'clock, clockwise (firmware convention).
// Arc outer radius = ARC_R + ARC_W/2 = 112 (px)
// Arc inner radius = ARC_R - ARC_W/2 = 103 (px)
static const int   CX    = 120;
static const int   CY    = 120;
static const int   ARC_R = 108;   // arc stroke-center radius
static const int   ARC_W = 9;     // arc stroke width
static const float ARC_A0    = 32.4f; // start angle (deg), 0=6 o'clock, CW
static const float SWEEP = 295.2f;// total arc sweep (deg)

// TRACK: #2A2A2A → RGB565: (0x2A>>3)=5, (0x2A>>2)=10, (0x2A>>3)=5
// = (5<<11)|(10<<5)|5 = 0x2945   (prototype final value)
static const uint16_t TRACK   = 0x2945;

// Default accent when knobColor is unset: #00C8FF → RGB565 0x065F
static const uint16_t ACCENT_DEFAULT = 0x065F;

// Tip dot radius (px), color white. Kept <= ARC_W/2 so the dot stays inside the
// arc band: the per-frame full-arc redraw then erases the previous dot for free
// (no separate erase op, which would flash the dot black on a non-buffered TFT).
static const int TIP_DOT_R = 4;

// ── Animation timing ──────────────────────────────────────────────────────────
static const unsigned long ANIM_DT        = 16;   // active animation step (~60 fps)
static const unsigned long IDLE_DT        = 16;   // idle frame step (~60 fps)
static const unsigned long IDLE_BREATH_MS = 1800; // breathing ring period (ms)

// ── State ─────────────────────────────────────────────────────────────────────
enum Mode { ACTIVE, IDLE };
static Mode  mode       = IDLE;
static int   activeKnob = -1;
static float targetVol  = 0.0f;
static float shownVol   = 0.0f;
static int   lastPct    = -1;

static bool  isMuted      = false;
static bool  showPercent  = false;
static bool  appDirty   = false;
static bool  idleDirty  = true;
static bool  gifMode    = false;   // idle screen is playing a stored GIF
static bool  uploadMode = false;   // showing the GIF-upload progress screen
static float uploadAngle = 0.0f;   // last-drawn progress arc angle
static int   uploadPct   = -1;     // last-drawn progress percentage

static unsigned long lastAnimMs = 0;
static unsigned long lastIdleMs = 0;

// Idle dot tracking: store previous positions to erase before redraw
static int prevDotX[3] = {-100, -100, -100};
static int prevDotY[3] = {-100, -100, -100};

// ── Geometry helper ───────────────────────────────────────────────────────────
// Convert angle (deg, 0 = 6 o'clock, clockwise) to screen coords on radius r.
// NOTE: verify orientation on device; if the tip dot is mirrored, flip the
// sign of the sinf() term (change CX - to CX +).
static void arcPoint(float angDeg, int r, int& x, int& y) {
  float a = angDeg * 0.01745329f; // deg → rad
  x = CX - (int)(r * sinf(a));
  y = CY + (int)(r * cosf(a));
}

// ── Accent color ──────────────────────────────────────────────────────────────
static uint16_t accent() {
  if (activeKnob >= 0 && activeKnob < MAX_KNOBS) {
    uint16_t c = knobColor[activeKnob];
    if (c != 0) return c;
  }
  return ACCENT_DEFAULT;
}

// ── Active screen helpers ─────────────────────────────────────────────────────

// Draw the number percentage inside the numSpr sprite and push it to the screen.
// Sprite: 150×56 (FIX 2), center = (75, 28).
//   pushSprite(CX-75, 137)  →  x=45, center-y = 137+28 = 165 ✓
// If numSpr allocation failed (FIX 1 null-guard), falls back to direct tft draw:
//   clears region x=[CX-75,CX+75], y=[137,193] then draws the number on tft directly.
static void drawNumber(int pct, uint16_t color) {
  char buf[5];
  snprintf(buf, sizeof(buf), showPercent ? "%d%%" : "%d", pct);
  // FreeSansBold24pt7b renders at native resolution (no pixel-doubling), giving
  // smooth glyphs. setTextSize(1) is required — scaling a free font degrades quality.
  if (numSprOK) {
    numSpr.fillSprite(TFT_BLACK);
    numSpr.setFreeFont(&FreeSansBold24pt7b);
    numSpr.setTextSize(1);
    numSpr.setTextDatum(MC_DATUM);
    numSpr.setTextColor(color, TFT_BLACK);
    numSpr.drawString(buf, 75, 28);
    numSpr.pushSprite(CX - 75, 137);
  } else {
    tft.fillRect(CX - 75, 137, 150, 56, TFT_BLACK);
    tft.setFreeFont(&FreeSansBold24pt7b);
    tft.setTextSize(1);
    tft.setTextDatum(MC_DATUM);
    tft.setTextColor(color, TFT_BLACK);
    tft.drawString(buf, CX, 165);
  }
}

// Full redraw of the active screen (on app switch or first show).
// Clears to black, draws the track arc, icon, and app name.
// The arc fill + tip + number are handled by animateActive() on the next tick.
static void fullActiveRedraw() {
  tft.fillScreen(TFT_BLACK);

  // Empty track arc — full sweep, dim gray
  tft.drawSmoothArc(CX, CY,
                    ARC_R + ARC_W / 2,  // outer radius = 112
                    ARC_R - ARC_W / 2,  // inner radius = 103
                    (uint32_t)ARC_A0,
                    (uint32_t)(ARC_A0 + SWEEP),
                    TRACK, TFT_BLACK, true);

  // Icon (only when the host has sent one for this knob)
  if (activeKnob >= 0 && activeKnob < MAX_KNOBS && knobHasIcon[activeKnob]) {
    // Icon top-left = (CX - ICON_W/2, 48) = (88, 48); bottom = 48+64 = 112
    // knobIcon holds host-order RGB565 (decoded little-endian). The GC9A01 wants
    // high-byte-first on the bus, so enable byte swapping for this push only;
    // restore the default afterwards so the number sprite (pushSprite) is unaffected.
    tft.setSwapBytes(true);
    tft.pushImage(CX - ICON_W / 2, 48, ICON_W, ICON_H, knobIcon[activeKnob]);
    tft.setSwapBytes(false);
  }

  // App name: centered at (CX, 130) — between icon bottom (~112) and number (~165)
  // Font substitution: TFT_eSPI font 2 ≈ prototype's 'bold 13px Courier New, monospace'
  const char* label = (activeKnob >= 0 && activeKnob < MAX_KNOBS && knobLabel[activeKnob][0])
                      ? knobLabel[activeKnob] : "---";
  tft.setTextDatum(MC_DATUM);
  tft.setTextColor(accent(), TFT_BLACK);
  tft.setTextFont(2);
  tft.setTextSize(1);
  tft.drawString(label, CX, 130);

  // shownVol is seeded by displayShowKnob (resumes the knob's last level), so
  // don't reset it here — only force the number to redraw.
  lastPct     = -1;
}

// Full arc update + tip dot + number sprite.
// Called every ANIM_DT ms when in ACTIVE mode (only while shownVol is moving).
//
// Rather than stamping per-frame delta segments + an AA "erase" window — which
// left accumulating anti-aliased seams along the swept path — we repaint the
// whole ring each frame as exactly two smooth arcs that meet at a single
// shared boundary angle: the filled portion (accent) and the remaining track
// portion. Both are drawn fresh over the same pixels every frame, so there is
// no seam accumulation, and the previous tip dot is overwritten automatically.
static void animateActive() {
  shownVol = targetVol;

  // When muted: draw the full arc in TRACK color (dim), no tip dot, "MUTE" label.
  // When unmuted: normal accent arc + tip dot + percentage.
  if (isMuted) {
    uint32_t a0 = (uint32_t)(ARC_A0 + 0.5f);
    uint32_t a1 = (uint32_t)(ARC_A0 + SWEEP + 0.5f);
    tft.drawSmoothArc(CX, CY, ARC_R + ARC_W / 2, ARC_R - ARC_W / 2,
                      a0, a1, TRACK, TFT_BLACK, true);

    if (lastPct != -2) {  // -2 sentinel: mute icon already drawn
      // Push the 🔇 emoji bitmap (generated by tools/emoji_to_progmem.py).
      // Center the icon in the number area: screen x=CX, y center=165.
      tft.fillRect(CX - 75, 137, 150, 56, TFT_BLACK);
      int ix = CX - MUTE_ICON_W / 2;
      int iy = 173 - MUTE_ICON_H / 2;
      tft.setSwapBytes(true);
      tft.pushImage(ix, iy, MUTE_ICON_W, MUTE_ICON_H, MUTE_ICON);
      tft.setSwapBytes(false);
      lastPct = -2;
    }
    return;
  }

  uint16_t col = accent();

  uint32_t a0  = (uint32_t)(ARC_A0 + 0.5f);
  uint32_t a1  = (uint32_t)(ARC_A0 + SWEEP + 0.5f);
  uint32_t mid = (uint32_t)(ARC_A0 + SWEEP * shownVol + 0.5f);
  if (mid < a0) mid = a0;
  if (mid > a1) mid = a1;

  if (mid > a0) {
    tft.drawSmoothArc(CX, CY, ARC_R + ARC_W / 2, ARC_R - ARC_W / 2,
                      a0, mid, col, TFT_BLACK, true);
  }
  if (mid < a1) {
    tft.drawSmoothArc(CX, CY, ARC_R + ARC_W / 2, ARC_R - ARC_W / 2,
                      mid, a1, TRACK, TFT_BLACK, true);
  }

  int tx, ty;
  arcPoint(ARC_A0 + SWEEP * shownVol, ARC_R, tx, ty);
  tft.fillCircle(tx, ty, TIP_DOT_R, TFT_WHITE);

  int pct = (int)(shownVol * 100.0f + 0.5f);
  if (pct != lastPct) {
    drawNumber(pct, TFT_WHITE);
    lastPct = pct;
  }
}

// ── Idle screen ───────────────────────────────────────────────────────────────

// One-time clear + static text drawn when entering idle.
static void idleEnterRedraw() {
  tft.fillScreen(TFT_BLACK);
  tft.setTextDatum(MC_DATUM);

  // "AUDIOMIXER" wordmark — muted white (prototype IDLE_WORDMARK #3A3A3A)
  // RGB565 for #3A3A3A: (0x3A>>3)=7, (0x3A>>2)=14, (0x3A>>3)=7 → 0x38E7 ≈ close
  // Use a slightly visible value so it's legible: 0xAD7F (soft blue-white, brief used)
  tft.setTextColor(0xAD7F, TFT_BLACK);
  tft.setTextFont(2);
  tft.setTextSize(1);
  tft.drawString("AudioMixer", CX, CY - 8);

  // "READY" subtitle — dimmer
  tft.setTextColor(TRACK, TFT_BLACK);
  tft.setTextFont(1);
  tft.setTextSize(1);
  tft.drawString("READY", CX, CY + 12);

  for (int i = 0; i < 3; i++) {
    prevDotX[i] = -100;
    prevDotY[i] = -100;
  }
}

// Per-frame idle animation: breathing ring + three drifting colored dots.
// now: millis() at time of call.
static void animateIdle(unsigned long now) {
  // Breathing ring: sinusoidal brightness on a circle, period = IDLE_BREATH_MS
  float phase  = (now % IDLE_BREATH_MS) / (float)IDLE_BREATH_MS; // 0..1
  float breath = 0.5f + 0.5f * sinf(phase * 6.2832f - 1.5708f);  // 0..1, starts at 0

  // Map breath to a 5-bit brightness level (0..31) for RGB565
  uint8_t lvl  = (uint8_t)(4 + 14 * breath);   // 4..18 in 5-bit range
  uint8_t lvlG = (uint8_t)(8 + 22 * breath);   // slightly brighter green channel
  // Compose a bluish-white ring color: (R=lvl, G=lvlG, B=31)
  uint16_t ring = ((uint16_t)lvl << 11) | ((uint16_t)lvlG << 5) | 0x1F;

  // Breathing circle: FIXED radius so each frame overwrites the same pixels
  // (a varying radius would leave faint ghost rings since old radii aren't
  // erased). The "breath" is the brightness pulse in `ring`, not the size.
  tft.drawCircle(CX, CY, 105, ring);
  tft.drawCircle(CX, CY, 106, ring);

  // Three orbiting colored dots (app accent colors from prototype)
  // Erase previous positions, compute new ones, draw
  static const float ORBIT_SPEED[3]  = { 0.28f, 0.19f, 0.23f };
  static const float ORBIT_PHASE[3]  = { 0.0f,  2.094f, 4.189f };
  static const float ORBIT_R[3]      = { 70.0f, 80.0f,  74.0f  };
  static const float ORBIT_DR[3]     = { 5.0f,  4.0f,   6.0f   };
  // RGB565 approximations of prototype accent colors
  static const uint16_t DOT_COLOR[3] = {
    0x0EE8,  // #1DB954 Spotify green  → R=1,G=29,B=8  → (3<<11)|(29<<5)|8 ≈ 0x1BA8 -- use 0x0EE8 (tighter)
    0x5C1E,  // #5865F2 Discord blurple
    0xE128,  // #E8453C Chrome red
  };

  float t = now / 1000.0f;  // seconds

  for (int i = 0; i < 3; i++) {
    // Erase old dot
    if (prevDotX[i] > -50) {
      tft.fillCircle(prevDotX[i], prevDotY[i], 3, TFT_BLACK);
    }
    // Compute new position
    float angle  = t * ORBIT_SPEED[i] * 6.2832f + ORBIT_PHASE[i];
    float radius = ORBIT_R[i] + ORBIT_DR[i] * sinf(t * 0.7f + ORBIT_PHASE[i]);
    int nx = CX + (int)(radius * cosf(angle));
    int ny = CY + (int)(radius * sinf(angle));
    tft.fillCircle(nx, ny, 3, DOT_COLOR[i]);
    prevDotX[i] = nx;
    prevDotY[i] = ny;
  }
}

// ── Public API ────────────────────────────────────────────────────────────────

void displaySetup() {
  tft.init();
  tft.setRotation(0);
  idleGifInit(&tft);   // mount LittleFS + load a stored idle GIF, if any
  numSpr.setColorDepth(16);
  // Sprite 150×56: wide enough for "100" at font4/size2 without clipping.
  // NOTE: exact fit must be confirmed on device; reduce setTextSize to 1.5 (or
  // use a narrower font) if "100" still clips at the right edge.
  numSprOK = (numSpr.createSprite(150, 56) != nullptr);
  idleDirty = true;
  mode      = IDLE;
}

void displaySetShowPercent(bool show) {
  if (show == showPercent) return;
  showPercent = show;
  lastPct = -1;  // force number redraw with new format
}

void displayShowMute(int knobIndex, bool muted) {
  if (knobIndex < 0 || knobIndex >= MAX_KNOBS) return;
  if (gifMode) { idleGifStop(); gifMode = false; }
  if (mode != ACTIVE || knobIndex != activeKnob) {
    activeKnob = knobIndex;
    appDirty   = true;
  }
  mode    = ACTIVE;
  isMuted = muted;
  lastPct = -1;  // force redraw of number / MUTE label
}

// Call when a knob is turned / selected. Switches to ACTIVE mode for knobIndex,
// sets the target volume (0..1). Triggers a full redraw if the app changed.
void displayShowKnob(int knobIndex, float value) {
  if (knobIndex < 0 || knobIndex >= MAX_KNOBS) return;
  value = constrain(value, 0.0f, 1.0f);
  if (gifMode) { idleGifStop(); gifMode = false; }
  if (mode != ACTIVE || knobIndex != activeKnob) {
    activeKnob = knobIndex;
    appDirty   = true;
  }
  mode      = ACTIVE;
  // animateActive() snaps shownVol straight to targetVol on the next tick, so the
  // number/arc jump directly to this value with no intermediate frames.
  targetVol = value;
}

// Call when all knobs are idle. Switches to IDLE animated screen.
void displayEnterIdle() {
  if (mode == IDLE) return;
  mode      = IDLE;
  idleDirty = true;
}

// ── GIF-upload progress screen ──────────────────────────────────────────────
// Mirrors the volume screen's look (progress arc + centered % + label) so the
// flash feels like a first-class part of the UI. Driven by idlegif.cpp.

static void uploadLabel(const char* text, uint16_t color) {
  tft.fillRect(0, 120, 240, 20, TFT_BLACK);   // clear the label band first
  tft.setTextDatum(MC_DATUM);
  tft.setTextColor(color, TFT_BLACK);
  tft.setTextFont(2);
  tft.setTextSize(1);
  tft.drawString(text, CX, 130);
}

void displayUploadBegin() {
  uploadMode  = true;
  uploadAngle = ARC_A0;
  uploadPct   = -1;

  tft.fillScreen(TFT_BLACK);
  tft.drawSmoothArc(CX, CY, ARC_R + ARC_W / 2, ARC_R - ARC_W / 2,
                    (uint32_t)ARC_A0, (uint32_t)(ARC_A0 + SWEEP),
                    TRACK, TFT_BLACK, true);
  uploadLabel("Updating", 0xAD7F);
  drawNumber(0, TFT_WHITE);
  uploadPct = 0;
}

void displayUploadProgress(float frac) {
  if (!uploadMode) return;
  if (frac < 0.0f) frac = 0.0f;
  if (frac > 1.0f) frac = 1.0f;

  // Redraw only on a whole-percent change: ~100 cheap updates across the whole
  // transfer instead of one per chunk, so drawing never throttles the link.
  int pct = (int)(frac * 100.0f + 0.5f);
  if (pct == uploadPct) return;
  uploadPct = pct;

  // Extend the accent arc by just the new wedge since the last update.
  float target = ARC_A0 + SWEEP * frac;
  if (target > uploadAngle) {
    tft.drawSmoothArc(CX, CY, ARC_R + ARC_W / 2, ARC_R - ARC_W / 2,
                      (uint32_t)uploadAngle, (uint32_t)target,
                      ACCENT_DEFAULT, TFT_BLACK, true);
    uploadAngle = target;
  }
  drawNumber(pct, TFT_WHITE);
}

void displayUploadEnd(bool ok) {
  if (!uploadMode) return;

  if (ok) {
    tft.drawSmoothArc(CX, CY, ARC_R + ARC_W / 2, ARC_R - ARC_W / 2,
                      (uint32_t)ARC_A0, (uint32_t)(ARC_A0 + SWEEP),
                      ACCENT_DEFAULT, TFT_BLACK, true);
    uploadLabel("Done", 0x8FF3);   // soft green
    drawNumber(100, TFT_WHITE);
  } else {
    uploadLabel("Failed", 0xF9A6); // soft red
  }
  delay(700);   // let the result register before the normal screen returns

  uploadMode = false;
  // Force a clean redraw of whichever mode we return to (idle plays the new GIF).
  idleDirty = true;
  appDirty  = true;
  lastPct   = -1;
}

// Call every loop(). Advances animation state and redraws only changed regions.
void displayTick() {
  if (uploadMode) return;   // upload screen owns the display until it finishes
  unsigned long now = millis();

  if (mode == ACTIVE) {
    if (appDirty) {
      fullActiveRedraw();
      appDirty = false;
    }
    if (now - lastAnimMs >= ANIM_DT) {
      lastAnimMs = now;
      // Animate as long as the value is moving, or if lastPct is unset (first frame)
      if (shownVol != targetVol || lastPct < 0) {
        animateActive();
      }
    }
  } else { // IDLE
    // A GIF added/cleared while already idle: re-init to switch screens.
    if (gifMode != idleGifAvailable()) idleDirty = true;
    if (idleDirty) {
      // Prefer a user-uploaded GIF; fall back to the built-in animation.
      if (idleGifAvailable()) {
        gifMode = true;
        idleGifStart(now);
      } else {
        gifMode = false;
        idleEnterRedraw();
      }
      idleDirty = false;
    }
    if (gifMode) {
      idleGifTick(now);
    } else if (now - lastIdleMs >= IDLE_DT) {
      lastIdleMs = now;
      animateIdle(now);
    }
  }
}
