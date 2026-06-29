#pragma once

// value: absolute 0.0–1.0 for pots, +1.0/-1.0 delta for encoders
typedef void (*KnobCallback)(const char* id, float value);

void knobsSetup(KnobCallback cb);
void knobsLoop();
