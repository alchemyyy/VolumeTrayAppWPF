// Uncomment to pad the tray context menu with 40 dummy device entries.
// Verifies ShowContextMenu positioning when the menu overflows the work area.
// #define DEBUG_OVERFLOW_DUMMIES

using System.Diagnostics;
using System.Windows.Controls;
using VolumeTrayAppWPF.Audio;
using VolumeTrayAppWPF.Localization;
using VolumeTrayAppWPF.Models;

namespace VolumeTrayAppWPF.WPF;

/// <summary>
/// Builder for the tray-icon right-click <see cref="ContextMenu"/>.
/// Pure factory: every call returns a fresh menu reflecting the current device / settings state,
/// styled entirely via the implicit ContextMenu / MenuItem styles in App.xaml.
/// Sections (in order): visible devices, sound-devices link, bluetooth link, settings, exit -
/// with Separator entries that <see cref="DissolveSeparatorsIntoNeighbors"/> rewrites into
/// per-item Tag-driven top/bottom rules on the surrounding MenuItems.
/// </summary>
internal static class TrayContextMenu
{
    // 2-dot ellipsis (".."), per the per-flow tray-menu name spec.
    // Distinct from the Unicode horizontal ellipsis the OS uses elsewhere so the truncation reads
    // as deliberate to anyone who has tuned the max length.
    private const string TrayMenuTruncationSuffix = "..";

    private const string MenuItemTagHasTopRule = "HasTopRule";
    private const string MenuItemTagHasBottomRule = "HasBottomRule";

