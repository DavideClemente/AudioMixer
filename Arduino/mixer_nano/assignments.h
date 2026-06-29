#pragma once
#include <Arduino.h>

// MAX_KNOBS must cover the highest knob index the PC may address (knob1..knobN).
// Each label costs 32 B of RAM, so keep this to the real knob count on a 2 KB Nano.
static const int MAX_KNOBS = 4;

extern char     knobLabel[MAX_KNOBS][32];
extern uint16_t knobColor[MAX_KNOBS];   // accent color, RGB565 (unused on mono OLED)

// Parse "assign:knob1:RRGGBB:AppName". Returns true if it was an assign line.
// NOTE: there is deliberately no handleIconLine here — the Nano cannot store
// 64x64 icons in RAM, so icon: lines are discarded by the serial reader instead.
bool handleAssignLine(const char* line);
