#pragma once
#include <Arduino.h>

static const int MAX_KNOBS   = 4;
static const int ICON_W      = 64;
static const int ICON_H      = 64;
static const int ICON_PIXELS = ICON_W * ICON_H;

extern char     knobLabel  [MAX_KNOBS][32];
extern uint16_t knobIcon   [MAX_KNOBS][ICON_PIXELS];
extern bool     knobHasIcon[MAX_KNOBS];
extern uint16_t knobColor[MAX_KNOBS];   // accent color, RGB565

bool handleAssignLine(const char* line);
bool handleIconLine  (const char* line);
