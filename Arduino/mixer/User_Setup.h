// TFT_eSPI pin config for GC9A01 on ESP32
// -----------------------------------------------------------------------
// Copy this file to your TFT_eSPI library folder, replacing the default
// User_Setup.h there (it overrides the whole file, so fonts must be
// listed here — without LOAD_GLCD, drawString() silently does nothing).
// -----------------------------------------------------------------------

#define GC9A01_DRIVER

#define TFT_SCLK  18   // SCL  -> D18
#define TFT_MOSI  23   // SDA  -> D23
#define TFT_CS    14   // CS   -> D14
#define TFT_DC    27   // DC   -> D27
#define TFT_RST    4   // RST  -> D4  (D12/GPIO12 is a strapping pin — avoid it)
// BL (backlight) not defined — wire BL to 3.3V if screen stays dark

#define SPI_FREQUENCY  40000000   // 40 MHz

// Fonts — required; without these drawString() renders nothing
#define LOAD_GLCD    // Font 1: default 5x7 pixel font (used by setTextSize)
#define LOAD_FONT2   // Font 2: 16px
#define LOAD_FONT4   // Font 4: 26px
#define LOAD_GFXFF   // Adafruit GFX free fonts
#define SMOOTH_FONT
