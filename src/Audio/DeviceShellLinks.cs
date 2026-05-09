using System.Diagnostics;
using VolumeTrayAppWPF.Audio.Interop;

namespace VolumeTrayAppWPF.Audio;

/// <summary>
/// Helpers for launching the classic Windows Sound control-panel surfaces. mmsys.cpl supports
/// hidden tab arguments (playback / recording / sounds / communications) the legacy Volume mixer
/// has used for years - we route every "open device properties" gesture through here so the
/// invocation is in one place and the user never sees raw rundll32 args bleed into call sites.
/// </summary>
internal static class DeviceShellLinks
{
    private const string Rundll32 = "rundll32.exe";

    private const string TabPlayback = "playback";
    private const string TabRecording = "recording";
    private const string TabSounds = "sounds";
    private const string TabCommunications = "communications";

    /// <summary>
    /// Opens the appropriate Sound control-panel tab for the given device. Render endpoints land
    /// on the Playback tab; capture endpoints on the Recording tab. Selecting the specific device
    /// inside that tab isn't a documented mmsys.cpl capability, so users land on the list with
    /// their device visible rather than a per-device properties dialog.
    /// </summary>
    public static void OpenDeviceProperties(AudioDevice device)
    {
        string tab = device.DataFlow == EDataFlow.eCapture ? TabRecording : TabPlayback;
        OpenSoundPanel(tab);
    }

    public static void OpenPlaybackTab() => OpenSoundPanel(TabPlayback);
    public static void OpenRecordingTab() => OpenSoundPanel(TabRecording);
    public static void OpenSoundsTab() => OpenSoundPanel(TabSounds);
    public static void OpenCommunicationsTab() => OpenSoundPanel(TabCommunications);

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
