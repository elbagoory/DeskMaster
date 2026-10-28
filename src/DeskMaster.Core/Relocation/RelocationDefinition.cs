namespace DeskMaster.Core.Relocation;

/// <summary>
/// A big folder an app can be told to keep somewhere else through an environment variable
/// (for example Ollama models via OLLAMA_MODELS). Loaded from <c>Data/relocations.json</c>.
/// </summary>
public sealed class RelocationDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string EnvVar { get; set; } = "";

    /// <summary>OS key to default folder templates (first existing one wins).</summary>
    public Dictionary<string, List<string>> DefaultPaths { get; set; } = [];

    /// <summary>Folder name created under the chosen destination.</summary>
    public string TargetFolderName { get; set; } = "";

    /// <summary>Processes that must be closed during the move.</summary>
    public List<string>? CloseProcesses { get; set; }
}
