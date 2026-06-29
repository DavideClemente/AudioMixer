// TFT_eSPI pin config for GC9A01 on ESP32
// -----------------------------------------------------------------------
// If Arduino doesn't pick this up automatically, copy this file to your
// TFT_eSPI library folder (replacing the default User_Setup.h there).
// -----------------------------------------------------------------------

#define GC9A01_DRIVER

#define TFT_SCLK  18   // SCL  -> D18
#define TFT_MOSI  23   // SDA  -> D23
#define TFT_CS    14   // CS   -> D14
#define TFT_DC    27   // DC   -> D27
#define TFT_RST    4   // RST  -> D4  (D12/GPIO12 is a strapping pin — avoid it)
// BL (backlight) not defined — wire BL to 3.3V if screen stays dark

#define SPI_FREQUENCY  40000000   // 40 MHz
