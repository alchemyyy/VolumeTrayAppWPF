namespace VolumeTrayAppWPF.Interop;

// Win32 error and HRESULT constants reused across the interop surface. S_OK and
// ERROR_INSUFFICIENT_BUFFER were each declared in two places (SetupAPI / IconExtraction and
// AudioHResults / IconExtraction); collapsing both keeps every consumer pinned to one value.
// AudioHResults.S_OK in Audio/Interop/AudioInterop.cs is kept as a separate alias for readability
// inside the audio COM stack; it forwards to this constant.
internal static class NativeErrors
{
    public const int S_OK = 0;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
}
