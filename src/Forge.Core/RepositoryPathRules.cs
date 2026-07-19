using System.Globalization;
using System.Text;

namespace Forge.Core;

public static class RepositoryPathRules
{
    private static readonly char[] ExtraInvalidCharacters = ['\0', '*', '?', '[', ']'];
    private static readonly HashSet<string> WindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static bool IsSafeRelativePath(string? value, int maximumLength = int.MaxValue)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value != value.Trim()) return false;
        if (!value.IsNormalized(NormalizationForm.FormC)) return false;
        if (Path.IsPathRooted(value) || Path.IsPathFullyQualified(value) || value.StartsWith('/') || value.StartsWith('\\')) return false;
        if (value.Contains(':') || value.IndexOfAny(ExtraInvalidCharacters) >= 0) return false;
        if (value.Any(character => char.GetUnicodeCategory(character) is UnicodeCategory.Control or UnicodeCategory.Format)) return false;
        var normalized = Normalize(value);
        var segments = normalized.Split('/');
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment != segment.Trim() ||
                segment.StartsWith('-') ||
                segment.EndsWith('.') || segment is "." or ".." ||
                segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                IsWindowsDeviceName(segment))) return false;
        return !segments.Any(segment => segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0);
    }

    public static string Normalize(string value) => value.Replace('\\', '/').Normalize(NormalizationForm.FormC);

    public static StringComparer Comparer { get; } = StringComparer.OrdinalIgnoreCase;

    private static bool IsWindowsDeviceName(string segment)
    {
        var stem = segment.Split('.', 2)[0];
        if (WindowsDeviceNames.Contains(stem)) return true;
        if (stem.Length != 4) return false;
        var prefix = stem[..3];
        var suffix = stem[3];
        return (prefix.Equals("COM", StringComparison.OrdinalIgnoreCase) ||
                prefix.Equals("LPT", StringComparison.OrdinalIgnoreCase)) &&
               suffix is '\u00b9' or '\u00b2' or '\u00b3';
    }
}
