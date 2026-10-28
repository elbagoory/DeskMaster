using System.Globalization;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace DeskMaster.App.Localization;

/// <summary>
/// UI language. English text is the key, so XAML and code stay readable; a missing translation
/// falls back to English. The language is chosen at start-up (Settings asks for a restart).
/// </summary>
public static class Loc
{
    private static IReadOnlyDictionary<string, string>? _table;

    public static string Language { get; private set; } = "en";

    public static bool IsRightToLeft => Language == "ar";

    public static FlowDirection FlowDirection => IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    /// <summary>Picks the language: the setting, or the OS language when the setting is empty.</summary>
    public static void Initialize(string setting)
    {
        Language = string.IsNullOrEmpty(setting)
            ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar" ? "ar" : "en"
            : setting;
        _table = Language == "ar" ? Arabic.Strings : null;
        Core.Localization.Text.Translator = T;
        Core.Localization.Translations.Language = Language;
        Core.Formatting.ByteSize.IsolateForRightToLeft = IsRightToLeft;
    }

    public static string T(string english) =>
        _table is not null && _table.TryGetValue(english, out var translated) ? translated : english;

    public static string F(string englishFormat, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(englishFormat), args);
}

/// <summary>XAML: <c>Text="{l:T 'Free up space'}"</c>.</summary>
public sealed class TExtension(string text) : MarkupExtension
{
    public string Text { get; } = text;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Text);
}
