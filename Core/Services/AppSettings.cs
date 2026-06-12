using System.Collections.Generic;
using AudioMixerWin.Core.Models;

namespace AudioMixerWin.Core.Services;

public class AppSettings
{
    public string ComPort { get; set; } = "COM3";
    public int BaudRate { get; set; } = 115200;
    public int RefreshIntervalSeconds { get; set; } = 2;
    public List<ChannelConfig> Channels { get; set; } = new();
    public List<string> ExcludedProcesses { get; set; } = new();
}
