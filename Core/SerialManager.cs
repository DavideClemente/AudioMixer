using System;
using System.Globalization;
using System.IO.Ports;
using AudioMixerWin.Core.Models;

namespace AudioMixerWin.Core;

public class SerialManager
{
    private readonly SerialPort _port;

    public event Action<string, float>? KnobChanged;
    public event Action<string, int>? KnobDelta;
    public event Action<string>? KnobPressed;

    public SerialManager(string comPort, int baudRate)
    {
        _port = new SerialPort(comPort, baudRate);
        _port.DataReceived += OnData;
    }

    public bool IsConnected => _port.IsOpen;

    public void Start()
    {
        _port.Open();
    }

    public void Stop()
    {
        try
        {
            _port.DataReceived -= OnData;
            if (_port.IsOpen)
                _port.Close();
            _port.Dispose();
        }
        catch { }
    }

    public void SendVolume(int knobIndex, float volume)
    {
        if (!_port.IsOpen) return;
        try { _port.WriteLine($"vol:knob{knobIndex + 1}:{volume.ToString("F2", CultureInfo.InvariantCulture)}"); }
        catch { }
    }

    public void SendAssignment(int knobIndex, string appName, (byte R, byte G, byte B) color, byte[] iconRgb565)
    {
        if (!_port.IsOpen) return;
        try
        {
            var knobId = $"knob{knobIndex + 1}";
            var hex = $"{color.R:X2}{color.G:X2}{color.B:X2}";
            var safeName = appName.Replace("\r", "").Replace("\n", "");
            _port.WriteLine($"assign:{knobId}:{hex}:{safeName}");
            if (iconRgb565.Length > 0)
                _port.WriteLine($"icon:{knobId}:{Convert.ToBase64String(iconRgb565)}");
        }
        catch { }
    }

    private void OnData(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            var line = _port.ReadLine().Trim();
            HandleCommand(line);
        }
        catch { }
    }

    private void HandleCommand(string cmd)
    {
        var parts = cmd.Split(':');
        if (parts.Length != 2)
            return;

        var knobId  = parts[0].Trim();
        var payload = parts[1].Trim();

        if (payload == "up")
            KnobDelta?.Invoke(knobId, +1);
        else if (payload == "down")
            KnobDelta?.Invoke(knobId, -1);
        else if (payload == "press")
            KnobPressed?.Invoke(knobId);
        else if (float.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            KnobChanged?.Invoke(knobId, Math.Clamp(value, 0f, 1f));
    }
}
