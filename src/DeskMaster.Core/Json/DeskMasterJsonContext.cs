using System.Text.Json.Serialization;
using DeskMaster.Core.Cleanup;
using DeskMaster.Core.Relocation;
using DeskMaster.Core.Settings;

namespace DeskMaster.Core.Json;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<CleanupRule>))]
[JsonSerializable(typeof(List<RelocationDefinition>))]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class DeskMasterJsonContext : JsonSerializerContext;
