#include "knobs.h"
#include <Arduino.h>

// Set to 1 to use rotary encoders, 0 to use potentiometers
#define USE_ENCODER 0

static KnobCallback s_cb = nullptr;

// ── Potentiometers ──────────────────────────────────────────────────────────
// Use analog pins A0–A3 (A4/A5 are reserved for the OLED's I2C bus). A6/A7 are
// also ADC-capable on the Nano if you need more than four pots.

struct PotConfig { const char* id; int pin; };

static PotConfig pots[] = {
  { "knob1", A0 },
  { "knob2", A1 },
};
static const int NUM_POTS = sizeof(pots) / sizeof(pots[0]);

static float smoothed[NUM_POTS] = {};
static float lastSent[NUM_POTS];

// ── Encoders ────────────────────────────────────────────────────────────────
// The ATmega328P has hardware interrupts only on D2 (INT0) and D3 (INT1), so a
// single encoder uses both for CLK/DT. The push switch can be any digital pin.

struct EncConfig { const char* id; int clkPin; int dtPin; int swPin; };

static EncConfig encoders[] = {
  { "knob1", 2, 3, 4 },
};
static const int NUM_ENCODERS = sizeof(encoders) / sizeof(encoders[0]);

static volatile int     encDelta[NUM_ENCODERS] = {};
static volatile uint8_t encState[NUM_ENCODERS] = {};
static uint8_t          swLastState[NUM_ENCODERS];
static unsigned long    swLastDebounce[NUM_ENCODERS];

static const int8_t QEM[16] = {
   0, -1,  1,  0,
   1,  0,  0, -1,
  -1,  0,  0,  1,
   0,  1, -1,  0
};

// Plain ISR — no IRAM_ATTR (that is an ESP32-only attribute).
static void readEncoders() {
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
  Serial.begin(115200);

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

    // Encoders emit relative deltas, not an absolute level. We only report the
    // detents to the PC; the on-device gauge is driven by the authoritative
    // `vol:` echo the PC sends back. Do NOT call s_cb here — that feeds ±1.0 into
    // displayShowKnob, which treats its argument as an absolute 0..1 level.
    if (delta > 0) {
      for (int d = 0; d < delta; d++) {
        Serial.print(encoders[i].id); Serial.println(":up");
      }
    } else if (delta < 0) {
      for (int d = 0; d > delta; d--) {
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
    float val = analogRead(pots[i].pin) / 1023.0f;   // 10-bit ADC on AVR
    smoothed[i] = smoothed[i] * 0.85f + val * 0.15f;

    if (fabs(smoothed[i] - lastSent[i]) >= 0.01f) {
      Serial.print(pots[i].id);
      Serial.print(":");
      Serial.println(smoothed[i], 2);
      lastSent[i] = smoothed[i];
      if (s_cb) s_cb(pots[i].id, smoothed[i]);
    }
  }
#endif
}
