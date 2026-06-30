using System.Collections.Generic;
using AudioMixerWin.Core.Models;

namespace AudioMixerWin.Core.Services;

public class AppSettings
{
    public string ComPort { get; set; } = "COM3";
    public int BaudRate { get; set; } = 921600;
    public int RefreshIntervalSeconds { get; set; } = 2;
    public int IdleTimeoutSeconds { get; set; } = 3;
    public double NavPaneWidth { get; set; } = 320;
    public InputMode InputMode { get; set; } = InputMode.Potentiometer;
    public float EncoderStepPercent { get; set; } = 2f;
    public int KnobCount { get; set; } = 4;
    public List<ChannelConfig> Channels { get; set; } = new();
    public List<string> ExcludedProcesses { get; set; } = new();
    public bool DebugSerialEvents { get; set; } = false;
    public bool ShowPercentSign { get; set; } = false;
    public List<IdleGifConfig> IdleGifs { get; set; } = new();
    public string? ActiveIdleGifId { get; set; }

    // The two playback endpoints the hardware output switch toggles between.
    // Stored as Core Audio device ids (GUID strings); null until assigned.
    public string? OutputDeviceAId { get; set; }
    public string? OutputDeviceBId { get; set; }
}
