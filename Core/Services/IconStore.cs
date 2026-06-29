using System;
using System.IO;
using System.Linq;

namespace AudioMixerWin.Core.Services;

/// <summary>
/// Persists extracted app icons to disk so they survive app restarts even when
/// the source process is no longer running. Each icon is stored as a raw 64x64
/// straight-alpha BGRA buffer (Format32bppArgb memory order), one file per process.
/// </summary>
public static class IconStore
{
    public const int IconSize = 64;
    private const int ByteCount = IconSize * IconSize * 4;

    private static readonly string Directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AudioMixerWin", "icons");

    public static void Save(string processName, byte[] bgra)
    {
        if (bgra.Length != ByteCount)
            return;

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllBytes(PathFor(processName), bgra);
        }
        catch { }
    }

    public static byte[]? Load(string processName)
    {
        try
        {
            var path = PathFor(processName);
            if (File.Exists(path))
            {
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length == ByteCount)
                    return bytes;
            }
        }
        catch { }

        return null;
    }

    private static string PathFor(string processName)
    {
        var safe = string.Concat(processName.Select(c =>
            Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(Directory, safe + ".bgra");
    }
}
