using System.Text.RegularExpressions;

namespace OpenDriverUpdater.Services;

public static partial class VersionUtil
{
    [GeneratedRegex(@"\d+(?:\.\d+){1,3}")]
    private static partial Regex VersionRegex();

    /// <summary>Extrae la última versión numérica de un texto ("Intel - Display - 31.0.101.5590" → 31.0.101.5590).</summary>
    public static string ExtractText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var m = VersionRegex().Matches(text);
        return m.Count > 0 ? m[^1].Value : "";
    }

    public static Version? Parse(string? text)
    {
        var s = ExtractText(text);
        if (s.Length == 0 || !Version.TryParse(s, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
    }

    /// <summary>&gt;0 si a es más nueva, &lt;0 si es más vieja, null si no se pueden comparar.</summary>
    public static int? Compare(string? a, string? b)
    {
        var va = Parse(a);
        var vb = Parse(b);
        if (va is null || vb is null) return null;
        return va.CompareTo(vb);
    }

    /// <summary>
    /// Convierte la versión de Windows de un driver NVIDIA a la versión comercial:
    /// 32.0.15.6094 → 560.94
    /// </summary>
    public static string? NvidiaFromWindows(string? windowsVersion)
    {
        if (string.IsNullOrWhiteSpace(windowsVersion)) return null;
        var parts = windowsVersion.Split('.');
        if (parts.Length < 4 || !parts[2].All(char.IsDigit) || !parts[3].All(char.IsDigit)) return null;
        var digits = parts[2] + parts[3].PadLeft(4, '0');
        if (digits.Length < 5) return null;
        var last5 = digits[^5..];
        return $"{int.Parse(last5[..3])}.{last5[3..]}";
    }
}
