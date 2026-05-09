using System.Runtime.InteropServices;
using VolumeTrayAppWPF.Audio.Interop;

namespace VolumeTrayAppWPF.Audio;

/// <summary>
/// Thin wrapper around <see cref="IAudioMeterInformation"/> that returns min(L, R) and max(L, R)
/// over the first two channels. Falls back to GetPeakValue for mono streams (one channel = both
/// values equal); returns zero on failure so callers can leave previous lerp targets in place
/// without special-casing exceptions.
/// In unified mode the two channels collapse into a single weighted value (returned as both
/// min and max) so the base bar and stereo overlay coincide and the meter renders as one solid bar.
/// </summary>
internal static class MeterReader
{
    private const int SOk = 0;

    /// <summary>
    /// Reads the per-channel peak values and returns min/max over the first two channels.
    /// Allocates an unmanaged buffer sized to the metering channel count, calls
    /// IAudioMeterInformation.GetChannelsPeakValues with a raw pointer (the only safe way - the
    /// CLR's default array marshaler doesn't honor size_is and would corrupt memory for
    /// chanCount > 1), then Marshal.Copies the first two slots into managed floats.
    /// When <paramref name="unified"/> is true, collapses the per-channel result through
    /// <see cref="ApplyUnifiedWeighting"/> so both outputs carry the same weighted value.
    /// </summary>
    internal static void ReadStereoPeaks(
        IAudioMeterInformation meter, bool unified, int biasMultiplier, out float min, out float max)
    {
        min = 0f;
        max = 0f;

        IntPtr buffer = IntPtr.Zero;
        try
        {
            meter.GetMeteringChannelCount(out uint chanCount);
            if (chanCount == 0) return;

            if (chanCount == 1)
            {
                // Mono: GetPeakValue avoids the unmanaged alloc, and the matching min/max keeps
                // the stereo overlay coincident with the base bar. Unified mode is a no-op here
                // since both values are already equal.
                meter.GetPeakValue(out float p);
                min = p;
                max = p;
                return;
            }

            buffer = Marshal.AllocHGlobal((int)chanCount * sizeof(float));
            int hr = meter.GetChannelsPeakValues(chanCount, buffer);
            if (hr != SOk) return;

            // Read just the first two channels - the rest are surround / LFE which the meter
            // doesn't visualize.
            float[] firstTwo = new float[2];
            Marshal.Copy(buffer, firstTwo, 0, 2);
            float a = firstTwo[0];
            float b = firstTwo[1];
            float lo = a < b ? a : b;
            float hi = a > b ? a : b;

            if (unified)
            {
                float combined = ApplyUnifiedWeighting(lo, hi, biasMultiplier);
                min = combined;
                max = combined;
            }
            else
            {
                min = lo;
                max = hi;
            }
        }
        catch
        {
            // Endpoint or session can fail mid-disconnect; leave 0/0 - the calling lerp will
            // continue from its previous targets until the next successful read.
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Combines the quieter and louder channel into one weighted value:
    /// <c>(low * M + high) / (M + 1)</c>. M=0 falls back to max(L, R); M=1 averages the channels;
    /// larger M biases the result toward min(L, R), dampening moment-to-moment stereo flutter
    /// without fully collapsing to the quieter channel. Multiplier is clamped to non-negative.
    /// </summary>
    private static float ApplyUnifiedWeighting(float low, float high, int biasMultiplier)
    {
        int m = biasMultiplier < 0 ? 0 : biasMultiplier;
        return (low * m + high) / (m + 1);
    }
}
