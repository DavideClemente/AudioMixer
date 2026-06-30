using System;
using System.Globalization;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AudioMixerWin.Core.Models;
using AudioMixerWin.Core.Services;

namespace AudioMixerWin.Core;

// Surfaced to the UI so an upload failure shows the actual cause, not a generic
// "failed" — the message is meant to be read by the user.
public class IdleGifUploadException : Exception
{
    public IdleGifUploadException(string message) : base(message) { }
}

public class SerialManager
{
    private readonly SerialPort _port;

    // Raw bytes per GIF-upload chunk. Its base64 (×4/3, ~5462 chars) plus the
    // "gif:d:" prefix must stay well under the firmware's serial RX buffer so a
    // chunk can't overrun it even if the device stalls briefly on a redraw.
    private const int GifChunkBytes = 4096;

    public event Action<string, float>? KnobChanged;
    public event Action<string, int>? KnobDelta;
    public event Action<string>? KnobPressed;

    // Position of the two-way output switch: 0 = A, 1 = B. The controller sends
    // "switch:0" / "switch:1" (or "switch:a" / "switch:b") whenever the toggle
    // moves; the app re-routes the Windows default output device in response.
    public event Action<int>? SwitchChanged;

    // Replies to the GIF-upload protocol ("gif:*") are routed here so the upload
    // coroutine can await them without racing the knob-event handlers.
    private readonly Channel<string> _gifResponses =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

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

    public void SendShowPercent(bool show)
    {
        if (!_port.IsOpen) return;
        try { _port.WriteLine($"config:pct:{(show ? 1 : 0)}"); }
        catch { }
    }

    public void SendMute(int knobIndex, bool muted)
    {
        if (!_port.IsOpen) return;
        try { _port.WriteLine($"mute:knob{knobIndex + 1}:{(muted ? 1 : 0)}"); }
        catch { }
    }

    // Tell the controller how long (ms) to wait with no knob activity before
    // it switches to its idle screen. Parsed by handleConfigLine on the device.
    public void SendIdleTimeout(int milliseconds)
    {
        if (!_port.IsOpen) return;
        try { _port.WriteLine($"cfg:idle:{Math.Max(0, milliseconds)}"); }
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
        // GIF upload acknowledgements are consumed by UploadIdleGifAsync, not the
        // knob pipeline. Route them and stop.
        if (cmd.StartsWith("gif:", StringComparison.Ordinal))
        {
            _gifResponses.Writer.TryWrite(cmd);
            return;
        }

        var parts = cmd.Split(':');
        if (parts.Length != 2)
            return;

        var knobId  = parts[0].Trim();
        var payload = parts[1].Trim();

        if (knobId == "switch")
        {
            if (payload is "0" or "a" or "A")
                SwitchChanged?.Invoke(0);
            else if (payload is "1" or "b" or "B")
                SwitchChanged?.Invoke(1);
            return;
        }

        if (payload == "up")
            KnobDelta?.Invoke(knobId, +1);
        else if (payload == "down")
            KnobDelta?.Invoke(knobId, -1);
        else if (payload == "press")
            KnobPressed?.Invoke(knobId);
        else if (float.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            KnobChanged?.Invoke(knobId, Math.Clamp(value, 0f, 1f));
    }

    // ── Idle-screen GIF upload ──────────────────────────────────────────────────
    // Line protocol, ACK-paced (one chunk in flight) so the ESP32's UART/flash
    // never falls behind. The device replies on the "gif:*" channel; see
    // Arduino/mixer/idlegif.cpp for the matching firmware side.

    private void DrainGifResponses()
    {
        while (_gifResponses.Reader.TryRead(out _)) { }
    }

    private async Task<string?> ReadGifResponseAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { return await _gifResponses.Reader.ReadAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; } // timeout
    }

