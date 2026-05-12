using System.Windows;
using VolumeTrayAppWPF.Models;
using VolumeTrayAppWPF.Visuals;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using FontFamily = System.Windows.Media.FontFamily;
using SettingsThemeMode = VolumeTrayAppWPF.Models.ThemeMode;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace VolumeTrayAppWPF.WPF;

/// <summary>
/// Writes the live brush / corner-radius / slider-thumb resource set onto an Application's resource dictionary.
/// All Theme*/CloseButton*/ToggleSwitch* brush keys plus CornerRadius* and SliderThumb* keys flow through here,
/// so a theme change is a single Apply call from the shell.
/// Also owns <see cref="ResolveEffectiveIsLightTheme"/> - the single source of truth for
/// the user's ThemeMode override against the live AppTheme.
/// </summary>
internal static class ThemeResources
{
    /// <summary>
    /// Returns the theme to apply (light=true) after considering the user's ThemeMode override.
    /// Safe to call before settings / theme are fully wired:
    /// missing pieces fall back to <c>theme?.IsLightTheme ?? false</c>.
    /// </summary>
    public static bool ResolveEffectiveIsLightTheme(AppSettings? settings, AppTheme? theme)
    {
        if (settings == null || theme == null) return theme?.IsLightTheme ?? false;

        return settings.ThemeMode switch
        {
            SettingsThemeMode.Light => true,
            SettingsThemeMode.Dark => false,
            _ => theme.IsLightTheme,
        };
    }

