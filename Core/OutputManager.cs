using System;
using System.Collections.Generic;
using System.Linq;
using AudioMixerWin.Core.Models;
using AudioSwitcher.AudioApi;
using AudioSwitcher.AudioApi.CoreAudio;

namespace AudioMixerWin.Core;

// Enumerates the active playback endpoints and changes the Windows default
// output device. NAudio (used by AudioManager for session volume) can read
// devices but not set the default, so this wraps AudioSwitcher's
// CoreAudioController, which drives the undocumented IPolicyConfig for us.
public class OutputManager
{
    private readonly CoreAudioController _controller = new();

    public IReadOnlyList<OutputDevice> GetOutputDevices()
    {
        var defaultId = _controller.DefaultPlaybackDevice?.Id;

        return _controller.GetPlaybackDevices(DeviceState.Active)
            .OrderBy(d => d.FullName)
            .Select(d => new OutputDevice
            {
                Id = d.Id.ToString(),
                Name = d.FullName,
                IsDefault = d.Id == defaultId,
            })
            .ToList();
    }

    public string? DefaultDeviceId => _controller.DefaultPlaybackDevice?.Id.ToString();

    // Makes the endpoint with this id the default for both multimedia and
    // communications roles. Returns false if the id is unknown/unparseable or
    // the device is no longer present.
    public bool SetDefault(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId) || !Guid.TryParse(deviceId, out var guid))
            return false;

        var device = _controller.GetDevice(guid);
        if (device is null || !device.IsPlaybackDevice)
            return false;

        var ok = device.SetAsDefault();
        device.SetAsDefaultCommunications();
        return ok;
    }
}
