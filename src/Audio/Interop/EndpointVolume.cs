using System.Runtime.InteropServices;

namespace VolumeTrayAppWPF.Audio.Interop;

// Endpoint volume + meter interfaces. Verified against Windows SDK endpointvolume.h:
//   IAudioEndpointVolumeCallback   657804FA-D6AD-4496-8A60-352752AF4F89
//   IAudioEndpointVolume           5CDF2C82-841E-4546-9722-0CF74078229A
//   IAudioMeterInformation         C02216F6-8C67-4B5B-9D00-D008E73E0064

// Pushed into IAudioEndpointVolumeCallback.OnNotify when master volume / mute / per-channel volume change.
// LayoutKind.Sequential matches the C struct exactly:
//   GUID guidEventContext;  // 16
//   BOOL bMuted;            // 4
//   float fMasterVolume;    // 4
//   UINT nChannels;         // 4
//   float afChannelVolumes[1];  // 4 (variable-length tail)
[StructLayout(LayoutKind.Sequential)]
internal struct AUDIO_VOLUME_NOTIFICATION_DATA
{
    public Guid guidEventContext;
    [MarshalAs(UnmanagedType.Bool)] public bool bMuted;
    public float fMasterVolume;
    public uint nChannels;
    // afChannelVolumes[1] omitted; we ignore per-channel changes.
}

// Implemented in managed code. NO [ComImport] - that attribute is for interfaces we consume
// from native COM. When present on an interface we implement, the runtime can fail to wire the
// CCW such that QueryInterface from native succeeds for registration but callbacks never deliver.
// PreserveSig so we own the HRESULT return.
// NOTE: marshaling a pointer to AUDIO_VOLUME_NOTIFICATION_DATA as IntPtr - caller marshals manually
// because the trailing channel-volumes array is variable-length and we don't need it.
[Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolumeCallback
{
    [PreserveSig] int OnNotify(IntPtr pNotify);
}

[ComImport]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    void RegisterControlChangeNotify([MarshalAs(UnmanagedType.Interface)] IAudioEndpointVolumeCallback pNotify);
    void UnregisterControlChangeNotify([MarshalAs(UnmanagedType.Interface)] IAudioEndpointVolumeCallback pNotify);

    void GetChannelCount(out uint pnChannelCount);

    void SetMasterVolumeLevel(float fLevelDB, [In] ref Guid pguidEventContext);
    void SetMasterVolumeLevelScalar(float fLevel, [In] ref Guid pguidEventContext);
    void GetMasterVolumeLevel(out float pfLevelDB);
    void GetMasterVolumeLevelScalar(out float pfLevel);

    void SetChannelVolumeLevel(uint nChannel, float fLevelDB, [In] ref Guid pguidEventContext);
    void SetChannelVolumeLevelScalar(uint nChannel, float fLevel, [In] ref Guid pguidEventContext);
    void GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
    void GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);

    void SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, [In] ref Guid pguidEventContext);
    void GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);

    void GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);
    void VolumeStepUp([In] ref Guid pguidEventContext);
    void VolumeStepDown([In] ref Guid pguidEventContext);

    void QueryHardwareSupport(out uint pdwHardwareSupportMask);
    void GetVolumeRange(out float pflVolumeMindB, out float pflVolumeMaxdB, out float pflVolumeIncrementdB);
}

// Real-time peak meter. Available on the endpoint AND on each session.
[ComImport]
[Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioMeterInformation
{
    void GetPeakValue(out float pfPeak);
    void GetMeteringChannelCount(out uint pnChannelCount);
    // GetChannelsPeakValues / QueryHardwareSupport not used here; declared as stubs to keep the vtable layout
    // intact so a future caller can extend without re-aligning offsets.
    void GetChannelsPeakValues(uint u32ChannelCount, [Out] float[] afPeakValues);
    void QueryHardwareSupport(out uint pdwHardwareSupportMask);
}
