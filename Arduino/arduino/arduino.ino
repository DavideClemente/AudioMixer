// Set to 1 to use rotary encoders, 0 to use potentiometers
#define USE_ENCODER 0

// -----------------------------------------------------------------------
// Knob definitions — add or remove structs here to match your hardware.
// Each knob needs a unique id string, and the relevant pins for its type.
// -----------------------------------------------------------------------

struct PotConfig {
  const char* id;
  int pin;
};

struct EncConfig {
  const char* id;
  int clkPin;
  int dtPin;
  int swPin;
};

#if USE_ENCODER

EncConfig encoders[] = {
  { "knob1", 17, 16, 5 },
  // { "knob2", 19, 18, X },
};
const int NUM_ENCODERS = sizeof(encoders) / sizeof(encoders[0]);

// Per-encoder state (mirrors encoders[] by index)
volatile int  encDelta[sizeof(encoders) / sizeof(encoders[0])] = {};
volatile uint8_t encState[sizeof(encoders) / sizeof(encoders[0])] = {};

// Button debounce state (mirrors encoders[] by index)
uint8_t  swLastState[sizeof(encoders) / sizeof(encoders[0])];
unsigned long swLastDebounce[sizeof(encoders) / sizeof(encoders[0])];

// Quadrature lookup: index = (prevAB << 2) | currAB, value = direction
// AB = (CLK << 1) | DT
static const int8_t QEM[16] = {
  0, -1,  1,  0,
  1,  0,  0, -1,
 -1,  0,  0,  1,
  0,  1, -1,  0
};

void readEncoders() {
  for (int i = 0; i < NUM_ENCODERS; i++) {
    uint8_t curr = (digitalRead(encoders[i].clkPin) << 1) | digitalRead(encoders[i].dtPin);
    uint8_t idx  = (encState[i] << 2) | curr;
    encDelta[i] += QEM[idx & 0x0F];
    encState[i]  = curr;
  }
}

void setup() {
  Serial.begin(115200);
  for (int i = 0; i < NUM_ENCODERS; i++) {
    pinMode(encoders[i].clkPin, INPUT_PULLUP);
    pinMode(encoders[i].dtPin,  INPUT_PULLUP);
    encState[i] = (digitalRead(encoders[i].clkPin) << 1) | digitalRead(encoders[i].dtPin);
    // Attach to BOTH pins so every quadrature edge is caught
    attachInterrupt(digitalPinToInterrupt(encoders[i].clkPin), readEncoders, CHANGE);
    attachInterrupt(digitalPinToInterrupt(encoders[i].dtPin),  readEncoders, CHANGE);
    pinMode(encoders[i].swPin, INPUT_PULLUP);
    swLastState[i] = HIGH;
    swLastDebounce[i] = 0;
  }
}

void loop() {
  for (int i = 0; i < NUM_ENCODERS; i++) {
    // Atomically grab and clear the delta
    noInterrupts();
    int delta = encDelta[i];
    encDelta[i] = 0;
    interrupts();

    if (delta > 0) {
      for (int d = 0; d < delta; d++) {
        Serial.print(encoders[i].id);
        Serial.println(":up");
      }
    } else if (delta < 0) {
      for (int d = 0; d > delta; d--) {
        Serial.print(encoders[i].id);
        Serial.println(":down");
      }
    }
  }

  // Poll button states with debounce
  unsigned long now = millis();
  for (int i = 0; i < NUM_ENCODERS; i++) {
    uint8_t swState = digitalRead(encoders[i].swPin);
    if (swState == LOW && swLastState[i] == HIGH && (now - swLastDebounce[i]) > 50) {
      Serial.print(encoders[i].id);
      Serial.println(":press");
      swLastDebounce[i] = now;
    }
    swLastState[i] = swState;
  }

  delay(5);
}

#else  // USE_ENCODER == 0  →  potentiometers

PotConfig pots[] = {
  { "knob1", 15 },
  { "knob2", 35 },
};
const int NUM_POTS = sizeof(pots) / sizeof(pots[0]);

float smoothed[sizeof(pots) / sizeof(pots[0])] = {};
float lastSent[sizeof(pots) / sizeof(pots[0])];

void setup() {
  Serial.begin(115200);
  for (int i = 0; i < NUM_POTS; i++) lastSent[i] = -1.0;
}

void loop() {
  for (int i = 0; i < NUM_POTS; i++) {
    int raw   = analogRead(pots[i].pin);
    float val = raw / 4095.0;
    smoothed[i] = smoothed[i] * 0.85 + val * 0.15;

    if (abs(smoothed[i] - lastSent[i]) >= 0.01) {
      Serial.print(pots[i].id);
      Serial.print(":");
      Serial.println(smoothed[i], 2);
      lastSent[i] = smoothed[i];
    }
  }
  delay(50);
}

#endif
