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
