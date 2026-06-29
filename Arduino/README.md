# AudioMixer — controller firmware

Firmware for the physical knob controller that drives the AudioMixerWin desktop app. The board
reads pots/encoders and reports them to the PC over USB serial; the PC sets per-app Windows
volumes and (on display-capable boards) echoes back state to render on screen.

There are three firmware **tiers** so the same project works on different hardware. Pick the
folder that matches your board, open its `.ino` in the Arduino IDE (or build with `arduino-cli`),
and flash it.

| Folder         | Board                       | Display                              | Features                                                   |
|----------------|-----------------------------|--------------------------------------|------------------------------------------------------------|
| `mixer/`       | ESP32 (e.g. WROOM)          | GC9A01 240×240 round TFT (TFT_eSPI)  | Full per-app **icons**, accent color, animated arc + idle  |
| `mixer_nano/`  | Arduino Nano / Uno / Pro Mini (ATmega328P) | 1.3" 128×64 I2C OLED (SH1106, U8g2)  | App **name + % + bar gauge** (monochrome). No icons.       |
| `arduino/`     | Any (ESP32/AVR)             | none                                 | Knobs only — minimal sketch, no screen                     |
| `display_test/`| ESP32                       | GC9A01                               | Display bring-up test only                                 |

The tiers exist because of RAM: the ESP32 build stores 64×64 RGB565 icons (32 KB) and a frame
sprite (~16 KB), which cannot fit an ATmega328P's 2 KB SRAM. The Nano build is a slimmed copy of
the same module layout that drops icons/sprites and discards the icon data the PC sends.

## Serial protocol (shared by all tiers)

115200 baud, newline-terminated, `CultureInfo.InvariantCulture` floats.

**Board → PC**
- `knob1:0.42` — potentiometer absolute level, 0.00–1.00
- `knob1:up` / `knob1:down` — encoder detent
- `knob1:press` — encoder push switch

**PC → Board** (display tiers; knobs-only/Nano ignore what they can't use)
- `vol:knob1:0.42` — authoritative volume echo; drives the on-device gauge
- `assign:knob1:RRGGBB:AppName` — label + accent color for a knob
- `icon:knob1:<base64>` — 64×64 RGB565 icon (~11 KB). **ESP32 only**; the Nano build discards
  these lines without buffering them.

Knob ids are 1-based (`knob1`…`knobN`); `MAX_KNOBS` in firmware must cover the highest index used.

---

## `mixer_nano/` — Arduino Nano (ATmega328P)

**Libraries:** [U8g2](https://github.com/olikraus/u8g2) (install via Library Manager).

**Wiring (defaults in the sketch):**

| Function          | Pin(s)        | Notes                                                        |
|-------------------|---------------|-------------------------------------------------------------|
| OLED SDA / SCL    | A4 / A5       | Hardware I2C (fixed on the 328P). OLED `VDD`→5V, `GND`→GND.  |
| Pots              | A0, A1 (…A3)  | Add more in `pots[]`; A6/A7 are also ADC-capable.           |
| Encoder CLK / DT  | D2 / D3       | The only interrupt pins on the 328P. (when `USE_ENCODER 1`) |
| Encoder switch    | D4            | Any spare digital pin.                                       |

Choose pots vs encoders with `#define USE_ENCODER` at the top of `knobs.cpp` (0 = pots, 1 =
encoder). Edit `pots[]` / `encoders[]` to match your build.

**Display note:** 1.3" panels are usually SH1106. If text is shifted ~2px or garbled on the right
edge, your panel is an SSD1306 — change the constructor in `display.cpp` to
`U8G2_SSD1306_128X64_NONAME_F_HW_I2C`.

**Build:**
```
arduino-cli compile --fqbn arduino:avr:nano:cpu=atmega328 Arduino/mixer_nano
```
Use `cpu=atmega328old` if upload fails on an older bootloader. Watch the SRAM report — the U8g2
full buffer is ~1 KB; if you run low, switch `display.cpp` to a page-buffer constructor
(`..._2_HW_I2C`) and a `firstPage()`/`nextPage()` render loop.

## `mixer/` — ESP32

**Libraries:** TFT_eSPI configured for GC9A01. Copy `mixer/User_Setup.h` into your TFT_eSPI
library folder (it overrides the library's default; see the header comment for pin mapping).

**Build:**
```
arduino-cli compile --fqbn esp32:esp32:esp32 Arduino/mixer
```
