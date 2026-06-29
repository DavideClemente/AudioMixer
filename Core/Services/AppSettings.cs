using System.Collections.Generic;
using AudioMixerWin.Core.Models;

namespace AudioMixerWin.Core.Services;

public class AppSettings
{
    public string ComPort { get; set; } = "COM3";
    public int BaudRate { get; set; } = 115200;
    public int RefreshIntervalSeconds { get; set; } = 2;
    public double NavPaneWidth { get; set; } = 320;
    public InputMode InputMode { get; set; } = InputMode.Potentiometer;
    public float EncoderStepPercent { get; set; } = 2f;
    public List<ChannelConfig> Channels { get; set; } = new();
    public List<string> ExcludedProcesses { get; set; } = new();
    public bool DebugSerialEvents { get; set; } = false;
}
