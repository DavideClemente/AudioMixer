#include "knobs.h"
#include "display.h"
#include "assignments.h"

static const unsigned long IDLE_TIMEOUT_MS = 3000;
static unsigned long lastKnobActivity = 0;
static bool isIdle = true;

// Buffer for lines arriving from the PC (icon lines are ~11 KB)
static char  inLine[12000];
static int   inPos = 0;

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

void readIncomingSerial() {
  while (Serial.available() > 0) {
    char c = (char)Serial.read();
    if (c == '\n' || c == '\r') {
      if (inPos > 0) {
        inLine[inPos] = '\0';
        handleAssignLine(inLine);
        handleIconLine(inLine);
        handleVolumeLine(inLine);
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

  if (!isIdle && millis() - lastKnobActivity > IDLE_TIMEOUT_MS) {
    displayEnterIdle();
    isIdle = true;
  }

  displayTick();
}
