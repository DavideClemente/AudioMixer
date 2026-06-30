#include "knobs.h"
#include "display.h"
#include "assignments.h"

// Runtime-configurable via "cfg:idle:<ms>" from the PC (handleConfigLine).
// Seeded to the previous hardcoded default until the first sync arrives.
static unsigned long idleTimeoutMs = 3000;
static unsigned long lastKnobActivity = 0;
static bool isIdle = true;

// Small line buffer. assign:/vol: lines are short; the PC's icon: lines (~11 KB)
// do NOT fit and are intentionally discarded — see readIncomingSerial().
static char inLine[96];
static int  inPos = 0;
static bool inOverflow = false;   // current line exceeded the buffer → drop it

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

static void handleMuteLine(const char* line) {
  if (strncmp(line, "mute:", 5) != 0) return;
  const char* rest = line + 5;                // "knob1:1"
  if (strncmp(rest, "knob", 4) != 0) return;
  const char* colon = strchr(rest, ':');
  if (!colon) return;
  int idx = atoi(rest + 4) - 1;
  if (idx < 0 || idx >= MAX_KNOBS) return;
  bool muted = atoi(colon + 1) != 0;
  displayShowMute(idx, muted);
  lastKnobActivity = millis();
  isIdle = false;
}

static void handleConfigLine(const char* line) {
  if (strncmp(line, "cfg:idle:", 9) == 0) {
    long ms = atol(line + 9);
    if (ms >= 0) idleTimeoutMs = (unsigned long)ms;
  } else if (strncmp(line, "config:pct:", 11) == 0) {
    displaySetShowPercent(atoi(line + 11) != 0);
  }
}

void readIncomingSerial() {
  while (Serial.available() > 0) {
    char c = (char)Serial.read();
    if (c == '\n' || c == '\r') {
      // Only process complete lines that fit the buffer. Over-long lines
      // (icon: blobs the Nano can't store) are silently dropped here.
      if (!inOverflow && inPos > 0) {
        inLine[inPos] = '\0';
        handleAssignLine(inLine);
        handleVolumeLine(inLine);
        handleMuteLine(inLine);
        handleConfigLine(inLine);
      }
      inPos = 0;
      inOverflow = false;
    } else if (inPos < (int)sizeof(inLine) - 1) {
      inLine[inPos++] = c;
    } else {
      inOverflow = true;   // keep draining the line, but stop storing it
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

  if (!isIdle && millis() - lastKnobActivity > idleTimeoutMs) {
    displayEnterIdle();
    isIdle = true;
  }

  displayTick();
}
