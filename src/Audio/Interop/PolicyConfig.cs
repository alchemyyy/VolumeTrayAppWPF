using System.Runtime.InteropServices;

namespace VolumeTrayAppWPF.Audio.Interop;

// IPolicyConfig is an undocumented Windows interface used by mmsys.cpl to set the system default
// audio endpoint and rewrite per-device engine settings. Slot indices are load-bearing - each
// unused-prior method is declared as a stub so the methods we DO call land at the right vtable
// offsets.
//
// Vtable map (Win10 RS1+):
//   0  GetMixFormat               (stub)
//   1  GetDeviceFormat            (stub)
//   2  ResetDeviceFormat          (stub)
//   3  SetDeviceFormat            <-- called for the format-picker context menu
//   4  GetProcessingPeriod        (stub)
//   5  SetProcessingPeriod        (stub)
//   6  GetShareMode               (stub)
//   7  SetShareMode               (stub)
//   8  GetPropertyValue           (stub)
//   9  SetPropertyValue           (stub)
//   10 SetDefaultEndpoint         <-- called for the device-icon set-as-default click
//   11 SetEndpointVisibility      <-- called for the device enable / disable click
//
// IIDs:
//   IPolicyConfig (Win7 / Win10 RS1+)   F8679F50-850A-41CF-9C72-430F290290C8
//   CLSID_PolicyConfigClient            870AF99C-171D-4F9E-AF0D-E63DF40C2BC9

[ComImport]
[Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    void Unused0();
    void Unused1();
    void Unused2();

    // Writes the per-endpoint default mix format. mmsys.cpl Advanced > Default Format and the
    // flyout's format-label context menu both end up here. pEndpointFormat is the format the
    // endpoint device renders / captures at; pMixFormat is what the audio engine mixes to
    // before resampling - in practice we pass the same WAVEFORMATEXTENSIBLE blob for both, which
    // matches what mmsys.cpl does. PreserveSig so the caller can log a non-zero HRESULT instead
    // of throwing through the threadpool action.
    [PreserveSig]
    int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, IntPtr pEndpointFormat, IntPtr pMixFormat);

    void Unused4();
    void Unused5();
    void Unused6();
    void Unused7();
    void Unused8();
    void Unused9();

    void SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, ERole eRole);
    void SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, [MarshalAs(UnmanagedType.I2)] short isVisible);
}

// CoCreate target. Cast `new PolicyConfigClientComObject()` to IPolicyConfig and the runtime
// triggers CoCreateInstance with this CLSID.
[ComImport]
[Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
internal class PolicyConfigClientComObject { }
