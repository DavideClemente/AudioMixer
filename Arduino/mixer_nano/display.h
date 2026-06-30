#pragma once

// Same public API as the ESP32 build (Arduino/mixer/display.h) so the sketch
// structure is identical across boards. The implementation here targets a
// monochrome 128x64 SH1106 I2C OLED via U8g2 — name + percentage + bar gauge,
// no icons, no sprites.
void displaySetup();
void displayShowKnob(int knobIndex, float value);
void displayShowMute(int knobIndex, bool muted);
void displaySetShowPercent(bool show);
void displayEnterIdle();
void displayTick();
