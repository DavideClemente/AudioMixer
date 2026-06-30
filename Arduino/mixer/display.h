#pragma once

void displaySetup();
void displayShowKnob(int knobIndex, float value);
void displayShowMute(int knobIndex, bool muted);
void displaySetShowPercent(bool show);
void displayEnterIdle();
void displayTick();

// GIF-upload progress screen (driven by idlegif.cpp while a new GIF flashes).
void displayUploadBegin();
void displayUploadProgress(float frac);   // 0..1
void displayUploadEnd(bool ok);
