using System.ComponentModel;
using System.Windows.Controls;
using VolumeTrayAppWPF.Audio;
using VolumeTrayAppWPF.Models;
using VolumeTrayAppWPF.Utils;
using VolumeTrayAppWPF.Visuals;

namespace VolumeTrayAppWPF.WPF;

/// <summary>
/// Composes the tray-icon side of the app shell:
/// owns the <see cref="TrayIconManager"/>, the <see cref="TrayIconRenderer"/>,
/// tracks the currently-default audio device for icon updates,
/// and produces fresh (icon, tooltip) pairs through <see cref="RequestRefresh"/>.
/// Dispose tears down event subscriptions and the underlying tray icon in one shot
/// so <c>App.ExitApplication</c> collapses to a single dispose call instead of
/// the per-field unhook block that used to live there.
/// </summary>
internal sealed class TrayShell : IDisposable
{
    private readonly AppTheme _theme;
    private readonly AppSettings _settings;
    private readonly AudioDeviceManager _audioManager;
    private readonly TrayIconRenderer _renderer;
    private readonly TrayIconManager _trayManager;
    private AudioDevice? _trackedDevice;
    private bool _disposed;

    /// <summary>Raised when the user left-mouse-downs the tray icon (host suppresses the LeftClick that follows).</summary>
    public event Action? LeftMouseDown;
    /// <summary>Raised on tray-icon left click.</summary>
    public event Action? LeftClick;
    /// <summary>Raised on tray-icon left double click.</summary>
    public event Action? LeftDoubleClick;
    /// <summary>Raised on tray-icon mouse-wheel scroll (delta in WHEEL_DELTA units).</summary>
    public event Action<int>? Scrolled;
    /// <summary>Raised on tray-icon right click with the cursor position.</summary>
    public event Action<System.Windows.Point>? RightClick;
    /// <summary>Raised when the user clicks the body of a balloon notification raised via <see cref="ShowBalloon"/>.</summary>
    public event Action? BalloonClicked;

    /// <summary>Currently tracked audio device (the system default). Null until first attach.</summary>
    public AudioDevice? TrackedDevice => _trackedDevice;

    public TrayShell(AppTheme theme, AppSettings settings, AudioDeviceManager audioManager)
    {
        _theme = theme;
        _settings = settings;
        _audioManager = audioManager;

        _renderer = new TrayIconRenderer(theme) { Glyph = GlyphCatalog.PLAYBACK_VOLUME_SILENT };

        _trayManager = new TrayIconManager
        {
            IsScrollEnabled = settings.TrayScrollEnabled,
        };
        _trayManager.LeftMouseDown += OnTrayLeftMouseDown;
        _trayManager.LeftClick += OnTrayLeftClick;
        _trayManager.LeftDoubleClick += OnTrayLeftDoubleClick;
        _trayManager.RightClick += OnTrayRightClick;
        _trayManager.RefreshNeeded += RequestRefresh;
        _trayManager.Scrolled += OnTrayScrolled;
        _trayManager.BalloonClicked += OnTrayBalloonClicked;

        _audioManager.PropertyChanged += OnAudioManagerPropertyChanged;
        AttachToTrackedDevice(_audioManager.DefaultDevice);
    }

    /// <summary>
    /// Show the tray icon. Call after construction so the first <see cref="RequestRefresh"/> has
    /// run and the icon has a real value before Shell_NotifyIconW makes it visible.
    /// </summary>
    public void Show()
    {
        RequestRefresh();
        _trayManager.IsVisible = true;
    }

    /// <summary>
    /// Re-render the tray icon + tooltip from the tracked device's current state.
    /// Throttled by <see cref="TrayIconManager"/> so a flurry of property-change events collapses
    /// into one Shell_NotifyIconW write per cooldown.
    /// </summary>
    public void RequestRefresh() => _trayManager.Update(GetTrayIconAndTooltip);

    /// <summary>
    /// Forward AppSettings.Changed-driven scroll-enabled / icon-style flips into the tray manager.
    /// </summary>
    public void ApplySettings()
    {
        _trayManager.IsScrollEnabled = _settings.TrayScrollEnabled;
        RequestRefresh();
    }

    /// <summary>
    /// Show the supplied context menu at <paramref name="point"/> using the configured placement strategy.
    /// </summary>
    public void ShowContextMenu(ContextMenu menu, System.Windows.Point point, ContextMenuPosition placement) =>
        _trayManager.ShowContextMenu(menu, point, placement);

