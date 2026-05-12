using System.IO;

namespace VolumeTrayAppWPF.Utils;

/// <summary>
/// Single-source NormalizePath used by every install / shortcut / running-exe path comparison in the codebase.
/// Trims trailing directory separators and resolves to a full path so two semantically identical strings
/// ("C:\App", "C:\App\", "C:/App") compare equal under <see cref="StringComparison.OrdinalIgnoreCase"/>.
/// </summary>
public static class PathNormalization
{
    /// <summary>
    /// Returns the canonical full path of <paramref name="path"/> with trailing separators stripped,
    /// or the empty string when input is null/empty. Falls back to the input string on any I/O exception
    /// so callers never observe an exception from this helper.
    /// </summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            // Filesystem-validation can throw on illegal characters; preserve the input so callers
            // can still do a degraded equality check rather than crashing.
            return path;
        }
    }
}
