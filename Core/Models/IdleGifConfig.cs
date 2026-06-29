using System;

namespace AudioMixerWin.Core.Models;

/// <summary>
/// Serializable metadata for one GIF in the idle-screen library. The binary
/// lives in the idle-gifs cache folder as {FileName}; this record is persisted
/// in settings.json.
/// </summary>
public class IdleGifConfig
{
    public string Id { get; set; } = "";
    public string FileName { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public DateTime AddedUtc { get; set; }
    public long SizeBytes { get; set; }
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
}
