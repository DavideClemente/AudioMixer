using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioMixerWin.Core.Models;

// A Windows playback (render) endpoint the switch can route audio to. Id is the
// Core Audio device id (a GUID string) — stable across reboots, so it's what we
// persist for each switch position rather than the friendly name (which can
// collide or change).
public partial class OutputDevice : ObservableObject
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    // Whether this endpoint is currently the system default; drives the "live"
    // marker in the picker and is refreshed after every route change.
    [ObservableProperty]
    private bool isDefault;
}
