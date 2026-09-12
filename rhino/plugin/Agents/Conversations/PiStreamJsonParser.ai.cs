using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Acp;
using ContentBlock = Acp.ContentBlock; // disambiguate from Rhino.AI.Server.ContentBlock

namespace Rhino.AI;

// The Pi stream-json strategy: turns one `pi --mode json` stdout line into ACP session/update
// events and frames one user turn for stdin. Owns no process/threads/turn gating (the runner does);
// stays RhinoApp-free so it can be Compile Include'd into a test project. Model and the system
// prompt come from AISettings at spawn time; the MCP server set is resolved by the runner and
// handed in per spawn.
//
// Pi's print mode (`-p`) is one-shot: it merges piped stdin into the initial prompt, runs the
// agent to completion, streams JSON events, and exits. Multi-turn continuity therefore comes from
// pi's session files, not a long-lived process, so IsOneTurnPerProcess is true and the runner
// closes stdin after each turn. Pi ignores --session-id in print mode and mints its own UUIDv7,
// reporting it in the session event; the runner adopts that id (AdoptMintedSessionId) and every
// later spawn passes --session <adopted id> to load the file from disk. The terminal event is
// agent_end (turn_end fires once per LLM turn, i.e. many times across a tool loop); it carries
// willRetry while a compaction retry is pending, so only willRetry:false completes the turn.
// Usage is summed over this run's assistant messages in agent_end.messages.
//
// Pi has no CLI sign-in flow: credentials are per-provider config (env vars, auth.json, or an
// apiKey in models.json), and `pi auth` only knows print-api-key / print-bearer-token / check.
// AuthStatusArguments and LoginArguments are therefore empty - the runner stays permanently
// Unknown and off the sign-in path, and a missing credential surfaces as pi's own top-level
// {"error":...} line, which Parse turns into a failed turn with the real message in the transcript.
internal sealed class PiStreamJsonParser : IStreamJsonParser
{
    private AgentDefinition Definition { get; }
    private string? McpConfigPath { get; set; }

    public PiStreamJsonParser(AgentDefinition definition)
    {
        Definition = definition;
    }

    public string DisplayName => Definition.Name;

    public string NotFoundMessage => "Pi CLI not found. Install pi (https://github.com/earendil-works/pi-coding-agent).";

    // One `pi -p` process per turn; the session file on disk carries continuity between them.
    public bool IsOneTurnPerProcess => true;

    // Pi cannot be asked for a single sign-in state (auth is per-provider, and there is no CLI
    // command that performs one), so leave it permanently Unknown and off the sign-in path.
    public IReadOnlyList<string> AuthStatusArguments => [];

    public IReadOnlyList<string> LoginArguments => [];

    public CliLogin.State ReadAuthState(string output, int exitCode) => CliLogin.State.Unknown;

    public void ConfigureArguments(ProcessStartInfo psi, string mcpUrl, string agentSessionId, IReadOnlyList<string> mcpServers, bool resume)
    {
        BypassCmdShim(psi);

        // Pi takes --mcp-config as a FILE PATH (unlike Claude's inline JSON), so the server set is
        // written to a temp file. The previous spawn's file is replaced, not leaked.
        if (McpConfigPath is not null)
        {
            try { File.Delete(McpConfigPath); } catch { /* best effort */ }
        }

        // Pi's MCP adapter picks the transport from which fields are present: "url" means
        // StreamableHTTP (SSE fallback), "command" means stdio. There is no "type" field - that is
        // Claude Code's shape - so it is stripped from the runner-resolved entries, which may be
        // authored for Claude.
        JsonObject servers = new()
        {
            ["rhino"] = new JsonObject { ["url"] = mcpUrl },
        };
        foreach (string entry in mcpServers)
            if (JsonNode.Parse(entry) is JsonObject obj)
                foreach (KeyValuePair<string, JsonNode?> kvp in obj)
                    if (kvp.Key != "rhino" && kvp.Value is JsonObject server)
                    {
                        JsonObject copy = new();
                        foreach (KeyValuePair<string, JsonNode?> field in server)
                            if (field.Key != "type")
                                copy[field.Key] = field.Value?.DeepClone();
                        servers[kvp.Key] = copy;
                    }

        McpConfigPath = Path.Combine(Path.GetTempPath(), $"rhino-pi-mcp-{Guid.NewGuid():N}.json");
        File.WriteAllText(McpConfigPath, new JsonObject { ["mcpServers"] = servers }.ToJsonString(McpSerializer.Options));

        psi.ArgumentList.Add("-p"); // print mode: process the prompt and exit (one turn per process)
        psi.ArgumentList.Add("--mode");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("--mcp-config");
        psi.ArgumentList.Add(McpConfigPath);

        string systemPrompt = AgentPrompts.Compose(AISettings.EffectivePrompt(Definition));
        if (systemPrompt.Length > 0)
        {
            psi.ArgumentList.Add("--append-system-prompt");
            psi.ArgumentList.Add(systemPrompt);
        }

        // Empty means "use pi's own configured default" (its settings.json defaultProvider/
        // defaultModel), so no flag at all - not an empty --model.
        if (AISettings.EffectiveModel(Definition) is { Length: > 0 } model)
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(model);
        }

