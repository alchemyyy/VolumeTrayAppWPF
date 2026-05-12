using System.Runtime.InteropServices;
using VolumeTrayAppWPF.Audio.Interop;

namespace VolumeTrayAppWPF.Audio;

/// <summary>
/// Plays a short PCM wav through a specific render endpoint via WASAPI shared mode.
/// Used by the device-volume-change feedback so the ding comes out of the device whose slider
/// the user just moved instead of the system default. Capture endpoints are not addressable here -
/// the caller is responsible for skipping them.
/// Best-effort: any failure (endpoint just disconnected, format rejected, etc.) is swallowed.
/// </summary>
internal static class EndpointSoundPlayback
{
    // Engine buffer hint to IAudioClient.Initialize, in 100-ns ticks. Long enough to hold our short
    // feedback wav comfortably; the audio service may round to its own period internally.
    private const long BufferDurationHns = 2_000_000;

    // Padding-poll slice during the drain loop. Tens-of-ms scale is fine - the wav is ~1 second
    // and the user can't perceive sub-frame timing on a confirmation ding.
    private const int PollSliceMs = 30;

    // Hard ceiling on the drain loop. Default Windows feedback wavs are well under a second; this
    // covers worst-case engine latency on a slow / contested system without ever stranding a worker.
    private const int MaxDrainMs = 5000;

    /// <summary>
    /// Fires the playback on a threadpool worker and returns immediately. We take the endpoint id
    /// as a string (not an IMMDevice RCW) so the worker can re-acquire the device on its own MTA
    /// thread - the AudioDevice's IMMDevice RCW is bound to the WPF UI-thread STA and QueryInterface
    /// fails if we marshal it across apartments. The worker owns every COM proxy it creates.
    /// </summary>
    public static void PlayAsync(string deviceId, byte[] wavBytes)
    {
        if (string.IsNullOrEmpty(deviceId) || wavBytes == null || wavBytes.Length < 44) return;
        Task.Run(() => Play(deviceId, wavBytes));
    }