    /// <summary>
    /// Build a fresh tray context menu wired to the supplied audio manager / settings state.
    /// The <paramref name="openSettings"/> / <paramref name="exitApplication"/> callbacks invoke
    /// the host's existing handlers so the menu doesn't need a reference back to the App instance.
    /// </summary>
    public static ContextMenu Build(
        AudioDeviceManager? audioManager,
        AppSettings? settings,
        Action openSettings,
        Action exitApplication)
    {
        // Items-host (BottomAnchoredItemsPanel) is wired into the ContextMenu Template in App.xaml,
        // not here - the template hard-codes the items host directly (no ItemsPresenter), so a
        // programmatic ContextMenu.ItemsPanel setter has no effect.
        ContextMenu contextMenu = new();

        // Section 1: visible devices, sourced from FlyoutDeviceOrdering so the tray menu and the
        // flyout never disagree on what counts as visible. Same set, same in-flyout order; we just
        // flip top-to-bottom here because the flyout stacks bottom-up with the default at the
        // bottom while a tray menu reads top-down with the default at the top.
        if (audioManager != null && settings != null)
        {
            List<AudioDevice> orderedForFlyout = FlyoutDeviceOrdering.Build(audioManager.Devices, settings);
            for (int i = orderedForFlyout.Count - 1; i >= 0; i--)
                contextMenu.Items.Add(BuildDeviceMenuItem(orderedForFlyout[i], settings));
            if (orderedForFlyout.Count > 0) contextMenu.Items.Add(new Separator());
        }

#if DEBUG_OVERFLOW_DUMMIES
        // Pad with 40 dummy entries so the menu overflows the work area.
        for (int debugIndex = 0; debugIndex < 40; debugIndex++)
            contextMenu.Items.Add(new MenuItem { Header = $"Dummy Playback Device {debugIndex + 1:00}" });
        contextMenu.Items.Add(new Separator());
#endif

        MenuItem soundDevicesItem = new() { Header = LocalizationManager.Instance["Tray_SoundDevices"] };
        soundDevicesItem.Click += (_, _) => OpenSoundDevicesPanel();

        MenuItem bluetoothItem = new() { Header = LocalizationManager.Instance["Tray_Bluetooth"] };
        bluetoothItem.Click += (_, _) => OpenBluetoothFlyout();

        MenuItem settingsItem = new() { Header = LocalizationManager.Instance["Tray_Settings"] };
        settingsItem.Click += (_, _) => openSettings();

        MenuItem exitItem = new() { Header = LocalizationManager.Instance["Tray_Exit"] };
        exitItem.Click += (_, _) => exitApplication();

        contextMenu.Items.Add(soundDevicesItem);
        contextMenu.Items.Add(bluetoothItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(settingsItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(exitItem);

        // Dissolve every Separator:
        // tag the preceding item HasBottomRule (it now paints the 1px rule inside its own ControlTemplate),
        // and the following item HasTopRule (it gets a 2px extra top gap so the rule stays visually centred
        // between pills). The Separator itself is removed.
        // Result: each rule is owned by the adjacent MenuItems' hit-test regions, eliminating the dead band
        // that a real Separator sibling created.
        DissolveSeparatorsIntoNeighbors(contextMenu);

        return contextMenu;
    }

    private static MenuItem BuildDeviceMenuItem(AudioDevice device, AppSettings settings)
    {
        MenuItem item = new() { Header = FormatTrayMenuDeviceName(device, settings) };
        item.Click += (_, _) => DeviceShellLinks.OpenDeviceProperties(device);
        return item;
    }

    /// <summary>
    /// Picks the slice of <paramref name="device"/>'s name to render in the tray context menu
    /// based on the per-flow style setting, then truncates with a 2-dot ellipsis when the slice
    /// exceeds <see cref="AppSettings.TrayMenuDeviceNameMaxLength"/>.
    /// </summary>
    private static string FormatTrayMenuDeviceName(AudioDevice device, AppSettings settings)
    {
        TrayMenuDeviceNameStyle style = device.IsCaptureDevice
            ? settings.TrayMenuRecordingDeviceNameStyle
            : settings.TrayMenuPlaybackDeviceNameStyle;

        string raw = style switch
        {
            TrayMenuDeviceNameStyle.Name => device.DeviceDescription,
            TrayMenuDeviceNameStyle.Model => device.InterfaceFriendlyName,
            _ => device.FriendlyName,
        };

        if (string.IsNullOrEmpty(raw)) return device.FriendlyName;

        int max = settings.TrayMenuDeviceNameMaxLength;
        if (raw.Length <= max) return raw;

        // Keep room for the suffix inside the cap; if max is smaller than the suffix itself we
        // degrade to a hard cut at max chars rather than producing a string longer than requested.
        int keep = Math.Max(0, max - TrayMenuTruncationSuffix.Length);
        return keep == 0 ? raw[..Math.Min(raw.Length, max)] : raw[..keep] + TrayMenuTruncationSuffix;
    }

    private static void DissolveSeparatorsIntoNeighbors(ContextMenu menu)
    {
        // Walk back-to-front so RemoveAt doesn't shift indices we still need to read.
        for (int i = menu.Items.Count - 1; i >= 0; i--)
        {
            if (menu.Items[i] is not Separator) continue;

            if (i > 0 && menu.Items[i - 1] is MenuItem prev)
                prev.Tag = MenuItemTagHasBottomRule;

            if (i + 1 < menu.Items.Count && menu.Items[i + 1] is MenuItem next)
                next.Tag = MenuItemTagHasTopRule;

            menu.Items.RemoveAt(i);
        }
    }

    // Opens the classic Sound control panel on the Playback tab via mmsys.cpl.
    // Other valid panel names: "recording", "sounds", "communications".
    private static void OpenSoundDevicesPanel()
    {
        try
        {
            using Process? _ = Process.Start(new ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = "shell32.dll,Control_RunDLL mmsys.cpl,,playback",
                UseShellExecute = false,
            });
        }
        catch (Exception ex) { WPFLog.Log($"TrayContextMenu.OpenSoundDevicesPanel: {ex.Message}"); }
    }

    // Opens the Windows 11 Quick Settings Bluetooth flyout via the ms-actioncenter URI.
    // Launched through explorer.exe so the URI handler resolves consistently across builds.
    private static void OpenBluetoothFlyout()
    {
        try
        {
            using Process? _ = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "ms-actioncenter:controlcenter/bluetooth",
                UseShellExecute = false,
            });
        }
        catch (Exception ex) { WPFLog.Log($"TrayContextMenu.OpenBluetoothFlyout: {ex.Message}"); }
    }
}
