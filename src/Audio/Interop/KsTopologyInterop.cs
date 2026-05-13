using System.Runtime.InteropServices;

namespace VolumeTrayAppWPF.Audio.Interop;

// Device-topology and Kernel-Streaming interop for querying the audio device's advertised
// pin data ranges (KSPROPSETID_Pin / KSPROPERTY_PIN_DATARANGES). This is the canonical source
// mmsys.cpl's Advanced > Default Format dropdown reads from. IAudioClient.IsFormatSupported
// probing doesn't match because drivers can report different sets through the two paths -
// the WASAPI engine view vs the KS audio pin's advertised data ranges.
//
// Signatures from devicetopology.h, ks.h, and ksmedia.h. All COM-pointer outputs are typed as
// out IntPtr (not "out IConnector" / "[MarshalAs(IUnknown)] out object") because the .NET
// RCW marshaller misbehaves across topology boundaries - the device-side connector reached
// via IConnector::GetConnectedTo lives in a different IDeviceTopology object and the RCW for
// the endpoint side can fail to QI for IPart cleanly. NAudio uses this same pattern.

internal enum ConnectorType
{
    Unknown_Connector = 0,
    Physical_Internal = 1,
    Physical_External = 2,
    Software_IO = 3,
    Software_Fixed = 4,
    Network = 5,
}

internal enum PartType
{
    Connector = 0,
    Subunit = 1,
}

