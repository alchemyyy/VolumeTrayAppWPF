using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Shell;
using VolumeTrayAppWPF.Interop;

namespace VolumeTrayAppWPF.WPF.Utils;

/// <summary>
/// Drives the outer chrome corner radius for a custom-chrome WPF Window. Combines the three knobs
/// that any window with rounded corners has to keep in lockstep:
///   - <see cref="WindowChrome.CornerRadius"/> on the window's WindowChrome (if attached)
///   - the outer-most Border's <see cref="Border.CornerRadius"/> (the painted shell)
///   - the DWM window-corner-preference attribute (Win11; overrides WindowChrome at the OS level)
///
/// Kept imperative (rather than DynamicResource-driven) because WindowChrome is a bare
/// <see cref="DependencyObject"/> and resource resolution against it doesn't reliably propagate;
/// the DWM call requires the HWND, which only exists after <c>SourceInitialized</c>.
/// </summary>
internal static class ChromeCornerRadiusHelper
{
    /// <summary>
    /// Apply <paramref name="radius"/> (in DIPs) to the window's WindowChrome corner radius, the
    /// outer Border's corner radius, and the DWM corner-preference attribute. Pass 0 to flatten.
    /// Safe before the HWND is realized: the DWM call short-circuits when the handle is zero,
    /// so a constructor-time call followed by a re-apply in <c>OnSourceInitialized</c> covers both
    /// the layout pre-pass and the OS-level corner shape.
    /// </summary>
    public static void Apply(Window window, Border outerBorder, double radius)
    {
        CornerRadius cornerRadius = new(radius);

        WindowChrome? chrome = WindowChrome.GetWindowChrome(window);
        if (chrome != null) chrome.CornerRadius = cornerRadius;

        outerBorder.CornerRadius = cornerRadius;

        ApplyDWMRoundedCorners(window, radius > 0);
    }

    private static void ApplyDWMRoundedCorners(Window window, bool rounded)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            int value = rounded ? DWMAPI.DWMWCP_ROUND : DWMAPI.DWMWCP_DONOTROUND;
            DWMAPI.DwmSetWindowAttribute(hwnd, DWMAPI.DWMWA_WINDOW_CORNER_PREFERENCE, ref value, sizeof(int));
        }
        catch
        {
            // DWM call may fail on older Windows; non-fatal.
        }
    }
}
