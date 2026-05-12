using System.Diagnostics;
using VolumeTrayAppWPF.Audio.Interop;

namespace VolumeTrayAppWPF.Audio;

/// <summary>
/// Helpers for opening Windows Sound surfaces. OpenDeviceProperties uses the per-endpoint
/// ms-settings deep-link "ms-settings:sound-properties?endpointId=&lt;IMMDevice id&gt;" which the
/// Win11 Settings app resolves to the specific device's properties page (volume, output format,
/// audio enhancements, spatial sound, allow-exclusive-control). The endpoint ID format the URI
/// expects ("{0.0.x.00000000}.{guid}") is exactly the string IMMDevice.GetId returns, so no
/// translation is needed. Legacy mmsys.cpl tab launchers stay for the global menu entries
/// (Playback / Recording / Sounds / Communications) where per-endpoint context doesn't apply.
/// </summary>
internal static class DeviceShellLinks
{
    private const string Rundll32 = "rundll32.exe";

    // Per-endpoint Win11 Settings URI. Documented under the Sound section of the ms-settings
    // reference. The endpointId query parameter takes the IMMDevice ID verbatim; we still
    // EscapeDataString it because the ID contains "{" and "}" which are reserved characters.
    private const string UriDevicePropertiesFormat = "ms-settings:sound-properties?endpointId={0}";

    private const string TabPlayback = "playback";
    private const string TabRecording = "recording";
    private const string TabSounds = "sounds";
    private const string TabCommunications = "communications";

    /// <summary>
    /// Opens the Windows 11 Settings per-device properties page for <paramref name="device"/>.
    /// Same surface for default and non-default endpoints alike - the endpointId deep-link
    /// resolves any active or disabled endpoint to its own properties page.
    /// </summary>
    public static void OpenDeviceProperties(AudioDevice device)
    {
        string uri = string.Format(UriDevicePropertiesFormat, Uri.EscapeDataString(device.Id));
        LaunchSettingsUri(uri);
    }

    public static void OpenPlaybackTab() => OpenSoundPanel(TabPlayback);
    public static void OpenRecordingTab() => OpenSoundPanel(TabRecording);
    public static void OpenSoundsTab() => OpenSoundPanel(TabSounds);
    public static void OpenCommunicationsTab() => OpenSoundPanel(TabCommunications);

    // Launches a ms-settings: URI through ShellExecute so the registered URI handler picks it up.
    // UseShellExecute = true is load-bearing: the alternative direct-exec path can't open URIs.
    private static void LaunchSettingsUri(string uri)
    {
        try
        {
            using Process? _ = Process.Start(new ProcessStartInfo
            {
                FileName = uri,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { WPFLog.Log($"DeviceShellLinks.LaunchSettingsUri({uri}): {ex.Message}"); }
    }

    private static void OpenSoundPanel(string tab)
    {
        try
        {
            using Process? _ = Process.Start(new ProcessStartInfo
            {
                FileName = Rundll32,
                Arguments = $"shell32.dll,Control_RunDLL mmsys.cpl,,{tab}",
                UseShellExecute = false,
            });
        }
        catch (Exception ex) { WPFLog.Log($"DeviceShellLinks.OpenSoundPanel({tab}): {ex.Message}"); }
    }
}
