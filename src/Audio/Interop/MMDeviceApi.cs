using System.Runtime.InteropServices;

namespace VolumeTrayAppWPF.Audio.Interop;

// IMMDeviceEnumerator / IMMDevice / IMMDeviceCollection / IMMEndpoint / IMMNotificationClient.
// Verified against Windows SDK mmdeviceapi.h:
//   IMMDeviceEnumerator         A95664D2-9614-4F35-A746-DE8DB63617E6
//   IMMDevice                   D666063F-1587-4E43-81F1-B948E807363F
//   IMMDeviceCollection         0BD7A1BE-7A1A-44DB-8397-CC5392387B5E
//   IMMEndpoint                 1BE09788-6894-4089-8586-9A2A6C265AC5
//   IMMNotificationClient       7991EEC9-7E89-4D85-8390-6C703CEC60C0
//   CLSID_MMDeviceEnumerator    BCDE0395-E52F-467C-8E3D-C4579291692E

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    // The activated interface arrives as IUnknown* via void**; the runtime hands us a managed proxy
    // and the caller QIs by casting to the desired RCW type.
    [PreserveSig]
    int Activate(
        [In, MarshalAs(UnmanagedType.LPStruct)] Guid iid,
        ClsCtx dwClsCtx,
        IntPtr pActivationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);

    void OpenPropertyStore(uint stgmAccess, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore ppProperties);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);

    [PreserveSig]
    int GetState(out uint pdwState);
}

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    void GetCount(out uint pcDevices);
    void Item(uint nDevice, [MarshalAs(UnmanagedType.Interface)] out IMMDevice ppDevice);
}

[ComImport]
[Guid("1BE09788-6894-4089-8586-9A2A6C265AC5")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMEndpoint
{
    void GetDataFlow(out EDataFlow pDataFlow);
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints(
        EDataFlow dataFlow,
        DeviceState dwStateMask,
        [MarshalAs(UnmanagedType.Interface)] out IMMDeviceCollection ppDevices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(
        EDataFlow dataFlow,
        ERole role,
        [MarshalAs(UnmanagedType.Interface)] out IMMDevice ppEndpoint);

    void GetDevice(
        [MarshalAs(UnmanagedType.LPWStr)] string pwstrId,
        [MarshalAs(UnmanagedType.Interface)] out IMMDevice ppDevice);

    void RegisterEndpointNotificationCallback(
        [MarshalAs(UnmanagedType.Interface)] IMMNotificationClient pClient);

    void UnregisterEndpointNotificationCallback(
        [MarshalAs(UnmanagedType.Interface)] IMMNotificationClient pClient);
}

// Implemented in managed code. NO [ComImport] - that attribute is for interfaces we consume
// from native COM. When present on an interface we implement, the runtime can fail to wire the
// CCW such that QueryInterface from native succeeds for registration but callbacks never deliver.
// PreserveSig so we return our own HRESULTs (0 = S_OK) rather than letting an exception bubble.
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
    [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, uint dwNewState);
    [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);
    [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);
    [PreserveSig] int OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string? pwstrDefaultDeviceId);
    [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, PROPERTYKEY key);
}

// CoCreate target for the device enumerator. Pattern: cast a `new MMDeviceEnumeratorComObject()`
// to `IMMDeviceEnumerator` and the runtime triggers CoCreateInstance with this CLSID.
[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject { }
