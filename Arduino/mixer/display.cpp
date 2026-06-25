#include "display.h"
#include "assignments.h"
#include <TFT_eSPI.h>
#include <SPI.h>
#include <math.h>

static TFT_eSPI    tft;
static TFT_eSprite numSpr = TFT_eSprite(&tft);

// ── Geometry (match prototype CONSTANTS TABLE) ────────────────────────────────
// CX=120, CY=120, ARC_R=108, ARC_W=9, A0=32.4°, SWEEP=295.2°
// 0° = 6 o'clock, clockwise (firmware convention).
// Arc outer radius = ARC_R + ARC_W/2 = 112 (px)
// Arc inner radius = ARC_R - ARC_W/2 = 103 (px)
static const int   CX    = 120;
static const int   CY    = 120;
static const int   ARC_R = 108;   // arc stroke-center radius
static const int   ARC_W = 9;     // arc stroke width
static const float A0    = 32.4f; // start angle (deg), 0=6 o'clock, CW
static const float SWEEP = 295.2f;// total arc sweep (deg)

// TRACK: #2A2A2A → RGB565: (0x2A>>3)=5, (0x2A>>2)=10, (0x2A>>3)=5
// = (5<<11)|(10<<5)|5 = 0x2945   (prototype final value)
static const uint16_t TRACK   = 0x2945;

// Default accent when knobColor is unset: #00C8FF → RGB565 0x065F
static const uint16_t ACCENT_DEFAULT = 0x065F;

// Tip dot radius (px), color white
static const int TIP_DOT_R = 6;

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
static float lastArcFrac= 0.0f;
static int   lastPct    = -1;
static bool  appDirty   = false;
static bool  idleDirty  = true;

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

// Draw (or erase) the tip dot at the given arc fraction.
// color: pass TFT_WHITE for the live tip; pass TRACK or arc fill color to erase.
static void drawTipDot(float frac, uint16_t color) {
  int x, y;
  arcPoint(A0 + SWEEP * frac, ARC_R, x, y);
  tft.fillCircle(x, y, TIP_DOT_R, color);
}

// Draw the number percentage inside the numSpr sprite and push it to the screen.
// Sprite: 120×54, centered horizontally (CX-60 … CX+60), vertical center at y=165.
//   pushSprite(60, 138)  →  sprite center = 60 + 27 = 165 ✓
static void drawNumber(int pct, uint16_t color) {
  numSpr.fillSprite(TFT_BLACK);
  numSpr.setTextDatum(MC_DATUM);
  numSpr.setTextColor(color, TFT_BLACK);
  numSpr.setTextFont(4);   // TFT_eSPI built-in font (substitutes prototype's bold 52px Courier New)
  numSpr.setTextSize(2);
  numSpr.drawNumber(pct, 60, 27); // draw at sprite center (60,27)
  numSpr.pushSprite(CX - 60, 138); // sprite top-left: x=60, y=138 → center y = 138+27=165
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
                    (uint32_t)A0,
                    (uint32_t)(A0 + SWEEP),
                    TRACK, TFT_BLACK, true);

  // Icon (only when the host has sent one for this knob)
  if (activeKnob >= 0 && activeKnob < MAX_KNOBS && knobHasIcon[activeKnob]) {
    // Icon top-left = (CX - ICON_W/2, 48) = (88, 48); bottom = 48+64 = 112
    tft.pushImage(CX - ICON_W / 2, 48, ICON_W, ICON_H, knobIcon[activeKnob]);
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

  // Reset animation state so the arc intro starts from 0
  lastArcFrac = 0.0f;
  shownVol    = 0.0f;
  lastPct     = -1;
}

// Incremental arc update + tip dot + number sprite.
// Called every ANIM_DT ms when in ACTIVE mode.
static void animateActive() {
  // Ease shownVol toward targetVol (k = 0.08, prototype final value)
  shownVol += (targetVol - shownVol) * 0.08f;
  if (fabsf(targetVol - shownVol) < 0.005f) shownVol = targetVol;

  uint16_t col = accent();

  // Arc delta — only repaint the changed segment, not the full arc
  if (shownVol > lastArcFrac + 0.0005f) {
    // Volume increased: fill new segment with accent color
    tft.drawSmoothArc(CX, CY,
                      ARC_R + ARC_W / 2,
                      ARC_R - ARC_W / 2,
                      (uint32_t)(A0 + SWEEP * lastArcFrac),
                      (uint32_t)(A0 + SWEEP * shownVol),
                      col, TFT_BLACK, false);
  } else if (shownVol < lastArcFrac - 0.0005f) {
    // Volume decreased: revert segment to track color
    tft.drawSmoothArc(CX, CY,
                      ARC_R + ARC_W / 2,
                      ARC_R - ARC_W / 2,
                      (uint32_t)(A0 + SWEEP * shownVol),
                      (uint32_t)(A0 + SWEEP * lastArcFrac),
                      TRACK, TFT_BLACK, false);
  }

  // Erase old tip dot (paint it with whatever color is behind it)
  // If volume increased the old tip was in filled region → accent color.
  // If decreased it was in the (now-erased) region → TRACK.
  drawTipDot(lastArcFrac, (shownVol >= lastArcFrac) ? col : TRACK);
  // Draw new tip dot (white, radius TIP_DOT_R=6)
  drawTipDot(shownVol, TFT_WHITE);

  lastArcFrac = shownVol;

  // Number: only redraw when the integer percent changes
  int pct = (int)(shownVol * 100.0f + 0.5f);
  if (pct != lastPct) {
    drawNumber(pct, TFT_WHITE);  // number in white (prototype: NUMBER_COLOR = #FFFFFF)
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

  // Dynamic breathing circle (radius slightly animates: 104..107)
  int ringR = 104 + (int)(3 * breath);
  tft.drawCircle(CX, CY, ringR,     ring);
  tft.drawCircle(CX, CY, ringR + 1, ring);

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
  numSpr.setColorDepth(16);
  numSpr.createSprite(120, 54);  // 120 wide, 54 tall; center = (60, 27)
  idleDirty = true;
  mode      = IDLE;
}

// Call when a knob is turned / selected. Switches to ACTIVE mode for knobIndex,
// sets the target volume (0..1). Triggers a full redraw if the app changed.
void displayShowKnob(int knobIndex, float value) {
  if (knobIndex < 0 || knobIndex >= MAX_KNOBS) return;
  if (mode != ACTIVE || knobIndex != activeKnob) {
    activeKnob = knobIndex;
    appDirty   = true;
  }
  mode      = ACTIVE;
  targetVol = constrain(value, 0.0f, 1.0f);
}

// Call when all knobs are idle. Switches to IDLE animated screen.
void displayEnterIdle() {
  if (mode == IDLE) return;
  mode      = IDLE;
  idleDirty = true;
}

// Call every loop(). Advances animation state and redraws only changed regions.
void displayTick() {
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
    if (idleDirty) {
      idleEnterRedraw();
      idleDirty = false;
    }
    if (now - lastIdleMs >= IDLE_DT) {
      lastIdleMs = now;
      animateIdle(now);
    }
  }
}
