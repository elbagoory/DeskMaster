using DeskMaster.Core.Cleanup;
using DeskMaster.Core.Localization;
using DeskMaster.Core.Relocation;

namespace DeskMaster.Core.Tests;

[Collection("Language")]
public class LocalizationTests
{
    [Fact]
    public void Every_built_in_rule_has_an_arabic_translation()
    {
        var translated = Translations.RuleIds("ar");
        var missing = RuleCatalog.LoadBuiltIn().Select(r => r.Id).Where(id => !translated.Contains(id)).ToList();
        Assert.True(missing.Count == 0, "Missing Arabic rule translations: " + string.Join(", ", missing));
    }

    [Fact]
    public void Arabic_names_are_applied_when_the_language_is_arabic()
    {
        Translations.Language = "ar";
        try
        {
            var rule = RuleCatalog.LoadForCurrentOs().FirstOrDefault(r => r.Id is "win-user-temp" or "linux-thumbnails" or "mac-user-logs");
            Assert.NotNull(rule);
            Assert.Matches(@"\p{IsArabic}", rule.Name);
            Assert.Contains(RelocationService.LoadDefinitions(), d => d.Name == "نماذج Ollama");
        }
        finally
        {
            Translations.Language = "en";
        }
    }

    [Fact]
    public void English_is_unchanged_by_default()
    {
        Assert.Equal("Temporary files", RuleCatalog.LoadBuiltIn().Single(r => r.Id == "win-user-temp").Name);
    }
}
