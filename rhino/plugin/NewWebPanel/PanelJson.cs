using System.Text.Json.Serialization;

namespace Rhino.AI.UI;

internal static class NewPanelJson
{
    private static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static string Serialize(WebPanel.PanelEvent value) => JsonSerializer.Serialize(value, Options);

    public static PanelCommand? Deserialize(string json) =>
        JsonSerializer.Deserialize<PanelCommand>(json, Options);
}
