using System.Text.Json;
using DeskMaster.Core.Json;
using DeskMaster.Core.Platform;

namespace DeskMaster.Core.Cleanup;

/// <summary>Loads the built-in rules plus any user rules from <c>{Config}/DeskMaster/rules/*.json</c>.</summary>
public static class RuleCatalog
{
    public const string BuiltInResource = "DeskMaster.Core.Data.cleanup-rules.json";

    public static string UserRulesFolder =>
        Path.Combine(PathResolver.CurrentTokens["Config"], "DeskMaster", "rules");

    /// <summary>Rules for this OS. User rules with the same id replace built-in ones.</summary>
    public static IReadOnlyList<CleanupRule> LoadForCurrentOs(ICollection<string>? warnings = null)
    {
        var byId = new Dictionary<string, CleanupRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in LoadBuiltIn())
            byId[rule.Id] = rule;

        if (Directory.Exists(UserRulesFolder))
        {
            foreach (var file in Directory.EnumerateFiles(UserRulesFolder, "*.json"))
            {
                try
                {
                    foreach (var rule in Parse(File.ReadAllText(file)))
                        byId[rule.Id] = rule;
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    warnings?.Add($"Skipped rule file {Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }

        var rules = byId.Values.Where(r => r.AppliesTo(PlatformInfo.Os) && !string.IsNullOrWhiteSpace(r.Id)).ToList();
        Localization.Translations.Apply(rules);
        return rules;
    }

    public static IReadOnlyList<CleanupRule> LoadBuiltIn()
    {
        using var stream = typeof(RuleCatalog).Assembly.GetManifestResourceStream(BuiltInResource)
                           ?? throw new InvalidOperationException("Built-in rule file is missing from the build.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static List<CleanupRule> Parse(string json) =>
        JsonSerializer.Deserialize(json, DeskMasterJsonContext.Default.ListCleanupRule) ?? [];
}
