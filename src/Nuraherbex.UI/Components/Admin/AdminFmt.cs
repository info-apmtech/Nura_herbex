using System.Globalization;

namespace Nuraherbex.UI.Components.Admin;

/// <summary>Formatting helpers (JS toLocaleString('en-IN') equivalents that do not depend on ICU data).</summary>
public static class AdminFmt
{
    /// <summary>12,34,567 style Indian digit grouping.</summary>
    public static string Inr(decimal value)
    {
        var rounded = Math.Round(value, 0, MidpointRounding.AwayFromZero);
        var neg = rounded < 0;
        var s = Math.Abs(rounded).ToString("0", CultureInfo.InvariantCulture);
        if (s.Length > 3)
        {
            var head = s[..^3];
            var tail = s[^3..];
            var parts = new List<string>();
            while (head.Length > 2) { parts.Insert(0, head[^2..]); head = head[..^2]; }
            if (head.Length > 0) parts.Insert(0, head);
            s = string.Join(",", parts) + "," + tail;
        }
        return (neg ? "-" : "") + s;
    }

    public static string Date(DateTime d) => d.ToLocalTime().ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
    public static string DateShort(DateTime d) => d.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);
    public static string DateTimeFull(DateTime d) => d.ToLocalTime().ToString("dd/MM/yyyy, hh:mm:ss tt", CultureInfo.InvariantCulture);
    public static string DateTimeShort(DateTime d) => d.ToLocalTime().ToString("dd MMM, HH:mm", CultureInfo.InvariantCulture);

    public static bool Has(string? haystack, string needle) =>
        haystack?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;
}
