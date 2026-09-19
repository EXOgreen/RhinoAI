using System.Text.Json.Serialization;

namespace Rhino.AI;

/// <summary>A definition of an AI Agent</summary>

internal sealed record AgentDefinition
{
    public string Name { get; set; } = string.Empty;
    public SearchPaths SearchPaths { get; set; } = new();

    // When "pi", Models is resolved live from the local pi config (see PiModelCatalog) instead of
    // being maintained in Definitions.json. Any other value (or null) means the models array in
    // Definitions.json is authoritative.
    public string? ModelSource { get; set; }

    public string DefaultModel { get; set; } = "default";
    public string DefaultPrompt { get; set; } = "";
    public bool Enabled { get; set; } = true;

    [JsonIgnore] private IReadOnlyList<ModelSpec>? _models;

    public IReadOnlyList<ModelSpec> Models
    {
        get => _models ??= ResolveModels();
        set => _models = value ?? [];
    }

    private IReadOnlyList<ModelSpec> ResolveModels() =>
        string.Equals(ModelSource, "pi", StringComparison.OrdinalIgnoreCase)
            // The exe path lets the catalog ask pi itself which models are actually available;
            // null (CLI not found) degrades to the full on-disk catalog.
            ? PiModelCatalog.Load(CliProcess.TryResolve(SearchPaths.GetPaths(), out string exe) ? exe : null)
            : [];

    public bool Available => SearchPaths.GetPaths().Any();

    public bool? LoggedIn { get; private set; } = null;

    public void EnsureLoggedIn()
    {
        if (LoggedIn ?? false) return;

        // TODO : Log-in
        LoggedIn = true;
    }

    public IAgentRunner GetRunner(string docTitle) => Name.ToLowerInvariant() switch
    {
        "claude" => new AgentRunner(this, docTitle, (client, convo, cwd) => new StreamJsonAgent(this, client, convo, cwd, new ClaudeStreamJsonParser(this))),
        "codex" => new AgentRunner(this, docTitle, (client, convo, cwd) => new StreamJsonAgent(this, client, convo, cwd, new CodexStreamJsonParser(this, CodexHome.Prepare()))),
        "gemini" => new AgentRunner(this, docTitle, (client, _, cwd) => GeminiConnection.Connect(this, client, cwd)),
        "pi" => new AgentRunner(this, docTitle, (client, convo, cwd) => new StreamJsonAgent(this, client, convo, cwd, new PiStreamJsonParser(this))),

        // TODO : Use a better result
        _ => throw new NotImplementedException($"{Name} is not configured")
    };

}

internal sealed record SearchPaths()
{
    [JsonInclude]
    private List<string> Win { get; set; } = [];

    [JsonInclude]
    private List<string> Mac { get; set; } = [];

    private static string USER_PROFILE => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string APPDATA => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string LOCAL_APPDATA => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private List<string>? Paths { get; set; }
    public IReadOnlyList<string> GetPaths()
    {
        if (Paths is null)
        {
            List<string> paths = [];
            if (OperatingSystem.IsWindows())
            {
                paths = Win;
            }
            else if (OperatingSystem.IsMacOS())
            {
                paths = Mac;
            }

            // Path Replacements
            for (int i = 0; i < paths.Count; i++)
            {
                paths[i] = paths[i]
                    .Replace("%LOCALAPPDATA%", LOCAL_APPDATA)
                    .Replace("%APPDATA%", APPDATA)
                    .Replace("%USERPROFILE%", USER_PROFILE);
            }

            Paths = paths.SelectMany(p => new Paths.Glob(p, false).TruePaths).ToList();
        }

        return Paths;
    }

}

internal sealed record ModelSpec(string Display, string Id);
