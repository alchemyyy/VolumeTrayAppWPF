using System.Runtime.InteropServices;

namespace VolumeTrayAppWPF.Audio.Interop;

// IPolicyConfig is an undocumented Windows interface used by mmsys.cpl to set the system default
// audio endpoint. Only SetDefaultEndpoint is invoked here; all earlier vtable slots are declared as
// no-arg placeholders so the slot index of SetDefaultEndpoint matches the native vtable.
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
    void Unused3();
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
