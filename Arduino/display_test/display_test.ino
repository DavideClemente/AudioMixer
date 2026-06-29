#include <TFT_eSPI.h>
#include <SPI.h>

TFT_eSPI tft = TFT_eSPI();

static const int CX = 120;
static const int CY = 120;

void setup() {
  Serial.begin(115200);

  tft.init();
  tft.setRotation(0);
  tft.fillScreen(TFT_BLACK);

  // Decorative ring (round display)
  tft.drawCircle(CX, CY, 115, TFT_WHITE);
  tft.drawCircle(CX, CY, 114, 0x4208); // subtle inner ring (dark grey)

  // Centered text
  tft.setTextDatum(MC_DATUM);
  tft.setTextColor(TFT_WHITE, TFT_BLACK);
  tft.setTextSize(2);
  tft.drawString("Display OK", CX, CY - 12);
  tft.setTextSize(1);
  tft.drawString("GC9A01 Ready", CX, CY + 14);

  Serial.println("Display initialized");
}

void loop() {
  // Pulse a small dot through colors to confirm the display is live
  static const uint16_t colors[] = {
    TFT_RED, TFT_GREEN, TFT_BLUE, TFT_YELLOW, TFT_CYAN, TFT_MAGENTA
  };
  static const int NUM_COLORS = sizeof(colors) / sizeof(colors[0]);
  static int idx = 0;
  static uint32_t lastTick = 0;

  if (millis() - lastTick >= 800) {
    tft.fillCircle(CX, CY + 45, 10, colors[idx % NUM_COLORS]);
    idx++;
    lastTick = millis();
  }
}