        // Fresh: ask pi to create the session under our id (it may mint its own instead - we adopt
        // whatever the session event reports). Respawn after cancel/crash: load it from disk.
        // (NOT --resume/-r: that flag takes no value and opens the interactive session picker.)
        if (resume)
            psi.ArgumentList.Add("--session");
        else
            psi.ArgumentList.Add("--session-id");
        psi.ArgumentList.Add(agentSessionId);
    }

    // On Windows CliProcess launches an npm .cmd shim through `cmd.exe /c`, and cmd re-parses the
    // command line: a multi-line quoted argument (our steered system prompt) breaks the parse and
    // drops everything after it - including --session, which silently starts a fresh session. The
    // shim is nothing but a node launcher for the bundled CLI, so when its layout resolves, point
    // straight at node.exe + cli.js: a real .exe receives CreateProcess's argument list verbatim.
    private static void BypassCmdShim(ProcessStartInfo psi)
    {
        if (!OperatingSystem.IsWindows()) return;
        // CliProcess.ConfigureFileName's batch-shim shape: FileName=cmd.exe, args=[/c, <shim>, ...].
        if (psi.ArgumentList.Count < 2 || !string.Equals(psi.ArgumentList[0], "/c", StringComparison.OrdinalIgnoreCase))
            return;
        string shim = psi.ArgumentList[1];
        if (!shim.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) && !shim.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            return;

        string dir = Path.GetDirectoryName(shim) ?? string.Empty;
        string cliJs = Path.Combine(dir, "node_modules", "@earendil-works", "pi-coding-agent", "dist", "bundle", "cli.js");
        if (!File.Exists(cliJs))
            return; // unknown layout: keep the cmd launch (single-line args survive it)

        string node = Path.Combine(dir, "node.exe");
        List<string> args = [cliJs];
        foreach (string arg in psi.ArgumentList.Skip(2))
            args.Add(arg);
        psi.ArgumentList.Clear();
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);
        psi.FileName = File.Exists(node) ? node : "node";
    }

    // The turn is written to stdin as plain text; pi -p merges piped stdin into the initial prompt
    // and the runner closes stdin (EOF) right after, which is what starts processing.
    public string FormatTurn(IReadOnlyList<ContentBlock> prompt)
    {
        List<string> pieces = [];
        foreach (ContentBlock block in prompt)
            if (block is TextContentBlock text && text.Text.Length > 0)
                pieces.Add(text.Text);
        return string.Join("\n", pieces);
    }

    public ParsedLine Parse(string line)
    {
        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return ParsedLine.None;

        // Pi's top-level failure shape: {"error":{"message":"...","code":404,...}} - a turn that
        // dies before it can stream (bad model id, missing credentials, HTTP error).
        if (root.TryGetProperty("error", out JsonElement error))
            return ParsedLine.Failed(StopReason.Refusal, Chunk($"Pi failed: {ErrorMessage(error)}"));

        if (!TryStr(root, "type", out string type))
            return ParsedLine.None;

        switch (type)
        {
            case "session":
                // Pi echoes the session id it adopted (ours, since we mint it). Lets the runner
                // adopt a different one if pi ever mints its own.
                return TryStr(root, "id", out string sessionId) ? ParsedLine.Session(sessionId) : ParsedLine.None;

            case "message_update":
                // Delta-only events: text_delta carries live answer text. Thinking and tool-call
                // deltas are ignored - ACP has no thinking content block, matching how the Claude
                // and Codex parsers surface only answer text.
                if (root.TryGetProperty("assistantMessageEvent", out JsonElement evt) &&
                    TryStr(evt, "type", out string evtType) &&
                    evtType == "text_delta" &&
                    TryStr(evt, "delta", out string delta))
                    return ParsedLine.Emit(Chunk(delta));
                return ParsedLine.None;

            case "agent_end":
                // The run's true terminal event (turn_end fires per LLM turn inside the tool loop).
                // willRetry:true means a compaction overflow retry is about to continue the run.
                if (root.TryGetProperty("willRetry", out JsonElement retry) && retry.ValueKind == JsonValueKind.True)
                    return ParsedLine.None;
                return FinishRun(root);

            default:
                return ParsedLine.None;
        }
    }

    // Sum this run's usage over its assistant messages and resolve the turn. A final message that
    // stopped on error/abort fails the turn with whatever text pi recorded, so the transcript
    // shows why instead of just stopping.
    private static ParsedLine FinishRun(JsonElement root)
    {
        int input = 0, output = 0;
        decimal cost = 0m;
        bool haveCost = false;
        string? stopReason = null;
        string? errorMessage = null;

        if (root.TryGetProperty("messages", out JsonElement messages) && messages.ValueKind == JsonValueKind.Array)
            foreach (JsonElement message in messages.EnumerateArray())
                if (message.ValueKind == JsonValueKind.Object && TryStr(message, "role", out string role) && role == "assistant")
                {
                    input += ReadInt(message, "usage", "input");
                    output += ReadInt(message, "usage", "output");
                    if (ReadCost(message, out decimal c))
                    {
                        cost += c;
                        haveCost = true;
                    }
                    stopReason = TryStr(message, "stopReason", out string sr) ? sr : stopReason;
                    errorMessage = TryStr(message, "errorMessage", out string em) ? em : errorMessage;
                }

        if (stopReason is "error" or "abort")
            return ParsedLine.Failed(StopReason.Refusal, Chunk($"Pi {stopReason}: {errorMessage ?? "no error message recorded"}"));

        return ParsedLine.Complete(StopReason.EndTurn, haveCost ? new TokenUsage(input, output, cost) : new TokenUsage(input, output));
    }

    private static string ErrorMessage(JsonElement error) =>
        error.ValueKind == JsonValueKind.String && error.GetString() is { Length: > 0 } s ? s
        : error.ValueKind == JsonValueKind.Object && TryStr(error, "message", out string m) ? m
        : "unknown error";

    private static AgentMessageChunkSessionUpdate Chunk(string text) => new()
    {
        Content = new TextContentBlock { Text = text },
    };

    // Two-level read: obj[name][inner].
    private static int ReadInt(JsonElement obj, string name, string inner) =>
        obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Object ? ReadInt(el, inner) : 0;

    private static int ReadInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int v) ? v : 0;

    private static bool ReadCost(JsonElement message, out decimal cost)
    {
        if (message.TryGetProperty("usage", out JsonElement usage) &&
            usage.TryGetProperty("cost", out JsonElement costEl) &&
            costEl.ValueKind == JsonValueKind.Object &&
            costEl.TryGetProperty("total", out JsonElement total) &&
            total.ValueKind == JsonValueKind.Number &&
            total.TryGetDecimal(out decimal d))
        {
            cost = d;
            return true;
        }
        cost = 0m;
        return false;
    }

    // JSON-boundary string read.
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
