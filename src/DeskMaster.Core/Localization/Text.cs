using System.Globalization;

namespace DeskMaster.Core.Localization;

/// <summary>
/// Translation hook for user-facing text produced by the core. English text is the key; the app
/// plugs in a translator (see DeskMaster.App/Localization). The CLI stays English.
/// </summary>
public static class Text
{
    public static Func<string, string> Translator { get; set; } = s => s;

    /// <summary>Translates a fixed English sentence.</summary>
    public static string T(string english) => Translator(english);

    /// <summary>Translates an English format string ("Freed {0}.") and fills it in.</summary>
    public static string F(string englishFormat, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Translator(englishFormat), args);
}
