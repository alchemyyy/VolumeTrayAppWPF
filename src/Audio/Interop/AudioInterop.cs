using System.Runtime.InteropServices;

namespace VolumeTrayAppWPF.Audio.Interop;

// Windows Core Audio common types, enums, property keys, and IPropertyStore.
// Signatures verified against the Windows SDK headers
// (mmdeviceapi.h, propsys.h, propkey.h, functiondiscoverykeys_devpkey.h).

internal enum EDataFlow
{
    eRender = 0,
    eCapture = 1,
    eAll = 2,
}

internal enum ERole
{
    eConsole = 0,
    eMultimedia = 1,
    eCommunications = 2,
}

internal enum AudioSessionState
{
    Inactive = 0,
    Active = 1,
    Expired = 2,
}

internal enum AudioSessionDisconnectReason
{
    DeviceRemoval = 0,
    ServerShutdown = 1,
    FormatChanged = 2,
    SessionLogoff = 3,
    SessionDisconnected = 4,
    ExclusiveModeOverride = 5,
}

[Flags]
internal enum DeviceState : uint
{
    Active = 0x00000001,
    Disabled = 0x00000002,
    NotPresent = 0x00000004,
    Unplugged = 0x00000008,
    All = 0x0000000F,
}

[Flags]
internal enum ClsCtx : uint
{
    INPROC_SERVER = 0x1,
    INPROC_HANDLER = 0x2,
    LOCAL_SERVER = 0x4,
    REMOTE_SERVER = 0x10,
    ALL = INPROC_SERVER | INPROC_HANDLER | LOCAL_SERVER | REMOTE_SERVER,
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROPERTYKEY
{
    public Guid fmtid;
    public uint pid;

    public PROPERTYKEY(Guid fmtid, uint pid) { this.fmtid = fmtid; this.pid = pid; }
}

// PROPVARIANT minimal layout. On x64 the natural size is 24 bytes (8-byte header + 16-byte union),
// and on x86 it's 16 bytes (8 + 8). Two IntPtr fields after the header match that on both archs.
// We only ever read VT_LPWSTR (pwszVal), VT_UI4 (ulVal), and VT_BOOL/VT_BLOB cases -
// pwszVal is reachable via the first pointer field; the second is padding to keep the layout
// safely aligned with the native size so PropVariantClear doesn't read past the struct.
[StructLayout(LayoutKind.Sequential)]
internal struct PROPVARIANT
{
    public ushort vt;
    public ushort wReserved1;
    public ushort wReserved2;
    public ushort wReserved3;
    public IntPtr p1;
    public IntPtr p2;

    public const ushort VT_EMPTY = 0;
    public const ushort VT_I4 = 3;
    public const ushort VT_BOOL = 11;
    public const ushort VT_UI4 = 19;
    public const ushort VT_LPWSTR = 31;
    public const ushort VT_BLOB = 65;

    public string? GetString() => vt == VT_LPWSTR ? Marshal.PtrToStringUni(p1) : null;

    public uint GetUInt32() => vt == VT_UI4 ? (uint)p1.ToInt64() : 0u;
}

// Well-known Function-Discovery property keys. Verified against
// functiondiscoverykeys_devpkey.h and mmdeviceapi.h.
internal static class PropertyKeys
{
    // Friendly endpoint name, e.g. "Speakers (Realtek(R) Audio)"
    public static readonly PROPERTYKEY PKEY_Device_FriendlyName = new(
        new Guid(0xA45C254E, 0xDF1C, 0x4EFD, 0x80, 0x20, 0x67, 0xD1, 0x46, 0xA8, 0x50, 0xE0), 14);

    // Adapter / interface name, e.g. "Realtek(R) Audio"
    public static readonly PROPERTYKEY PKEY_DeviceInterface_FriendlyName = new(
        new Guid(0x026E516E, 0xB814, 0x414B, 0x83, 0xCD, 0x85, 0x6D, 0x6F, 0xEF, 0x48, 0x22), 2);

    // Endpoint description, e.g. "Speakers" or "Headphones"
    public static readonly PROPERTYKEY PKEY_Device_DeviceDesc = new(
        new Guid(0xA45C254E, 0xDF1C, 0x4EFD, 0x80, 0x20, 0x67, 0xD1, 0x46, 0xA8, 0x50, 0xE0), 2);

    // Endpoint stable GUID (for matching across reboots)
    public static readonly PROPERTYKEY PKEY_AudioEndpoint_GUID = new(
        new Guid(0x1DA5D803, 0xD492, 0x4EDD, 0x8C, 0x23, 0xE0, 0xC0, 0xFF, 0xEE, 0x7F, 0x0E), 4);
}

// IPropertyStore: read-only side used to pull endpoint properties out of an IMMDevice.
[ComImport]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    void GetCount(out uint cProps);
    void GetAt(uint iProp, out PROPERTYKEY pkey);
    void GetValue([In] ref PROPERTYKEY key, out PROPVARIANT pv);
    void SetValue([In] ref PROPERTYKEY key, [In] ref PROPVARIANT propvar);
    void Commit();
}

// Frees PROPVARIANT-allocated resources (e.g. the LPWSTR returned by GetValue).
internal static class Ole32
{
    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PROPVARIANT pvar);
}
