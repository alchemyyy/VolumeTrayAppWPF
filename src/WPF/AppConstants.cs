namespace VolumeTrayAppWPF.WPF;

/// <summary>
/// Cross-cutting WPF-layer constants that don't belong in <see cref="TimeConstants"/>
/// (those are time-only) and aren't per-feature enough to live on a specific window.
/// </summary>
public static class AppConstants
{
    /// <summary>
    /// Mouse-wheel volume step in percent per notch.
    /// Used by the tray-icon wheel handler (App) and the in-flyout cell wheel handlers (VolumeFlyout)
    /// so the two surfaces step identically.
    /// </summary>
    public const double WheelVolumeStepPercent = 2.0;

    /// <summary>
    /// XAML resource key for the Segoe Fluent Icons FontFamily declared in App.xaml.
    /// XAML consumers should use <c>FontFamily="{StaticResource IconFontFamily}"</c>
    /// instead of hardcoding the family string.
    /// </summary>
    public const string IconFontFamilyResourceKey = "IconFontFamily";
}