[ComImport]
[Guid("2A07407E-6497-4A18-9787-32F79BD0D98F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeviceTopology
{
    [PreserveSig] int GetConnectorCount(out uint count);
    [PreserveSig] int GetConnector(uint index, out IntPtr connector);
    [PreserveSig] int GetSubunitCount(out uint count);
    [PreserveSig] int GetSubunit(uint index, out IntPtr subunit);

    void Unused_GetPartById();

    // Returns the audio adapter's device interface path (e.g. \\?\hdaudio#func_01...) when
    // invoked on the adapter-side topology obtained via IPart::GetTopologyObject. That path
    // is what we hand to CreateFile to open the KS filter directly - IPart::Activate(IKsControl)
    // returns E_NOINTERFACE on every part of every audio driver we've tested, so the KS query
    // has to go through DeviceIoControl on the filter handle instead.
    [PreserveSig] int GetDeviceId([MarshalAs(UnmanagedType.LPWStr)] out string deviceId);

    void Unused_GetSignalPath();
}

[ComImport]
[Guid("9c2c4058-23f5-41de-877a-df3af236a09e")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IConnector
{
    [PreserveSig] int GetType(out ConnectorType type);
    [PreserveSig] int GetDataFlow(out EDataFlow flow);

    void Unused_ConnectTo();
    void Unused_Disconnect();
    void Unused_IsConnected();

    [PreserveSig] int GetConnectedTo(out IntPtr connectedToConnector);

    void Unused_GetConnectorIdConnectedTo();
    void Unused_GetDeviceIdConnectedTo();
}

// Per the mmsys.cpl decompile, the Default Format dropdown is populated by calling
// IPart::Activate(CLSCTX_INPROC_SERVER, IID_IKsFormatSupport) on a part inside the audio
// adapter's topology (NOT the endpoint topology) and then probing each candidate format with
// IKsFormatSupport::IsFormatSupported. Which exact part exposes IKsFormatSupport varies by
// driver, so we walk the adapter topology and try every Connector and Subunit until one
// returns a non-null pointer.
[ComImport]
[Guid("AE2DE0E4-5BCA-4F2D-AA46-5D13F8FDB3A9")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPart
{
    void Unused_GetName();
    void Unused_GetLocalId();
    void Unused_GetGlobalId();
    void Unused_GetPartType();
    void Unused_GetSubType();
    void Unused_GetControlInterfaceCount();
    void Unused_GetControlInterface();
    void Unused_EnumPartsIncoming();
    void Unused_EnumPartsOutgoing();

    [PreserveSig] int GetTopologyObject(out IntPtr topologyObject);

    [PreserveSig]
    int Activate(ClsCtx dwClsContext, ref Guid refiid, out IntPtr interfacePointer);

    void Unused_RegisterControlChangeCallback();
    void Unused_UnregisterControlChangeCallback();
}

// IKsFormatSupport: tests whether an audio device's KS pin natively supports a candidate
// WAVEFORMATEX. The format is wrapped in a 104-byte KSDATAFORMAT_WAVEFORMATEX envelope
// (KSDATAFORMAT header + WAVEFORMATEX payload). This is the canonical interface mmsys.cpl
// uses to populate its Default Format dropdown - confirmed by decompile.
[ComImport]
[Guid("3CB4A69D-BB6F-4D2B-95B7-452D2C155DB5")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IKsFormatSupport
{
    // IsFormatSupported(PKSDATAFORMAT pKsFormat, ULONG cbFormat, BOOL *pbSupported)
    // - pKsFormat: pointer to KSDATAFORMAT_WAVEFORMATEX (104 bytes for a 40-byte WAVEFORMATEXTENSIBLE)
    // - cbFormat: size of the format buffer in bytes (104)
    // - pbSupported: out, BOOL (32-bit int) - TRUE/FALSE for whether the format is supported
    [PreserveSig]
    int IsFormatSupported(IntPtr pKsFormat, uint cbFormat, [MarshalAs(UnmanagedType.Bool)] out bool supported);

    void Unused_GetDevicePreferredFormat();
}

// KSPROPERTY: the generic 24-byte set/id/flags triple every KS property request starts with.
// Total size = 16 (Set GUID) + 4 (Id) + 4 (Flags) = 24.
[StructLayout(LayoutKind.Sequential)]
internal struct KSPROPERTY
{
    public Guid Set;
    public uint Id;
    public uint Flags;
}

// KSP_PIN: KSPROPERTY plus a pin id selector. 32 bytes total. Used for pin-scoped properties
// like KSPROPERTY_PIN_DATARANGES.
[StructLayout(LayoutKind.Sequential)]
internal struct KSP_PIN
{
    public KSPROPERTY Property;
    public uint PinId;
    public uint Reserved;
}

// KSMULTIPLE_ITEM: 8-byte header on a KS array response. Size is the total buffer size
// including this header; Count is the item count; items follow immediately.
[StructLayout(LayoutKind.Sequential)]
internal struct KSMULTIPLE_ITEM
{
    public uint Size;
    public uint Count;
}

// KSDATARANGE_AUDIO: 64-byte KSDATARANGE header followed by 20 bytes of audio-specific
// fields, 84 bytes total. The header's FormatSize tells the actual length of each item -
// audio extensions are fixed-size but extensions for other format families may vary, so the
// parser walks by FormatSize, not sizeof(KSDATARANGE_AUDIO).
[StructLayout(LayoutKind.Sequential)]
internal struct KSDATARANGE_AUDIO
{
    // KSDATARANGE header (64 bytes)
    public uint FormatSize;
    public uint Flags;
    public uint SampleSize;
    public uint Reserved;
    public Guid MajorFormat;
    public Guid SubFormat;
    public Guid Specifier;

    // Audio-specific tail (20 bytes)
    public uint MaximumChannels;
    public uint MinimumBitsPerSample;
    public uint MaximumBitsPerSample;
    public uint MinimumSampleFrequency;
    public uint MaximumSampleFrequency;
}

internal static class KsConstants
{
    // Microsoft-private IID used by mmsys.cpl's Default Format dropdown population. Confirmed
    // from a Hex-Rays decompile of mmsys.cpl: the dropdown is populated via this chain (the
    // entry point for the audio engine's internal topology, where IKsFormatSupport lives -
    // the public IDeviceTopology never exposes it).
    //
    //   ifilter = IMMDevice::Activate(IID_AudioEnginePartFilter, CLSCTX_INPROC_SERVER, NULL)
    //   enum    = ifilter->vtable[3](&ksDataFormat=64B, 64, NULL)
    //   count   = enum->vtable[3]()
    //   part    = enum->vtable[4](i)
    //   fs      = part->Activate(CLSCTX_INPROC_SERVER, IID_IKsFormatSupport)
    //   supported = fs->IsFormatSupported(KSDATAFORMAT_WAVEFORMATEX, 104)  // per candidate
    //
    // The KSDATAFORMAT passed to vtable[3] tells the filter what data range to enumerate parts
    // for - we pass {TYPE_AUDIO, SUBTYPE_PCM, SPECIFIER_WAVEFORMATEX} to get audio PCM parts.
    public static readonly Guid IID_AudioEnginePartFilter = new(
        0x2B0711DE, 0xDAB7, 0x4610, 0xA1, 0x6F, 0xD3, 0x38, 0x37, 0x49, 0xB2, 0x20);

    // KSPROPSETID_Pin: legacy direct-pin property set. Kept for reference; not used now that
    // the IKsFormatSupport probe path is wired up.
    public static readonly Guid KSPROPSETID_Pin = new(
        0x8C134960, 0x51AD, 0x11CF, 0x87, 0x8A, 0x94, 0xF8, 0x01, 0xC1, 0x00, 0x00);

    // KSPROPERTY_PIN enumeration value 3 (CINSTANCES=0, CTYPES=1, DATAFLOW=2, DATARANGES=3,
    // DATAINTERSECTION=4, INTERFACES=5, MEDIUMS=6, ...).
    public const uint KSPROPERTY_PIN_DATARANGES = 3;
    public const uint KSPROPERTY_PIN_CTYPES = 1;
    public const uint KSPROPERTY_TYPE_GET = 0x00000001;

    // IOCTL_KS_PROPERTY = CTL_CODE(FILE_DEVICE_KS, 0x000, METHOD_NEITHER, FILE_ANY_ACCESS)
    //                  = (0x002F << 16) | (0 << 14) | (0 << 2) | 3 = 0x002F0003
    // Sent via DeviceIoControl on a KS filter handle (returned by CreateFile on the audio
    // adapter's device interface path). The driver reads the KSP_PIN input describing which
    // property to query and writes the response (KSMULTIPLE_ITEM + items) to the output buffer.
    public const uint IOCTL_KS_PROPERTY = 0x002F0003;

    // KSDATAFORMAT_TYPE_AUDIO: MajorFormat for audio formats.
    public static readonly Guid KSDATAFORMAT_TYPE_AUDIO = new(
        0x73647561, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);

    // KSDATAFORMAT_SPECIFIER_WAVEFORMATEX: Specifier value for a KSDATAFORMAT whose payload is
    // a WAVEFORMATEX (variable size) immediately following the 64-byte KSDATAFORMAT header.
    public static readonly Guid KSDATAFORMAT_SPECIFIER_WAVEFORMATEX = new(
        0x05589F81, 0xC356, 0x11CE, 0xBF, 0x01, 0x00, 0xAA, 0x00, 0x55, 0x59, 0x5A);

    // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT: float-PCM ranges. We probe both PCM and float subtypes
    // for 32-bit candidates (float is the engine's internal form); 16-bit and 24-bit use PCM.
    public static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new(
        0x00000003, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
}
