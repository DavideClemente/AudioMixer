using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AudioMixerWin.Core.Services;

public sealed class EncodedGifFrame
{
    public ushort DelayMs { get; init; }
    public byte[] Rgb565 { get; init; } = Array.Empty<byte>();
}

/// <summary>
/// A GIF decoded into device-ready frames: square, RGB565 (little-endian),
/// plus per-frame delays. Sized for the controller's display.
/// </summary>
public sealed class EncodedGif
{
    public int Width { get; init; }
    public int Height { get; init; }
    public IReadOnlyList<EncodedGifFrame> Frames { get; init; } = Array.Empty<EncodedGifFrame>();

    // Raw pixel bytes the device must store (excludes the small header/delay table).
    public long PixelByteCount => (long)Width * Height * 2 * Frames.Count;
}

/// <summary>
/// Decodes a GIF into frames for the ESP32 idle screen. Each composited frame is
/// scaled "uniform-to-fill" and centre-cropped to a square target (matching the
/// app's UniformToFill preview), then converted to little-endian RGB565 — the
/// exact byte order the firmware reconstructs and pushes to the GC9A01 (same
/// packing as <see cref="AudioManager"/>'s icon path). When a GIF has more frames
/// than the cap, frames are sampled evenly and the dropped frames' delays are
/// folded into the kept ones so total runtime is preserved.
///
/// Uses System.Drawing, whose GIF decoder returns fully composited frames (it
/// handles GIF disposal/transparency for us).
/// </summary>
public static class GifFrameEncoder
{
    private const int PropertyTagFrameDelay = 0x5100; // GDI+ per-frame delay, in 1/100 s

    public static EncodedGif Encode(string filePath, int target, int maxFrames, int maxFps)
    {
        using var img = new Bitmap(filePath);

        int frameCount;
        try { frameCount = Math.Max(1, img.GetFrameCount(FrameDimension.Time)); }
        catch { frameCount = 1; } // not animated (or no time dimension)

        var delaysCs = ReadFrameDelaysCentis(img, frameCount);

        // Cap output frames by the storage budget AND a sustainable framerate: the
        // board can't render 240x240 from flash much above ~20fps, so sending more
        // frames just wastes flash and plays back slow. Resample to <= maxFps,
        // folding skipped frames' delays into the kept ones to keep total runtime.
        int cap = Math.Max(1, maxFrames);
        if (maxFps > 0)
        {
            long totalMs = 0;
            foreach (var cs in delaysCs) totalMs += cs * 10L;
            int byFps = (int)Math.Max(1, (totalMs * maxFps + 999) / 1000); // ceil(seconds * fps)
            cap = Math.Min(cap, byFps);
        }

        var keep = SelectFrameIndices(frameCount, cap);

        var frames = new List<EncodedGifFrame>(keep.Count);
        for (int k = 0; k < keep.Count; k++)
        {
            int start = keep[k];
            int end = (k + 1 < keep.Count) ? keep[k + 1] : frameCount;

            // Fold the delays of any frames we skipped into this kept frame.
            int delayCs = 0;
            for (int f = start; f < end; f++) delayCs += delaysCs[f];
            int delayMs = delayCs * 10;
            if (delayMs <= 0) delayMs = 100;

            if (frameCount > 1)
                img.SelectActiveFrame(FrameDimension.Time, start);

            frames.Add(new EncodedGifFrame
            {
                DelayMs = (ushort)Math.Clamp(delayMs, 10, 65535),
                Rgb565 = RenderFrameToRgb565(img, target),
            });
        }

        return new EncodedGif { Width = target, Height = target, Frames = frames };
    }

    private static int[] ReadFrameDelaysCentis(Bitmap img, int frameCount)
    {
        var delays = new int[frameCount];
        for (int i = 0; i < frameCount; i++) delays[i] = 10; // 100 ms default

        try
        {
            var prop = img.GetPropertyItem(PropertyTagFrameDelay);
            if (prop?.Value != null)
            {
                int n = Math.Min(frameCount, prop.Value.Length / 4);
                for (int i = 0; i < n; i++)
                {
                    int cs = BitConverter.ToInt32(prop.Value, i * 4);
                    delays[i] = cs > 0 ? cs : 10;
                }
            }
        }
        catch
        {
            // No delay metadata (e.g. a static GIF) — keep the defaults.
        }

        return delays;
    }

    // Evenly spaced source-frame indices, capped at maxFrames.
    private static List<int> SelectFrameIndices(int frameCount, int maxFrames)
    {
        var keep = new List<int>();
        if (frameCount <= maxFrames)
        {
            for (int i = 0; i < frameCount; i++) keep.Add(i);
            return keep;
        }

        for (int k = 0; k < maxFrames; k++)
        {
            int idx = (int)((long)k * frameCount / maxFrames);
            if (keep.Count == 0 || keep[^1] != idx) keep.Add(idx);
        }
        return keep;
    }

    // Scale uniform-to-fill, centre-crop to target×target, pack as little-endian RGB565.
    private static byte[] RenderFrameToRgb565(Bitmap frame, int target)
    {
        using var canvas = new Bitmap(target, target, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Black);

            double scale = Math.Max((double)target / frame.Width, (double)target / frame.Height);
            int dw = (int)Math.Ceiling(frame.Width * scale);
            int dh = (int)Math.Ceiling(frame.Height * scale);
            int dx = (target - dw) / 2;
            int dy = (target - dh) / 2;
            g.DrawImage(frame, dx, dy, dw, dh);
        }

        var data = canvas.LockBits(new Rectangle(0, 0, target, target),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bgra = new byte[data.Stride * target];
            Marshal.Copy(data.Scan0, bgra, 0, bgra.Length);

            var rgb565 = new byte[target * target * 2];
            for (int y = 0; y < target; y++)
            {
                int row = y * data.Stride;
                for (int x = 0; x < target; x++)
                {
                    int o = row + x * 4;             // BGRA
                    byte b = bgra[o], grn = bgra[o + 1], r = bgra[o + 2];
                    ushort px = (ushort)(((r >> 3) << 11) | ((grn >> 2) << 5) | (b >> 3));
                    int p = (y * target + x) * 2;
                    rgb565[p] = (byte)(px & 0xFF);   // LSB first — matches firmware
                    rgb565[p + 1] = (byte)(px >> 8);
                }
            }
            return rgb565;
        }
        finally { canvas.UnlockBits(data); }
    }
}
