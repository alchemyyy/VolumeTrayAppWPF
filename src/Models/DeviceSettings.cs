using System.IO;
using System.Xml;
using System.Xml.Serialization;

namespace VolumeTrayAppWPF.Models;

/// <summary>
/// One row of persisted per-device UI state. The Id is the Windows audio endpoint
/// id from MMDevice -- the same string <see cref="Audio.AudioDevice.Id"/> exposes.
/// Properties default to the same values a brand-new VolumeFlyoutCell would render, so a missing
/// entry behaves identically to one with all defaults.
/// </summary>
public class DeviceSettingsEntry
{
    [XmlAttribute] public string Id { get; set; } = "";
    [XmlAttribute] public bool IsAppDrawerExpanded { get; set; } = true;
}

/// <summary>
/// Persisted per-device UI state collection. Backs devices.xml in the LocalAppData app folder,
/// the same directory as settings.xml. Only state that is specific to a single endpoint belongs
/// here; everything global lives in <see cref="AppSettings"/>.
///
/// Reads are non-destructive (<see cref="Find"/> returns null for unknown ids); writes go through
/// <see cref="GetOrCreate"/> so a device only gets a row on its first persisted edit, not on every
/// flyout open.
/// </summary>
[XmlRoot("Devices")]
public class DeviceSettings
{
    [XmlElement("Device")]
    public List<DeviceSettingsEntry> Devices { get; set; } = [];

    /// <summary>Returns the entry for the given endpoint id, or null when no row exists.</summary>
    public DeviceSettingsEntry? Find(string id)
    {
        for (int i = 0; i < Devices.Count; i++)
        {
            if (string.Equals(Devices[i].Id, id, StringComparison.Ordinal)) return Devices[i];
        }
        return null;
    }

    /// <summary>Returns the existing entry or appends a fresh default row tagged with the given id.</summary>
    public DeviceSettingsEntry GetOrCreate(string id)
    {
        DeviceSettingsEntry? existing = Find(id);
        if (existing != null) return existing;
        DeviceSettingsEntry entry = new() { Id = id };
        Devices.Add(entry);
        return entry;
    }

    public static string GetDefaultPath()
    {
        string appDataFolder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string appFolder = Path.Combine(appDataFolder, Program.ApplicationName);
        Directory.CreateDirectory(appFolder);
        return Path.Combine(appFolder, "devices.xml");
    }

    public void Save() => Save(GetDefaultPath());

    public void Save(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            XmlSerializerNamespaces namespaces = new();
            namespaces.Add("", "");

            XmlWriterSettings writerSettings = new()
            {
                Indent = true,
                IndentChars = "  ",
                NewLineChars = Environment.NewLine,
                NewLineHandling = NewLineHandling.Replace
            };

            using FileStream stream = new(path, FileMode.Create);
            using XmlWriter writer = XmlWriter.Create(stream, writerSettings);
            XmlSerializer serializer = new(typeof(DeviceSettings));
            serializer.Serialize(writer, this, namespaces);
        }
        catch
        {
            // best-effort
        }
    }

    public static DeviceSettings LoadOrDefault() => LoadOrDefault(GetDefaultPath());

    public static DeviceSettings LoadOrDefault(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                using FileStream stream = new(path, FileMode.Open);
                XmlSerializer serializer = new(typeof(DeviceSettings));
                if (serializer.Deserialize(stream) is DeviceSettings loaded) return loaded;
            }
        }
        catch
        {
            // fall through to default
        }
        return new DeviceSettings();
    }
}
