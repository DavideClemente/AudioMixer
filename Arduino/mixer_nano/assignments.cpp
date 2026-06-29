#include "assignments.h"

char     knobLabel[MAX_KNOBS][32] = {};
uint16_t knobColor[MAX_KNOBS]     = { 0x065F, 0x065F, 0x065F, 0x065F };  // default accent

// Parse "assign:knob1:RRGGBB:AppName"
bool handleAssignLine(const char* line) {
  if (strncmp(line, "assign:", 7) != 0) return false;
  const char* p = line + 7;                       // "knob1:RRGGBB:AppName"
  if (strncmp(p, "knob", 4) != 0) return false;

  int idx = atoi(p + 4) - 1;                       // 1-based -> 0-based
  if (idx < 0 || idx >= MAX_KNOBS) return false;

  const char* c1 = strchr(p, ':');                 // after "knobN"
  if (!c1) return false;
  const char* colorStr = c1 + 1;                   // "RRGGBB:AppName"
  const char* c2 = strchr(colorStr, ':');          // after color
  if (!c2) return false;

  if (c2 - colorStr >= 6) {
    char hex[7];
    memcpy(hex, colorStr, 6);
    hex[6] = '\0';
    long rgb = strtol(hex, nullptr, 16);
    uint8_t r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
    knobColor[idx] = ((r & 0xF8) << 8) | ((g & 0xFC) << 3) | (b >> 3);
  }

  strncpy(knobLabel[idx], c2 + 1, 31);
  knobLabel[idx][31] = '\0';
  return true;
}