    /// <summary>Bytes the controller has free for an idle GIF, or -1 if unknown.</summary>
    public async Task<long> QueryIdleGifSpaceAsync(CancellationToken ct = default)
    {
        if (!_port.IsOpen) return -1;
        DrainGifResponses();
        try { _port.WriteLine("gif:space?"); } catch { return -1; }

        var reply = await ReadGifResponseAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        if (reply != null && reply.StartsWith("gif:space:", StringComparison.Ordinal)
            && long.TryParse(reply.AsSpan("gif:space:".Length), out var bytes))
            return bytes;
        return -1;
    }

    /// <summary>
    /// Uploads the encoded GIF to the controller's flash, reporting 0..1 progress.
    /// Throws <see cref="IdleGifUploadException"/> with a user-readable reason on
    /// failure. The device keeps no half-written GIF — the firmware writes a temp
    /// file and only swaps it in on success.
    /// </summary>
    public async Task UploadIdleGifAsync(EncodedGif gif, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (!_port.IsOpen)
            throw new IdleGifUploadException("Controller is not connected.");
        if (gif.Frames.Count == 0)
            throw new IdleGifUploadException("The GIF has no frames to send.");

        DrainGifResponses();

        var delaysCsv = string.Join(",", System.Linq.Enumerable.Select(gif.Frames, f => f.DelayMs));
        WriteOrThrow($"gif:begin:{gif.Frames.Count}:{gif.Width}:{gif.Height}:{delaysCsv}");

        var rdy = await ReadGifResponseAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        if (rdy == null)
            throw new IdleGifUploadException(
                "No response from the controller. Check the cable and that the firmware baud rate matches (921600).");
        if (rdy == "gif:err")
            throw new IdleGifUploadException(
                "The controller rejected the upload — its firmware may have no storage partition. Flash a partition scheme that includes a filesystem (e.g. \"Default 4MB with spiffs\").");
        if (rdy != "gif:rdy")
            throw new IdleGifUploadException($"Unexpected controller response: {rdy}");

        long totalBytes = gif.PixelByteCount;
        long sentBytes = 0;
        var chunk = new byte[GifChunkBytes];
        int chunkFill = 0;

        async Task FlushChunkAsync()
        {
            if (chunkFill == 0) return;
            var b64 = Convert.ToBase64String(chunk, 0, chunkFill);
            WriteOrThrow("gif:d:" + b64);
            var ack = await ReadGifResponseAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
            if (ack == null)
                throw new IdleGifUploadException("The controller stopped responding during transfer (timed out).");
            if (ack == "gif:err")
                throw new IdleGifUploadException("The controller reported an error during transfer — its storage may be full.");
            if (ack != "gif:ack")
                throw new IdleGifUploadException($"Unexpected controller response: {ack}");
            sentBytes += chunkFill;
            chunkFill = 0;
            progress?.Report(totalBytes > 0 ? (double)sentBytes / totalBytes : 1);
        }

        foreach (var frame in gif.Frames)
        {
            var px = frame.Rgb565;
            int offset = 0;
            while (offset < px.Length)
            {
                int take = Math.Min(chunk.Length - chunkFill, px.Length - offset);
                Array.Copy(px, offset, chunk, chunkFill, take);
                chunkFill += take;
                offset += take;
                if (chunkFill == chunk.Length)
                    await FlushChunkAsync().ConfigureAwait(false);
            }
        }
        await FlushChunkAsync().ConfigureAwait(false);

        WriteOrThrow("gif:end");
        var done = await ReadGifResponseAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        if (done == null)
            throw new IdleGifUploadException("The controller stopped responding before confirming the upload.");
        if (done != "gif:done")
            throw new IdleGifUploadException("The upload did not verify on the controller (size mismatch or storage full).");
    }

    private void WriteOrThrow(string line)
    {
        try { _port.WriteLine(line); }
        catch (Exception ex) { throw new IdleGifUploadException($"Serial write failed: {ex.Message}"); }
    }

    /// <summary>Removes the stored idle GIF so the controller reverts to its built-in animation.</summary>
    public void ClearIdleGif()
    {
        if (!_port.IsOpen) return;
        try { _port.WriteLine("gif:clear"); } catch { }
    }
}
