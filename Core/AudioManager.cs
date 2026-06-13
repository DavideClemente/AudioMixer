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

        private readonly MMDevice _device;
        private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

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
                        DisplayName = process.ProcessName,
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
                DisplayName = MasterVolumeProcessName,
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
    }