    /// <summary>
    /// Push a balloon notification through the tray icon. Title is clipped to 63 chars and body to
    /// 255 chars by the shell so callers don't need to truncate.
    /// </summary>
    public void ShowBalloon(string title, string message) => _trayManager.ShowBalloon(title, message);

    private void OnTrayLeftMouseDown() => LeftMouseDown?.Invoke();
    private void OnTrayLeftClick() => LeftClick?.Invoke();
    private void OnTrayLeftDoubleClick() => LeftDoubleClick?.Invoke();
    private void OnTrayRightClick(System.Windows.Point point) => RightClick?.Invoke(point);
    private void OnTrayScrolled(int delta) => Scrolled?.Invoke(delta);
    private void OnTrayBalloonClicked() => BalloonClicked?.Invoke();

    private void OnAudioManagerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AudioDeviceManager.DefaultDevice))
            AttachToTrackedDevice(_audioManager.DefaultDevice);
    }

    private void AttachToTrackedDevice(AudioDevice? device)
    {
        if (ReferenceEquals(_trackedDevice, device)) return;

        if (_trackedDevice != null)
            _trackedDevice.PropertyChanged -= OnTrackedDevicePropertyChanged;

        _trackedDevice = device;

        if (_trackedDevice != null)
            _trackedDevice.PropertyChanged += OnTrackedDevicePropertyChanged;

        RequestRefresh();
    }

    private void OnTrackedDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Master volume / mute changes are the only mutations that affect the tray icon glyph or
        // tooltip; ignore the rest (PeakValue, FriendlyName, IsDefault) so the throttled refresh
        // path doesn't fire on every meter tick.
        if (e.PropertyName == nameof(AudioDevice.Volume) ||
            e.PropertyName == nameof(AudioDevice.IsMuted))
            RequestRefresh();
    }

    private (Icon? icon, string tooltip) GetTrayIconAndTooltip()
    {
        AudioDevice? device = _trackedDevice;
        bool isLight = ThemeResources.ResolveEffectiveIsLightTheme(_settings, _theme);

        if (device == null)
        {
            _renderer.IsLightTheme = isLight;
            _renderer.Glyph = GlyphCatalog.PLAYBACK_VOLUME_SILENT;
            _renderer.BackdropGlyph = GlyphCatalog.PLAYBACK_VOLUME_HIGH;
            return (_renderer.CreateIcon(), "No audio device");
        }

        _renderer.IsLightTheme = isLight;
        string foregroundGlyph = GlyphCatalog.GetVolumeTier(device.Volume, device.IsMuted);
        _renderer.Glyph = foregroundGlyph;
        // Full-volume speaker as a dimmed backdrop on partial speaker-tier states, mirroring how
        // the OS shell paints Wi-Fi: the silhouette of the full glyph stays present so the
        // partial foreground reads as "this much of that". Skip the backdrop when muted (the
        // mute glyph is a distinct icon, not a speaker-tier variant, so the speaker silhouette
        // doesn't belong behind it) and when the foreground IS the full glyph.
        _renderer.BackdropGlyph = device.IsMuted || foregroundGlyph == GlyphCatalog.PLAYBACK_VOLUME_HIGH
            ? null
            : GlyphCatalog.PLAYBACK_VOLUME_HIGH;

        int percent = (int)Math.Round(device.Volume * 100);
        string tooltip = device.IsMuted
            ? $"{device.FriendlyName}: muted"
            : $"{device.FriendlyName}: {percent}%";
        return (_renderer.CreateIcon(), tooltip);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _audioManager.PropertyChanged -= OnAudioManagerPropertyChanged;

        if (_trackedDevice != null)
        {
            _trackedDevice.PropertyChanged -= OnTrackedDevicePropertyChanged;
            _trackedDevice = null;
        }

        _trayManager.LeftMouseDown -= OnTrayLeftMouseDown;
        _trayManager.LeftClick -= OnTrayLeftClick;
        _trayManager.LeftDoubleClick -= OnTrayLeftDoubleClick;
        _trayManager.RightClick -= OnTrayRightClick;
        _trayManager.RefreshNeeded -= RequestRefresh;
        _trayManager.Scrolled -= OnTrayScrolled;
        _trayManager.BalloonClicked -= OnTrayBalloonClicked;

        Safe.Dispose(_trayManager);
        Safe.Dispose(_renderer);
    }
}
