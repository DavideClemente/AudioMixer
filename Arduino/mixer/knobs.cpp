#include "knobs.h"
#include <Arduino.h>

// Set to 1 to use rotary encoders, 0 to use potentiometers
#define USE_ENCODER 1

static KnobCallback s_cb = nullptr;

// ── Potentiometers ──────────────────────────────────────────────────────────

struct PotConfig { const char* id; int pin; };

static PotConfig pots[] = {
  { "knob1", 15 },
  { "knob2", 35 },
};
static const int NUM_POTS = sizeof(pots) / sizeof(pots[0]);

static float smoothed[NUM_POTS] = {};
static float lastSent[NUM_POTS];

// ── Encoders ────────────────────────────────────────────────────────────────

struct EncConfig { const char* id; int clkPin; int dtPin; int swPin; };

static EncConfig encoders[] = {
  { "knob1", 17, 16, 5 },
};
static const int NUM_ENCODERS = sizeof(encoders) / sizeof(encoders[0]);

static volatile int     encDelta[NUM_ENCODERS] = {};
static volatile uint8_t encState[NUM_ENCODERS] = {};
static int              encResidual[NUM_ENCODERS] = {};
static uint8_t          swLastState[NUM_ENCODERS];
static unsigned long    swLastDebounce[NUM_ENCODERS];

// Raw quadrature steps per physical detent. Common KY-040-style encoders emit a
// full 2-step gray-code transition between detents (we interrupt on both CLK and
// DT, CHANGE edge), so one click = 2 raw steps. Reporting every raw step made one
// click apply the PC's volume step twice (e.g. 4% → 8%). Set to 1 if your encoder
// is 1 step/detent, or 4 for a full 4-edge cycle per detent.
static const int DETENT_DIV = 2;

static const int8_t QEM[16] = {
   0, -1,  1,  0,
   1,  0,  0, -1,
  -1,  0,  0,  1,
   0,  1, -1,  0
};

static void IRAM_ATTR readEncoders() {
  for (int i = 0; i < NUM_ENCODERS; i++) {
    uint8_t curr = (digitalRead(encoders[i].clkPin) << 1) | digitalRead(encoders[i].dtPin);
    uint8_t idx  = (encState[i] << 2) | curr;
    encDelta[i] += QEM[idx & 0x0F];
    encState[i]  = curr;
  }
}

// ── Public API ──────────────────────────────────────────────────────────────

void knobsSetup(KnobCallback cb) {
  s_cb = cb;
  // Larger RX buffer so a base64 GIF-upload chunk (~5.5 KB) can't overrun the
  // UART FIFO if loop() briefly stalls on a redraw. Must precede begin().
  Serial.setRxBufferSize(16384);
  Serial.begin(921600);

#if USE_ENCODER
  for (int i = 0; i < NUM_ENCODERS; i++) {
    pinMode(encoders[i].clkPin, INPUT_PULLUP);
    pinMode(encoders[i].dtPin,  INPUT_PULLUP);
    encState[i] = (digitalRead(encoders[i].clkPin) << 1) | digitalRead(encoders[i].dtPin);
    attachInterrupt(digitalPinToInterrupt(encoders[i].clkPin), readEncoders, CHANGE);
    attachInterrupt(digitalPinToInterrupt(encoders[i].dtPin),  readEncoders, CHANGE);
    pinMode(encoders[i].swPin, INPUT_PULLUP);
    swLastState[i]    = HIGH;
    swLastDebounce[i] = 0;
  }
#else
  for (int i = 0; i < NUM_POTS; i++) lastSent[i] = -1.0f;
#endif
}

void knobsLoop() {
#if USE_ENCODER
  for (int i = 0; i < NUM_ENCODERS; i++) {
    noInterrupts();
    int delta = encDelta[i];
    encDelta[i] = 0;
    interrupts();

    // Collapse the raw quadrature steps into whole detents, carrying the leftover
    // step so a click that straddles two loop iterations still counts once.
    encResidual[i] += delta;
    int detents = encResidual[i] / DETENT_DIV;
    encResidual[i] -= detents * DETENT_DIV;

    // Encoders emit relative deltas, not an absolute level. We only report the
    // detents to the PC; the on-device gauge is driven by the authoritative
    // `vol:` echo the PC sends back (see handleVolumeLine in mixer.ino). Do NOT
    // call s_cb here — that feeds ±1.0 into displayShowKnob, which treats its
    // argument as an absolute 0..1 level and snaps the gauge to 0%/100%.
    if (detents > 0) {
      for (int d = 0; d < detents; d++) {
        Serial.print(encoders[i].id); Serial.println(":up");
      }
    } else if (detents < 0) {
      for (int d = 0; d > detents; d--) {
        Serial.print(encoders[i].id); Serial.println(":down");
      }
    }
  }

  unsigned long now = millis();
  for (int i = 0; i < NUM_ENCODERS; i++) {
    uint8_t sw = digitalRead(encoders[i].swPin);
    if (sw == LOW && swLastState[i] == HIGH && (now - swLastDebounce[i]) > 50) {
      Serial.print(encoders[i].id); Serial.println(":press");
      swLastDebounce[i] = now;
    }
    swLastState[i] = sw;
  }

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
}
