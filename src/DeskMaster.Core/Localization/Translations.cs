using System.Text.Json;
using DeskMaster.Core.Cleanup;
using DeskMaster.Core.Relocation;

namespace DeskMaster.Core.Localization;

/// <summary>Applies translated names and descriptions (Data/translations-{lang}.json) to rules.</summary>
public static class Translations
{
    /// <summary>Current UI language ("en", "ar"); set by the app.</summary>
    public static string Language { get; set; } = "en";

    private static readonly Dictionary<string, JsonDocument?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static void Apply(IEnumerable<CleanupRule> rules)
    {
        if (Load() is not { } doc || !doc.RootElement.TryGetProperty("rules", out var map))
            return;
        foreach (var rule in rules)
        {
            if (!map.TryGetProperty(rule.Id, out var t))
                continue;
            rule.Name = Get(t, "name") ?? rule.Name;
            rule.Description = Get(t, "description") ?? rule.Description;
            rule.Notes = Get(t, "notes") ?? rule.Notes;
        }
    }

    public static void Apply(IEnumerable<RelocationDefinition> definitions)
    {
        if (Load() is not { } doc || !doc.RootElement.TryGetProperty("relocations", out var map))
            return;
        foreach (var def in definitions)
        {
            if (!map.TryGetProperty(def.Id, out var t))
                continue;
            def.Name = Get(t, "name") ?? def.Name;
            def.Description = Get(t, "description") ?? def.Description;
        }
    }

    /// <summary>Ids in a translation file, for completeness tests.</summary>
    public static IReadOnlyCollection<string> RuleIds(string language)
    {
        var doc = Load(language);
        return doc is not null && doc.RootElement.TryGetProperty("rules", out var map)
            ? map.EnumerateObject().Select(p => p.Name).ToList()
            : [];
    }

    private static JsonDocument? Load(string? language = null)
    {
        language ??= Language;
        if (string.IsNullOrEmpty(language) || language == "en")
            return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(language, out var cached))
                return cached;
            using var stream = typeof(Translations).Assembly.GetManifestResourceStream($"DeskMaster.Core.Data.translations-{language}.json");
            var doc = stream is null
                ? null
                : JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            Cache[language] = doc;
            return doc;
        }
    }

    private static string? Get(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
