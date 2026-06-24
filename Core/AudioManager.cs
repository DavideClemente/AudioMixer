using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using AudioMixerWin.Core.Models;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NAudio.CoreAudioApi;

namespace AudioMixerWin.Core;

public class AudioManager
    {
        public const string MasterVolumeProcessName = "System Volume";

        public static string GetDisplayName(string processName)
        {
            if (processName.Equals(MasterVolumeProcessName, StringComparison.OrdinalIgnoreCase))
                return "System";

            return processName.Length > 0
                ? char.ToUpperInvariant(processName[0]) + processName[1..]
                : processName;
        }

        private readonly MMDevice _device;
        private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, byte[]> _iconRgb565Cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (byte R, byte G, byte B)> _iconColorCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly (byte R, byte G, byte B) DefaultAccent = (0, 200, 255);

        public AudioManager()
        {
            var enumerator = new MMDeviceEnumerator();
            _device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        public List<AudioSession> GetSessions()
        {
            var result = new List<AudioSession>();

            _device.AudioSessionManager.RefreshSessions();
            var sessions = _device.AudioSessionManager.Sessions;

            Console.WriteLine("Audio sessions detected:\n");

            for (var i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];

                try
                {
                    var pid = (int)session.GetProcessID;
                    var process = Process.GetProcessById(pid);

                    result.Add(new AudioSession
                    {
                        ProcessName = process.ProcessName,
                        DisplayName = GetDisplayName(process.ProcessName),
                        Volume = session.SimpleAudioVolume.Volume,
                        IconSource = GetIconForProcess(process),
                    });

                    Console.WriteLine(
                        $"{process.ProcessName} - Volume: {session.SimpleAudioVolume.Volume}"
                    );
                }
                catch
                {
                    Console.WriteLine($"System session - Volume: {session.SimpleAudioVolume.Volume}");
                }
            }

            result.Add(new AudioSession
            {
                ProcessName = MasterVolumeProcessName,
                DisplayName = GetDisplayName(MasterVolumeProcessName),
                Volume = GetMasterVolume(),
            });

            return result
                .GroupBy(x => x!.ProcessName)
                .Select(g => g.First()!)
                .OrderBy(x => x.ProcessName)
                .ToList();;
        }

        private ImageSource? GetIconForProcess(Process process)
        {
            if (_iconCache.TryGetValue(process.ProcessName, out var cached))
                return cached;

            ImageSource? icon = null;
            try
            {
                var path = process.MainModule?.FileName;
                if (path is not null)
                {
                    using var extracted = Icon.ExtractAssociatedIcon(path);
                    if (extracted is not null)
                        icon = ConvertIconToBitmapImage(extracted);
                }
            }
            catch
            {
                // Some processes (elevated, different bitness, etc.) deny access to MainModule.
            }

            _iconCache[process.ProcessName] = icon;
            return icon;
        }

        public byte[] GetIconRgb565(string processName)
        {
            if (_iconRgb565Cache.TryGetValue(processName, out var cached))
                return cached;

            byte[] result = Array.Empty<byte>();
            try
            {
                var procs = Process.GetProcessesByName(processName);
                if (procs.Length > 0)
                {
                    var path = procs[0].MainModule?.FileName;
                    if (path is not null)
                    {
                        using var icon = Icon.ExtractAssociatedIcon(path);
                        if (icon is not null)
                            result = ConvertIconToRgb565(icon);
                    }
                }
            }
            catch { }

            _iconRgb565Cache[processName] = result;
            return result;
        }

        public (byte R, byte G, byte B) GetIconColor(string processName)
        {
            if (_iconColorCache.TryGetValue(processName, out var cached))
                return cached;

            var result = DefaultAccent;
            try
            {
                if (TryGetIconArgb(processName, out var argb))
                    result = ComputeDominantColor(argb);
            }
            catch { }

            _iconColorCache[processName] = result;
            return result;
        }

        // Returns the 64x64 icon as raw BGRA bytes (Format32bppArgb memory order: B,G,R,A).
        private static bool TryGetIconArgb(string processName, out byte[] argb)
        {
            argb = Array.Empty<byte>();
            var procs = Process.GetProcessesByName(processName);
            if (procs.Length == 0) return false;

            var path = procs[0].MainModule?.FileName;
            if (path is null) return false;

            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon is null) return false;

            const int size = 64;
            using var bmp = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                using var srcBmp = icon.ToBitmap();
                g.DrawImage(srcBmp, 0, 0, size, size);
            }

            var data = bmp.LockBits(
                new System.Drawing.Rectangle(0, 0, size, size),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                argb = new byte[data.Stride * size];
                Marshal.Copy(data.Scan0, argb, 0, argb.Length);
            }
            finally { bmp.UnlockBits(data); }
            return true;
        }

        // Coarse-histogram dominant color: skip transparent/near-gray/near-dark pixels,
        // bucket the rest (3 bits/channel), return the average of the most populated bucket.
        private static (byte R, byte G, byte B) ComputeDominantColor(byte[] argb)
        {
            var counts = new Dictionary<int, int>();
            var sums = new Dictionary<int, (long R, long G, long B, int N)>();
            int pixels = argb.Length / 4;

            for (int i = 0; i < pixels; i++)
            {
                int o = i * 4;
                byte b = argb[o], g = argb[o + 1], r = argb[o + 2], a = argb[o + 3];
                if (a < 128) continue;

                int max = Math.Max(r, Math.Max(g, b));
                int min = Math.Min(r, Math.Min(g, b));
                int sat = max == 0 ? 0 : (max - min) * 255 / max;
                if (sat < 40 || max < 40) continue; // skip gray and very dark pixels

                int key = ((r >> 5) << 6) | ((g >> 5) << 3) | (b >> 5);
                counts.TryGetValue(key, out int c);
                counts[key] = c + 1;
                sums.TryGetValue(key, out var s);
                sums[key] = (s.R + r, s.G + g, s.B + b, s.N + 1);
            }

            if (counts.Count == 0) return DefaultAccent;

            int best = counts.OrderByDescending(kv => kv.Value).First().Key;
            var bs = sums[best];
            return ((byte)(bs.R / bs.N), (byte)(bs.G / bs.N), (byte)(bs.B / bs.N));
        }

        private static byte[] ConvertIconToRgb565(Icon icon)
        {
            const int size = 64;
            using var bmp = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            using var srcBmp = icon.ToBitmap();
            g.DrawImage(srcBmp, 0, 0, size, size);

            var data = bmp.LockBits(
                new System.Drawing.Rectangle(0, 0, size, size),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            try
            {
                var raw = new byte[data.Stride * size];
                Marshal.Copy(data.Scan0, raw, 0, raw.Length);

                var rgb565 = new byte[size * size * 2];
                for (int i = 0; i < size * size; i++)
                {
                    int o = i * 4;
                    // Format32bppArgb in memory: B, G, R, A
                    byte b = raw[o];
                    byte grn = raw[o + 1];
                    byte r = raw[o + 2];
                    byte a = raw[o + 3];
                    // Alpha-blend onto black
                    r = (byte)(r * a / 255);
                    grn = (byte)(grn * a / 255);
                    b = (byte)(b * a / 255);

                    ushort px = (ushort)(((r >> 3) << 11) | ((grn >> 2) << 5) | (b >> 3));
                    rgb565[i * 2]     = (byte)(px & 0xFF);   // LSB first
                    rgb565[i * 2 + 1] = (byte)(px >> 8);
                }
                return rgb565;
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        private static WriteableBitmap ConvertIconToBitmapImage(Icon icon)
        {
            using var bitmap = icon.ToBitmap();
            var width = bitmap.Width;
            var height = bitmap.Height;

            var bitmapData = bitmap.LockBits(
                new Rectangle(0, 0, width, height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);

            try
            {
                var bytes = new byte[bitmapData.Stride * height];
                Marshal.Copy(bitmapData.Scan0, bytes, 0, bytes.Length);

                var writeableBitmap = new WriteableBitmap(width, height);
                using var pixelStream = writeableBitmap.PixelBuffer.AsStream();
                pixelStream.Write(bytes, 0, bytes.Length);

                return writeableBitmap;
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }
        }
        
        public float GetMasterVolume() => _device.AudioEndpointVolume.MasterVolumeLevelScalar;

        public void SetMasterVolume(float volume) =>
            _device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume, 0f, 1f);

        public float GetVolume(string processName)
        {
            if (processName.Equals(MasterVolumeProcessName, StringComparison.OrdinalIgnoreCase))
                return GetMasterVolume();

            var sessions = _device.AudioSessionManager.Sessions;

            for (var i = 0; i < sessions.Count; i++)
            {
                try
                {
                    var session = sessions[i];

                    var pid = (int)session.GetProcessID;
                    var process = Process.GetProcessById(pid);

                    if (process.ProcessName.Equals(
                            processName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return session.SimpleAudioVolume.Volume;
                    }
                }
                catch
                {
                    // ignored
                }
            }

            return 0;
        }
        
        public void SetVolume(string processName, float volume)
        {
            if (processName.Equals(MasterVolumeProcessName, StringComparison.OrdinalIgnoreCase))
            {
                SetMasterVolume(volume);
                return;
            }

            var sessions = _device.AudioSessionManager.Sessions;

            for (var i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];

                try
                {
                    var pid = (int)session.GetProcessID;
                    var process = Process.GetProcessById(pid);

                    if (process.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
                    {
                        session.SimpleAudioVolume.Volume = Math.Clamp(volume, 0f, 1f);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Session error: {ex.Message}");
                }
            }
        }

        public bool GetMute(string processName)
        {
            if (processName.Equals(MasterVolumeProcessName, StringComparison.OrdinalIgnoreCase))
                return _device.AudioEndpointVolume.Mute;

            var sessions = _device.AudioSessionManager.Sessions;

            for (var i = 0; i < sessions.Count; i++)
            {
                try
                {
                    var session = sessions[i];
                    var pid = (int)session.GetProcessID;
                    var process = Process.GetProcessById(pid);

                    if (process.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
                        return session.SimpleAudioVolume.Mute;
                }
                catch { }
            }

            return false;
        }

        public void SetMute(string processName, bool muted)
        {
            if (processName.Equals(MasterVolumeProcessName, StringComparison.OrdinalIgnoreCase))
            {
                _device.AudioEndpointVolume.Mute = muted;
                return;
            }

            var sessions = _device.AudioSessionManager.Sessions;

            for (var i = 0; i < sessions.Count; i++)
            {
                try
                {
                    var session = sessions[i];
                    var pid = (int)session.GetProcessID;
                    var process = Process.GetProcessById(pid);

                    if (process.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
                        session.SimpleAudioVolume.Mute = muted;
                }
                catch { }
            }
        }
    }