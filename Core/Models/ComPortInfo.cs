using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;

namespace AudioMixerWin.Core.Models;

public partial class ComPortInfo
{
    public string Port { get; set; } = "";
    public string Description { get; set; } = "";
    public string DisplayText => string.IsNullOrEmpty(Description) ? Port : $"{Port}  —  {Description}";

    public static ComPortInfo[] GetPorts()
    {
        var descriptions = QueryPortDescriptions();
        var activePorts = SerialPort.GetPortNames();
        var allPorts = activePorts
            .Union(Enumerable.Range(1, 15).Select(i => $"COM{i}"), StringComparer.OrdinalIgnoreCase)
            .OrderBy(GetPortNumber);

        return allPorts.Select(p => new ComPortInfo
        {
            Port = p,
            Description = descriptions.GetValueOrDefault(p.ToUpperInvariant(), "")
        }).ToArray();
    }

    private static Dictionary<string, string> QueryPortDescriptions()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
            foreach (var obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString();
                if (name is null) continue;

                var match = ComPortRegex().Match(name);
                if (!match.Success) continue;

                var port = match.Groups[1].Value;
                var desc = name.Replace($"({port})", "").Trim();
                result[port] = desc;
            }
        }
        catch { }
        return result;
    }

    private static int GetPortNumber(string portName) =>
        int.TryParse(portName.AsSpan(3), out var number) ? number : int.MaxValue;

    [GeneratedRegex(@"\((COM\d+)\)")]
    private static partial Regex ComPortRegex();
}