    /// <summary>
    /// Push every theme-derived brush / radius / slider-thumb value onto the application's resource
    /// dictionary so DynamicResource bindings refresh on the next layout pass.
    /// No-op when <paramref name="theme"/> is null.
    /// </summary>
    public static void Apply(Application app, AppTheme? theme, AppSettings? settings, bool isLightTheme)
    {
        if (theme == null) return;

        ResourceDictionary res = app.Resources;

        // Core colors (user overrides win).
        res["ThemeBackground"] = new SolidColorBrush(theme.ResolveBackground(settings, isLightTheme));
        res["ThemeForeground"] = new SolidColorBrush(theme.ResolveForeground(settings, isLightTheme));
        res["ThemeBorder"] = new SolidColorBrush(theme.Border.For(isLightTheme));
        res["ThemeHover"] = new SolidColorBrush(theme.Hover.For(isLightTheme));
        res["ThemePressed"] = new SolidColorBrush(theme.Pressed.For(isLightTheme));
        res["ThemeSeparator"] = new SolidColorBrush(theme.Separator.For(isLightTheme));
        res["ThemeDisabledForeground"] = new SolidColorBrush(theme.DisabledForeground.For(isLightTheme));
        res["ThemeAccent"] = new SolidColorBrush(theme.Accent.For(isLightTheme));

        res["ThemeSecondaryForeground"] = new SolidColorBrush(theme.SecondaryForeground.For(isLightTheme));
        res["ThemeFooterBackground"] = new SolidColorBrush(theme.FooterBackground.For(isLightTheme));

        // Win11 Settings card background (slightly lighter than body).
        res["ThemeCardBackground"] = new SolidColorBrush(theme.CardBackground.For(isLightTheme));

        // Win11 input control background (text boxes, combo boxes, buttons).
        res["ThemeControlBackground"] = new SolidColorBrush(theme.ControlBackground.For(isLightTheme));

        // Focused TextBox: a shade darker than ThemeControlBackground so the focused state stays
        // visible without collapsing toward the window bg.
        res["ThemeTextBoxFocused"] = new SolidColorBrush(theme.TextBoxFocused.For(isLightTheme));
        res["ThemeSliderTrack"] = new SolidColorBrush(theme.SliderTrack.For(isLightTheme));
        res["ThemeSliderProgress"] = new SolidColorBrush(theme.SliderProgress.For(isLightTheme));
        res["ThemeSliderThumb"] = new SolidColorBrush(theme.SliderThumb.For(isLightTheme));

        // Peak meter overlays. Two solid colors (no light/dark variant), both live-preview-aware
        // so the picker drag updates each brush in real time before the user closes the picker.
        // MeterPeakBrush paints the base bar to min(L, R); MeterPeakStereoBrush paints on top to
        // max(L, R) and is translucent by default so the stereo extension reads as a halo.
        // Fallbacks parse the *DefaultHex consts in AppSettings via NullableThemeColor so the
        // baseline matches the picker's Reset/Default seed without duplicate literal copies here.
        Color meterPeakColor = settings?.EffectiveMeterPeakColor
            ?? NullableThemeColor.ParseHexOrDefault(AppSettings.MeterPeakColorDefaultHex, Colors.White);
        res["MeterPeakBrush"] = new SolidColorBrush(meterPeakColor);
        Color meterPeakStereoColor = settings?.EffectiveMeterPeakStereoColor
            ?? NullableThemeColor.ParseHexOrDefault(
                AppSettings.MeterPeakStereoColorDefaultHex,
                Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
        res["MeterPeakStereoBrush"] = new SolidColorBrush(meterPeakStereoColor);

        // Confirm-overlay scrim. SettingsWindow's overlay uses this brush as its Background, so
        // theme switches re-tint the dim layer without touching the XAML.
        res["FlyoutOverlayBackdropBrush"] = new SolidColorBrush(theme.FlyoutOverlayBackdrop.For(isLightTheme));
        // ThemeButtonHover / ThemeButtonPressed are intentionally stronger-contrast variants of
        // ThemeHover / ThemePressed (theme.ButtonHover vs theme.Hover, different palette entries).
        // Used by the flyout's device-icon and grid app-icon hover pills where the standard subtle
        // settings hover would disappear against the ThemeFooterBackground card. Do not collapse.
        res["ThemeButtonHover"] = new SolidColorBrush(theme.ButtonHover.For(isLightTheme));
        res["ThemeButtonPressed"] = new SolidColorBrush(theme.ButtonPressed.For(isLightTheme));
        res["ThemeIconForeground"] = new SolidColorBrush(theme.IconForeground.For(isLightTheme));

        // Chrome brushes for control templates whose triggers used to hardcode hex literals in App.xaml.
        // These are theme-agnostic single-color values
        // promoting to per-theme is a one-line lift (.For(isLightTheme)) if visual design ever requires it.
        res["ToggleSwitchOnTrackBrush"] = new SolidColorBrush(theme.ToggleSwitchOnTrack.Light);
        res["ToggleSwitchOnThumbBrush"] = new SolidColorBrush(theme.ToggleSwitchOnThumb.Light);
        res["CloseButtonHoverBrush"] = new SolidColorBrush(theme.CloseButtonHover.Light);
        res["CloseButtonPressedBrush"] = new SolidColorBrush(theme.CloseButtonPressed.Light);
        res["CloseButtonGlyphActiveBrush"] = new SolidColorBrush(theme.CloseButtonGlyphActive.Light);

        res["GlyphSettings"] = theme.GlyphSettings;

        // Rounded-corners toggle:
        // map every literal radius in XAML to a resource that evaluates to 0 when disabled,
        // and the original visual value when on.
        bool rounded = settings?.EnableRoundedCorners ?? true;
        res["CornerRadiusTiny"] = new CornerRadius(rounded ? 1.5 : 0);
        res["CornerRadiusSmall"] = new CornerRadius(rounded ? 2 : 0);
        res["CornerRadiusScrollThumb"] = new CornerRadius(rounded ? 3 : 0);
        res["CornerRadiusScrollThumbExpanded"] = new CornerRadius(rounded ? 7 : 0);
        res["CornerRadiusMedium"] = new CornerRadius(rounded ? 4 : 0);
        res["CornerRadiusLarge"] = new CornerRadius(rounded ? 6 : 0);
        res["CornerRadiusFlyout"] = new CornerRadius(rounded ? 8 : 0);
        res["CornerRadiusHuge"] = new CornerRadius(rounded ? 16 : 0);
        res["CornerRadiusFooterBottom"] = new CornerRadius(0, 0, rounded ? 8 : 0, rounded ? 8 : 0);

        // Slider thumb: resolve the user's selected option (or default) and push every part of its
        // shape onto DynamicResource keys the flyout's thumb template binds against.
        SliderThumbGlyphOption thumb = ResolveSliderThumbOption(settings);
        res["SliderThumbGlyph"] = thumb.Glyph;
        res["SliderThumbGlyphFont"] = new FontFamily(thumb.FontFamily);
        res["SliderThumbGlyphSize"] = thumb.FontSize;
        res["SliderThumbGlyphWidth"] = thumb.Width;
        res["SliderThumbGlyphHeight"] = thumb.Height;
        res["SliderThumbGlyphScaleX"] = thumb.XScale;
        res["SliderThumbGlyphVisibility"] = thumb.IsGlyph ? Visibility.Visible : Visibility.Collapsed;
        res["SliderThumbCapsuleVisibility"] = thumb.IsCapsule ? Visibility.Visible : Visibility.Collapsed;

        // Capsule corner radius = half the smaller dimension -> semicircular ends, straight sides.
        // Border.CornerRadius doesn't auto-clamp, so an over-large value renders as a lens, not a pill.
        // Goes to 0 when the user disables rounded corners - same Border then serves as a sharp bar.
        double capsuleRadius = rounded ? Math.Min(thumb.Width, thumb.Height) / 2.0 : 0;
        res["CornerRadiusCapsule"] = new CornerRadius(capsuleRadius);
    }

    private static SliderThumbGlyphOption ResolveSliderThumbOption(AppSettings? settings)
    {
        List<SliderThumbGlyphOption> options =
            settings?.SliderThumbOptions is { Count: > 0 } list
                ? list
                : SliderThumbGlyphOption.CreateDefaults();

        string name = settings?.SliderThumbGlyph ?? "Capsule";
        return options.FirstOrDefault(o => o.Name == name) ?? options[0];
    }
}
