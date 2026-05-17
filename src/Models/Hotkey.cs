using System.Xml.Serialization;

namespace VolumeTrayAppWPF.Models;

/// <summary>
/// Skeleton action set. Extend with project-specific actions in your fork
/// (e.g. domain commands the global hotkey listener should fire).
/// </summary>
public enum HotkeyAction
{
    OpenSettings,
    OpenFlyout,
}

/// <summary>
/// One persisted hotkey binding.
/// Identity = (Action, Parameter, BindingID): per (action, parameter) pair there can be N bindings,
/// distinguished by BindingID. BindingID == 0 is the legacy/primary row; legacy XML files without
/// the attribute deserialise to 0 and so become the primary row by default.
/// Modifiers and VirtualKey are raw Win32 values (MOD_* and VK_*) so the storage shape matches RegisterHotKey
/// directly and the settings model doesn't depend on WPF input enums.
/// MOD_NOREPEAT is added at registration time, never persisted.
/// </summary>
public sealed class HotkeyBinding
{
    [XmlAttribute]
    public HotkeyAction Action { get; set; }

    /// <summary>
    /// Free-form action-specific parameter, encoded as a string so XmlSerializer roundtrips it trivially.
    /// Empty for fixed-target actions; project-specific actions are free to define their own encoding.
    /// </summary>
    [XmlAttribute]
    public string Parameter { get; set; } = string.Empty;

    // MOD_* flags from RegisterHotKey (no MOD_NOREPEAT - added at registration time).
    [XmlAttribute]
    public uint Modifiers { get; set; }

    // VK_* virtual key code passed to RegisterHotKey.
    [XmlAttribute]
    public uint VirtualKey { get; set; }

    [XmlAttribute]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Disambiguator for multiple bindings sharing the same (Action, Parameter).
    /// 0 = primary/legacy row; 1, 2, ... = additional bindings added by the user.
    /// Missing in legacy XML, deserialises to 0.
    /// </summary>
    [XmlAttribute]
    public int BindingID { get; set; }

    /// <summary>
    /// Tombstone flag: true means the user explicitly removed this binding through the UI.
    /// Tombstones are kept in the persisted list (instead of being deleted) so the default-seeder
    /// in HotkeyDefaults.EnsureDefaults can tell that a default was removed on purpose and must
    /// not be re-added on the next launch.
    /// Filtered out of UI display and hotkey registration.
    /// </summary>
    [XmlAttribute]
    public bool RemovedByUser { get; set; }

    public bool IsBound => VirtualKey != 0 && Modifiers != 0;

    /// <summary>
    /// "Any binding for this (Action, Parameter)" lookup.
    /// Use the 3-arg overload when row identity matters for persistence/status.
    /// </summary>
    public bool Matches(HotkeyAction action, string? parameter) =>
        Action == action && string.Equals(Parameter, parameter ?? string.Empty, StringComparison.Ordinal);

    // Strict identity check including the per-row BindingID disambiguator.
    public bool Matches(HotkeyAction action, string? parameter, int bindingID) =>
        Action == action
        && string.Equals(Parameter, parameter ?? string.Empty, StringComparison.Ordinal)
        && BindingID == bindingID;
}

/// <summary>
/// Built-in default hotkey bindings: source of truth for what fresh installs get seeded with,
/// plus the dedupe / top-up logic AppSettings.LoadOrDefault runs on every launch.
/// Lives next to <see cref="HotkeyBinding"/> so AppSettings.cs stays focused on settings state
/// rather than hotkey policy.
/// Skeleton ships with one illustrative binding; replace with your project's own defaults.
/// </summary>
public static class HotkeyDefaults
{
    /// <summary>
    /// The set of built-in hotkey bindings seeded for fresh installs and topped up on every launch.
    /// Identity is (Action, Parameter, BindingID): defaults always live on BindingID 0 (the primary row),
    /// so a user-added secondary binding (BindingID &gt;= 1) for the same action does not block re-seeding
    /// the primary row.
    /// </summary>
    public static IReadOnlyList<HotkeyBinding> Create() => [];

    /// <summary>
    /// True if the binding occupies the same identity slot as one of the built-in defaults
    /// (same Action, Parameter, and BindingID). Used by the settings UI to decide whether removing
    /// a binding should hard-delete it or keep it as a tombstone (RemovedByUser=true) so the default
    /// doesn't reappear on the next launch.
    /// </summary>
    public static bool IsDefaultIdentity(HotkeyAction action, string parameter, int bindingID)
    {
        foreach (HotkeyBinding d in Create())
            if (d.Matches(action, parameter, bindingID)) return true;
        return false;
    }

    /// <summary>
    /// Removes redundant hotkey rows that share the same identity tuple (Action, Parameter, BindingID),
    /// keeping the first occurrence.
    /// Returns true when at least one row was dropped (caller should persist).
    /// </summary>
    public static bool DedupeByIdentity(IList<HotkeyBinding> hotkeys)
    {
        HashSet<(HotkeyAction, string, int)> seen = [];
        int writeIndex = 0;
        for (int readIndex = 0; readIndex < hotkeys.Count; readIndex++)
        {
            HotkeyBinding b = hotkeys[readIndex];
            (HotkeyAction, string, int) key = (b.Action, b.Parameter ?? string.Empty, b.BindingID);
            if (!seen.Add(key)) continue;

            if (writeIndex != readIndex) hotkeys[writeIndex] = b;
            writeIndex++;
        }
        if (writeIndex == hotkeys.Count) return false;

        // List<T>.RemoveRange exists; fall back to remove-from-tail for IList consumers.
        int removeCount = hotkeys.Count - writeIndex;
        for (int i = 0; i < removeCount; i++) hotkeys.RemoveAt(hotkeys.Count - 1);
        return true;
    }

    /// <summary>
    /// Adds any built-in default hotkey bindings that aren't already represented in <paramref name="hotkeys"/>.
    /// "Represented" means: an existing entry with the same (Action, Parameter, BindingID) - including
    /// tombstoned entries with RemovedByUser=true - so a user who has explicitly removed a default
    /// is not re-seeded.
    /// Returns true when at least one default was newly added (caller should persist).
    /// </summary>
    public static bool EnsureDefaults(IList<HotkeyBinding> hotkeys)
    {
        bool added = false;
        foreach (HotkeyBinding d in Create())
        {
            bool present = false;
            foreach (HotkeyBinding existing in hotkeys)
            {
                if (!existing.Matches(d.Action, d.Parameter, d.BindingID)) continue;

                present = true;
                break;
            }
            if (present) continue;

            hotkeys.Add(new HotkeyBinding
            {
                Action = d.Action,
                Parameter = d.Parameter,
                Modifiers = d.Modifiers,
                VirtualKey = d.VirtualKey,
                Enabled = d.Enabled,
                BindingID = d.BindingID,
            });
            added = true;
        }
        return added;
    }
}
