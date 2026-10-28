using System.Globalization;

namespace DeskMaster.Core.Formatting;

/// <summary>Human-readable byte sizes (binary units, invariant culture).</summary>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>
    /// Wraps sizes in a left-to-right isolate so "92.7 GB" keeps its order inside right-to-left
    /// (Arabic) text. Set by the app; off for the CLI and tests.
    /// </summary>
    public static bool IsolateForRightToLeft { get; set; }

    public static string Format(long bytes) =>
        IsolateForRightToLeft ? "⁦" + FormatCore(bytes) + "⁩" : FormatCore(bytes);

    private static string FormatCore(long bytes)
    {
        if (bytes < 0)
            return "-" + FormatCore(-bytes);

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        if (unit == 0)
            return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");

        var format = value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return value.ToString(format, CultureInfo.InvariantCulture) + " " + Units[unit];
    }

    public static string Percent(double fraction) =>
        (fraction * 100).ToString(fraction >= 0.1 ? "0" : "0.0", CultureInfo.InvariantCulture) + "%";
}
