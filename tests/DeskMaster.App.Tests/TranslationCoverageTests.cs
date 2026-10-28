using System.Text.RegularExpressions;
using DeskMaster.App.Localization;

namespace DeskMaster.App.Tests;

/// <summary>Fails when someone adds UI text without an Arabic translation, or breaks a placeholder.</summary>
public partial class TranslationCoverageTests
{
    [GeneratedRegex(@"\{l:T '([^']*)'\}")]
    private static partial Regex XamlKey();

    // T("...") or F("...") and the "a" : "b" alternatives of F(cond ? "a" : "b", ...)
    [GeneratedRegex("""\b[TF]\(\s*(?:[^()"]*\?\s*)?"((?:[^"\\]|\\.)*)"(?:\s*:\s*"((?:[^"\\]|\\.)*)")?""")]
    private static partial Regex CodeKey();

    [GeneratedRegex(@"\{\d+(:[^}]*)?\}")]
    private static partial Regex Placeholder();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DeskMaster.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static IEnumerable<string> UsedKeys()
    {
        var src = Path.Combine(RepoRoot(), "src");
        foreach (var file in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.Contains("DeskMaster.Cli"))
                continue;
            var text = File.ReadAllText(file);
            if (file.EndsWith(".axaml", StringComparison.Ordinal))
            {
                foreach (Match m in XamlKey().Matches(text))
                    yield return m.Groups[1].Value;
            }
            else if (file.EndsWith(".cs", StringComparison.Ordinal) && !file.EndsWith("Arabic.cs", StringComparison.Ordinal))
            {
                foreach (Match m in CodeKey().Matches(text))
                {
                    for (var g = 1; g <= 2; g++)
                    {
                        if (m.Groups[g].Success)
                            yield return Regex.Unescape(m.Groups[g].Value);
                    }
                }
            }
        }
    }

    [Fact]
    public void Every_ui_text_has_an_arabic_translation()
    {
        var keys = UsedKeys().Distinct().ToList();
        Assert.True(keys.Count > 200, $"Only {keys.Count} keys found; the scanner is probably broken.");
        var missing = keys.Where(k => !Arabic.Strings.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "Missing Arabic translations:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Arabic_keeps_every_placeholder()
    {
        var broken = Arabic.Strings
            .Where(kv => !Placeholder().Matches(kv.Key).Select(m => m.Value).Distinct().Order()
                .SequenceEqual(Placeholder().Matches(kv.Value).Select(m => m.Value).Distinct().Order()))
            .Select(kv => kv.Key)
            .ToList();
        Assert.True(broken.Count == 0, "Placeholders differ for:\n" + string.Join("\n", broken));
    }
}
