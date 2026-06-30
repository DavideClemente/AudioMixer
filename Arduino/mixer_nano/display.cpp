#include "display.h"
#include "assignments.h"
#include <U8g2lib.h>
#include <Wire.h>

// ── Driver ────────────────────────────────────────────────────────────────────
// 1.3" 128x64 I2C OLED. These panels are almost always SH1106; if you see a 2px
// horizontal shift or garbage on the right edge, the panel is an SSD1306 — swap
// the constructor below to U8G2_SSD1306_128X64_NONAME_F_HW_I2C.
//
// Page-buffer "_1_" mode keeps only a 128 B buffer in RAM (one 8-row page) and
// renders the frame in 8 passes via firstPage()/nextPage(). The full-buffer
// "_F_" variant (1 KB) overflows the Nano's 2 KB SRAM, so page mode is required
// here. If you ever target a board with more RAM, "_F_" is faster.
static U8G2_SH1106_128X64_NONAME_1_HW_I2C u8g2(U8G2_R0, U8X8_PIN_NONE);

static const int SCR_W = 128;
static const int SCR_H = 64;

// ── Animation timing ──────────────────────────────────────────────────────────
static const unsigned long IDLE_DT = 50;    // ~20 fps idle frame step

// ── State ─────────────────────────────────────────────────────────────────────
enum Mode { ACTIVE, IDLE_MODE };
static Mode  mode       = IDLE_MODE;
static int   activeKnob = -1;
static float shownVol   = 0.0f;
static bool  isMuted     = false;
static bool  showPercent = false;

static unsigned long lastIdleMs = 0;
static bool          activeDirty = true;   // redraw the active screen on next tick

// ── Active screen ───────────────────────────────────────────────────────────────

static void drawCentered(const char* s, int y) {
  int w = u8g2.getStrWidth(s);
  u8g2.drawStr((SCR_W - w) / 2, y, s);
}

static void renderActive() {
  const char* label = (activeKnob >= 0 && activeKnob < MAX_KNOBS && knobLabel[activeKnob][0])
                      ? knobLabel[activeKnob] : "---";

  const int bx = 6, by = 54, bw = SCR_W - 12, bh = 8;

  if (isMuted) {
    u8g2.firstPage();
    do {
      u8g2.setFont(u8g2_font_6x12_tr);
      drawCentered(label, 12);

      // 🔇-style icon using primitives, centered in the number area (y=24–44).
      // Speaker body: 8×16 rect at (52,24). Cone: triangle to the left.
      // Mute X: two crossed lines to the right of the body.
      // U8g2 fills in page-buffer mode so we draw inside firstPage/nextPage.
      u8g2.drawBox(52, 24, 8, 16);                       // speaker body
      u8g2.drawTriangle(52,24, 52,40, 44,32);            // cone
      u8g2.drawLine(64, 24, 74, 40);                     // X line 1
      u8g2.drawLine(65, 24, 75, 40);
      u8g2.drawLine(74, 24, 64, 40);                     // X line 2
      u8g2.drawLine(75, 24, 65, 40);

      u8g2.drawFrame(bx, by, bw, bh);
    } while (u8g2.nextPage());
    return;
  }

  int pct = (int)(shownVol * 100.0f + 0.5f);
  char num[5];
  snprintf(num, sizeof(num), "%d", pct);

  u8g2.setFont(u8g2_font_logisoso24_tn);
  int numW = u8g2.getStrWidth(num);
  // Reserve space for '%' only when it will be drawn
  int pctW = showPercent ? (2 + 6) : 0;   // 6 ≈ '%' width in 6x12 font
  int startX = (SCR_W - (numW + pctW)) / 2;

  int fill = (int)((bw - 2) * shownVol + 0.5f);

  u8g2.firstPage();
  do {
    u8g2.setFont(u8g2_font_6x12_tr);
    drawCentered(label, 12);

    u8g2.setFont(u8g2_font_logisoso24_tn);
    u8g2.drawStr(startX, 44, num);
    if (showPercent) {
      u8g2.setFont(u8g2_font_6x12_tr);
      u8g2.drawStr(startX + numW + 2, 44, "%");
    }

    u8g2.drawFrame(bx, by, bw, bh);
    if (fill > 0) u8g2.drawBox(bx + 1, by + 1, fill, bh - 2);
  } while (u8g2.nextPage());
}

// ── Idle screen ─────────────────────────────────────────────────────────────────

static void renderIdle(unsigned long now) {
  // A small dot sweeps left↔right along the bottom so the screen looks alive.
  float phase = (now % 2000) / 2000.0f;          // 0..1 over 2 s
  float tri   = phase < 0.5f ? phase * 2.0f : (1.0f - phase) * 2.0f;  // 0..1..0
  int dotX = 8 + (int)((SCR_W - 16) * tri);

  u8g2.firstPage();
  do {
    u8g2.setFont(u8g2_font_7x14B_tr);
    drawCentered("AudioMixer", 28);

    u8g2.setFont(u8g2_font_5x7_tr);
    drawCentered("READY", 42);

    u8g2.drawDisc(dotX, 58, 2);
  } while (u8g2.nextPage());
}

// ── Public API ────────────────────────────────────────────────────────────────

void displaySetup() {
  u8g2.begin();
  u8g2.setBusClock(400000);   // 400 kHz I2C so a full 1 KB frame pushes fast enough
  mode = IDLE_MODE;
}

void displayShowKnob(int knobIndex, float value) {
  if (knobIndex < 0 || knobIndex >= MAX_KNOBS) return;
  value = constrain(value, 0.0f, 1.0f);
  mode = ACTIVE;
  // Snap straight to the new value instead of easing through every intermediate
  // number — the displayed percentage jumps directly to the current volume.
  // Redraw whenever the active knob or its value changes.
  if (knobIndex != activeKnob || value != shownVol) {
    activeKnob  = knobIndex;
    shownVol    = value;
    activeDirty = true;
  }
}

void displaySetShowPercent(bool show) {
  showPercent = show;
  activeDirty = true;
}

void displayShowMute(int knobIndex, bool muted) {
  if (knobIndex < 0 || knobIndex >= MAX_KNOBS) return;
  mode        = ACTIVE;
  activeKnob  = knobIndex;
  isMuted     = muted;
  activeDirty = true;
}

void displayEnterIdle() {
  mode = IDLE_MODE;
}

void displayTick() {
  unsigned long now = millis();

  if (mode == ACTIVE) {
    if (activeDirty) {
      renderActive();
      activeDirty = false;
    }
  } else { // IDLE_MODE
    if (now - lastIdleMs >= IDLE_DT) {
      lastIdleMs = now;
      renderIdle(now);
    }
  }
}
