using System.Runtime.InteropServices;

namespace VolumeTrayAppWPF.Audio.Interop;

// Audio session interfaces (per-app sessions on a device endpoint).
// Verified against Windows SDK audiopolicy.h and audioclient.h:
//   IAudioSessionEvents         24918ACC-64B3-37C1-8CA9-74A66E9957A8
//   IAudioSessionControl        F4B1A599-7266-4319-A8CA-E70ACB11E8CD
//   IAudioSessionControl2       BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D
//   IAudioSessionEnumerator     E2F5BB11-0570-40CA-ACDD-3AA01277DEE8
//   IAudioSessionNotification   641DD20B-4D41-49CC-ABA3-174B9477BB08
//   IAudioSessionManager2       77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F
//   ISimpleAudioVolume          87CE5498-68D6-44E5-9215-6DA47EF883D8

// Implemented in managed code. NO [ComImport] - that attribute is for interfaces we consume
// from native COM. When present on an interface we implement, the runtime can fail to wire the
// CCW such that QueryInterface from native succeeds for registration but callbacks never deliver.
// PreserveSig so we own the HRESULT return.
[Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEvents
{
    [PreserveSig] int OnDisplayNameChanged([MarshalAs(UnmanagedType.LPWStr)] string NewDisplayName, [In] ref Guid EventContext);
    [PreserveSig] int OnIconPathChanged([MarshalAs(UnmanagedType.LPWStr)] string NewIconPath, [In] ref Guid EventContext);
    [PreserveSig] int OnSimpleVolumeChanged(float NewVolume, [MarshalAs(UnmanagedType.Bool)] bool NewMute, [In] ref Guid EventContext);
    [PreserveSig] int OnChannelVolumeChanged(uint ChannelCount, IntPtr NewChannelVolumeArray, uint ChangedChannel, [In] ref Guid EventContext);
    [PreserveSig] int OnGroupingParamChanged([In] ref Guid NewGroupingParam, [In] ref Guid EventContext);
    [PreserveSig] int OnStateChanged(AudioSessionState NewState);
    [PreserveSig] int OnSessionDisconnected(AudioSessionDisconnectReason DisconnectReason);
}

[ComImport]
[Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl
{
    void GetState(out AudioSessionState pRetVal);
    void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
    void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string Value, [In] ref Guid EventContext);
    void GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
    void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string Value, [In] ref Guid EventContext);
    void GetGroupingParam(out Guid pRetVal);
    void SetGroupingParam([In] ref Guid Override, [In] ref Guid EventContext);
    void RegisterAudioSessionNotification([MarshalAs(UnmanagedType.Interface)] IAudioSessionEvents NewNotifications);
    void UnregisterAudioSessionNotification([MarshalAs(UnmanagedType.Interface)] IAudioSessionEvents NewNotifications);
}

// IAudioSessionControl2 inherits IAudioSessionControl; declare the parent's vtable in order, then add 2's methods.
// Cast: query a fresh IAudioSessionControl2 from a session by calling Marshal.QueryInterface or direct cast.
[ComImport]
[Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    // Inherited from IAudioSessionControl (kept in vtable order)
    void GetState(out AudioSessionState pRetVal);
    void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
    void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string Value, [In] ref Guid EventContext);
    void GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
    void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string Value, [In] ref Guid EventContext);
    void GetGroupingParam(out Guid pRetVal);
    void SetGroupingParam([In] ref Guid Override, [In] ref Guid EventContext);
    void RegisterAudioSessionNotification([MarshalAs(UnmanagedType.Interface)] IAudioSessionEvents NewNotifications);
    void UnregisterAudioSessionNotification([MarshalAs(UnmanagedType.Interface)] IAudioSessionEvents NewNotifications);

    // Added by IAudioSessionControl2
    void GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
    void GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
    void GetProcessId(out uint pRetVal);
    [PreserveSig] int IsSystemSoundsSession();
    void SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}

[ComImport]
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    void GetCount(out int SessionCount);
    void GetSession(int SessionCount, [MarshalAs(UnmanagedType.Interface)] out IAudioSessionControl Session);
}

// Implemented in managed code. NO [ComImport] - see note on IAudioSessionEvents above.
[Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionNotification
{
    [PreserveSig] int OnSessionCreated([MarshalAs(UnmanagedType.Interface)] IAudioSessionControl NewSession);
}

// IAudioSessionManager2 inherits IAudioSessionManager (which has GetAudioSessionControl + GetSimpleAudioVolume).
// Declare both in vtable order so QI to the .NET RCW lays out correctly.
[ComImport]
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    // Inherited from IAudioSessionManager
    void GetAudioSessionControl(
        [In] ref Guid AudioSessionGuid,
        uint StreamFlags,
        [MarshalAs(UnmanagedType.Interface)] out IAudioSessionControl SessionControl);

    void GetSimpleAudioVolume(
        [In] ref Guid AudioSessionGuid,
        uint StreamFlags,
        [MarshalAs(UnmanagedType.Interface)] out ISimpleAudioVolume AudioVolume);

    // Added by IAudioSessionManager2
    void GetSessionEnumerator([MarshalAs(UnmanagedType.Interface)] out IAudioSessionEnumerator SessionEnum);
    void RegisterSessionNotification([MarshalAs(UnmanagedType.Interface)] IAudioSessionNotification SessionNotification);
    void UnregisterSessionNotification([MarshalAs(UnmanagedType.Interface)] IAudioSessionNotification SessionNotification);

    // Duck-notification slots present in the vtable; we don't use them but they have to be declared
    // so the methods above remain at the right offsets.
    void RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionID, IntPtr duckNotification);
    void UnregisterDuckNotification(IntPtr duckNotification);
}

[ComImport]
[Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    void SetMasterVolume(float fLevel, [In] ref Guid EventContext);
    void GetMasterVolume(out float pfLevel);
    void SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, [In] ref Guid EventContext);
    void GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
}
