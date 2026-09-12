using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Rhino.AI;

// Live catalog of the models configured in this machine's pi installation, so Pi's model list is
// never maintained by hand in Definitions.json. Pi keeps two sources under its agent dir (the
// PI_CODING_AGENT_DIR env var, default ~/.pi/agent):
//   models-store.json  - provider catalogs pi fetched upstream: { "<provider>": { "models": [...] } }
//   models.json        - user-defined providers that override the store on conflict:
//                        { "providers": { "<provider>": { "models": [...] } } }
// Both are merged into ModelSpecs with fully-qualified ids ("provider/id"), which is exactly what
// `pi --model` accepts. The result is cached and refreshed only when a file's last-write time
// changes, so UI access stays cheap even though models-store.json can be hundreds of KB.
internal static class PiModelCatalog
{
    private static readonly object Gate = new();

    private static IReadOnlyList<ModelSpec>? _cache;
    private static long _storeStamp = -1;
    private static long _userStamp = -1;

    public static IReadOnlyList<ModelSpec> Load()
    {
        lock (Gate)
        {
            string dir = AgentDir();
            string storePath = Path.Combine(dir, "models-store.json");
            string userPath = Path.Combine(dir, "models.json");
            long storeStamp = Mtime(storePath);
            long userStamp = Mtime(userPath);

            if (_cache is not null && _storeStamp == storeStamp && _userStamp == userStamp)
                return _cache;

            List<ModelSpec> models = [];
            Dictionary<string, int> index = new(); // qualified id -> position in models
            Merge(storePath, index, models);
            Merge(userPath, index, models); // user config wins on conflict
            _cache = models;
            _storeStamp = storeStamp;
            _userStamp = userStamp;
            return _cache;
        }
    }

    private static string AgentDir()
    {
        string? dir = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");
        return dir is { Length: > 0 } ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pi", "agent");
    }

    // -1 when the file does not exist, so a missing file is a stable stamp, not an error.
    private static long Mtime(string path)
    {
        try { return new FileInfo(path).LastWriteTimeUtc.Ticks; }
        catch (Exception) { return -1; }
    }

    private static void Merge(string path, Dictionary<string, int> index, List<ModelSpec> models)
    {
        if (Mtime(path) < 0)
            return;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (Exception)
        {
            return; // unreadable or half-written file: skip it, keep whatever we have
        }

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return;

        // models.json wraps providers in a top-level "providers" object; models-store.json does not.
        JsonElement providers = doc.RootElement.TryGetProperty("providers", out JsonElement p) && p.ValueKind == JsonValueKind.Object
            ? p
            : doc.RootElement;

        foreach (JsonProperty providerProp in providers.EnumerateObject())
        {
            if (providerProp.Value.ValueKind != JsonValueKind.Object ||
                !providerProp.Value.TryGetProperty("models", out JsonElement modelsEl) ||
                modelsEl.ValueKind != JsonValueKind.Array)
                continue;

            foreach (JsonElement modelEl in modelsEl.EnumerateArray())
            {
                if (modelEl.ValueKind != JsonValueKind.Object ||
                    !TryStr(modelEl, "id", out string id) ||
                    id.Length == 0)
                    continue;

                string qualified = $"{providerProp.Name}/{id}";
                ModelSpec spec = new(TryStr(modelEl, "name", out string name) ? name : id, qualified);
                if (index.TryGetValue(qualified, out int i))
                    models[i] = spec; // override (user config replacing the store entry)
                else
                {
                    index[qualified] = models.Count;
                    models.Add(spec);
                }
            }
        }
    }

    private static bool TryStr(JsonElement obj, string name, out string value)
    {
        if (obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } s)
        {
            value = s;
            return true;
        }
        value = string.Empty;
        return false;
    }
}
