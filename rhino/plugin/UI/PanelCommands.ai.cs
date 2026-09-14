using System.Text.Json.Serialization;

namespace Rhino.AI.UI;

[JsonDerivedType(typeof(PromptCommand), "prompt")]
internal abstract partial record PanelCommand { }

internal sealed record PromptCommand(PromptRequest Request) : PanelCommand;

internal sealed record PromptRequest(string Text, IReadOnlyList<PanelAttachment> Attachments);