    private static void Play(string deviceId, byte[] wavBytes)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? client = null;
        IAudioRenderClient? render = null;
        IntPtr formatPtr = IntPtr.Zero;
        try
        {
            if (!TryParseWav(wavBytes, out int channels, out int samplesPerSec, out int bitsPerSample,
                             out int blockAlign, out int dataOffset, out int dataLength))
                return;

            // Fresh enumerator + IMMDevice on this worker thread so the COM object lives in the same
            // apartment we'll be calling from. Re-acquiring is cheap (in-proc, microseconds).
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            enumerator.GetDevice(deviceId, out device);
            if (device == null) return;

            int hr = device.Activate(typeof(IAudioClient).GUID, ClsCtx.ALL, IntPtr.Zero, out object? clientObj);
            if (hr < 0 || clientObj == null) return;
            client = (IAudioClient)clientObj;

            // Synthesize a clean 18-byte WAVEFORMATEX matching the source PCM. Avoids feeding the
            // engine any extra fields that may sit after cbSize in the file's fmt chunk.
            byte[] format = new byte[18];
            BitConverter.GetBytes((ushort)1).CopyTo(format, 0); // WAVE_FORMAT_PCM
            BitConverter.GetBytes((ushort)channels).CopyTo(format, 2);
            BitConverter.GetBytes((uint)samplesPerSec).CopyTo(format, 4);
            BitConverter.GetBytes((uint)(samplesPerSec * blockAlign)).CopyTo(format, 8);
            BitConverter.GetBytes((ushort)blockAlign).CopyTo(format, 12);
            BitConverter.GetBytes((ushort)bitsPerSample).CopyTo(format, 14);
            BitConverter.GetBytes((ushort)0).CopyTo(format, 16); // cbSize

            formatPtr = Marshal.AllocHGlobal(format.Length);
            Marshal.Copy(format, 0, formatPtr, format.Length);

            uint streamFlags = AudioClientStreamFlags.NoPersist
                             | AudioClientStreamFlags.AutoConvertPcm
                             | AudioClientStreamFlags.SrcDefaultQuality;
            hr = client.Initialize(AudioClientShareMode.Shared, streamFlags,
                                   BufferDurationHns, 0, formatPtr, IntPtr.Zero);
            if (hr < 0) { WPFLog.Log($"EndpointSoundPlayback.Initialize: hr=0x{hr:X8}"); return; }

            hr = client.GetBufferSize(out uint bufferFrames);
            if (hr < 0 || bufferFrames == 0) return;

            hr = client.GetService(typeof(IAudioRenderClient).GUID, out object? renderObj);
            if (hr < 0 || renderObj == null) return;
            render = (IAudioRenderClient)renderObj;

            int byteCursor = 0;
            int totalBytes = dataLength;

            // Initial fill before Start so the engine never plays a glitch of silence.
            byteCursor += FillBuffer(render, bufferFrames, wavBytes, dataOffset + byteCursor,
                                     totalBytes - byteCursor, blockAlign);

            hr = client.Start();
            if (hr < 0) return;

            int waited = 0;
            while (waited < MaxDrainMs)
            {
                Thread.Sleep(PollSliceMs);
                waited += PollSliceMs;

                if (client.GetCurrentPadding(out uint padding) < 0) break;
                if (byteCursor >= totalBytes && padding == 0) break;
                if (byteCursor < totalBytes)
                {
                    uint freeFrames = bufferFrames > padding ? bufferFrames - padding : 0;
                    if (freeFrames > 0)
                    {
                        byteCursor += FillBuffer(render, freeFrames, wavBytes, dataOffset + byteCursor,
                                                 totalBytes - byteCursor, blockAlign);
                    }
                }
            }

            try { client.Stop(); } catch { /* device may have torn down */ }
        }
        catch (Exception ex)
        {
            WPFLog.Log($"EndpointSoundPlayback.Play: {ex.Message}");
        }
        finally
        {
            if (formatPtr != IntPtr.Zero) Marshal.FreeHGlobal(formatPtr);
            if (render != null) try { Marshal.FinalReleaseComObject(render); } catch { }
            if (client != null) try { Marshal.FinalReleaseComObject(client); } catch { }
            if (device != null) try { Marshal.FinalReleaseComObject(device); } catch { }
            if (enumerator != null) try { Marshal.FinalReleaseComObject(enumerator); } catch { }
        }
    }

    // Writes up to (freeFrames, dataLeft / blockAlign) frames into the render ring. Returns the
    // number of source bytes consumed - 0 when the engine refused the buffer or no source remains.
    private static int FillBuffer(IAudioRenderClient render, uint freeFrames, byte[] source,
                                  int sourceOffset, int sourceBytesLeft, int blockAlign)
    {
        if (freeFrames == 0 || sourceBytesLeft < blockAlign) return 0;

        int dataFrames = sourceBytesLeft / blockAlign;
        int framesToWrite = (int)Math.Min(freeFrames, (uint)dataFrames);
        if (framesToWrite <= 0) return 0;

        int hr = render.GetBuffer((uint)framesToWrite, out IntPtr buffer);
        if (hr < 0 || buffer == IntPtr.Zero) return 0;

        int bytesToWrite = framesToWrite * blockAlign;
        Marshal.Copy(source, sourceOffset, buffer, bytesToWrite);
        render.ReleaseBuffer((uint)framesToWrite, 0);
        return bytesToWrite;
    }

    // Walks RIFF / WAVE chunks for the fmt and data offsets. Mirrors the parser in VolumeFlyout
    // but also returns the channel count and sample rate that the WASAPI Initialize call needs.
    private static bool TryParseWav(byte[] data, out int channels, out int samplesPerSec,
                                    out int bitsPerSample, out int blockAlign,
                                    out int dataOffset, out int dataLength)
    {
        channels = 0; samplesPerSec = 0; bitsPerSample = 0; blockAlign = 0;
        dataOffset = 0; dataLength = 0;

        if (data.Length < 12) return false;
        if (data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F') return false;
        if (data[8] != 'W' || data[9] != 'A' || data[10] != 'V' || data[11] != 'E') return false;

        bool fmtSeen = false;
        int pos = 12;
        while (pos + 8 <= data.Length)
        {
            int chunkSize = BitConverter.ToInt32(data, pos + 4);
            int chunkData = pos + 8;
            if (chunkSize < 0 || chunkData + chunkSize > data.Length) return false;

            if (data[pos] == 'f' && data[pos + 1] == 'm' && data[pos + 2] == 't' && data[pos + 3] == ' ')
            {
                if (chunkSize < 16) return false;
                channels = BitConverter.ToUInt16(data, chunkData + 2);
                samplesPerSec = (int)BitConverter.ToUInt32(data, chunkData + 4);
                blockAlign = BitConverter.ToUInt16(data, chunkData + 12);
                bitsPerSample = BitConverter.ToUInt16(data, chunkData + 14);
                fmtSeen = channels > 0 && samplesPerSec > 0 && blockAlign > 0 && bitsPerSample > 0;
            }
            else if (data[pos] == 'd' && data[pos + 1] == 'a' && data[pos + 2] == 't' && data[pos + 3] == 'a')
            {
                if (!fmtSeen) return false;
                dataOffset = chunkData;
                dataLength = chunkSize;
                return true;
            }

            pos = chunkData + chunkSize;
            if ((chunkSize & 1) != 0) pos++;
        }
        return false;
    }
}
