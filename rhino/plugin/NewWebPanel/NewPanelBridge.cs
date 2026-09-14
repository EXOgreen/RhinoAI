using System.Threading.Tasks;
using Eto.Forms;

namespace Rhino.AI.UI;

internal class NewPanelBridge
{

    public string Id { get; } = Guid.NewGuid().ToString();

    private WebView View { get; }

    private Action<PanelCommand?> CommandReceiver { get; }

    public NewPanelBridge(WebView view, Action<PanelCommand?> commandReceiver)
    {
        View = view;
        CommandReceiver = commandReceiver;
        View.MessageReceived += HandleReceived;
        View.DocumentLoaded += HandleBackLog;
    }

    private void HandleReceived(object? _, WebViewMessageEventArgs e)
    {
        try
        {
            PanelCommand? command = NewPanelJson.Deserialize(e.Message);
            CommandReceiver?.Invoke(command);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            RhinoApp.WriteLine($"[rhino-ai] panel sent a command this build does not handle: {ex.Message}");
            return;
        }
    }

    private void HandleBackLog(object? _, WebViewLoadedEventArgs e)
    {
        while (Backlog.TryDequeue(out WebPanel.PanelEvent? @event))
        {
            if (@event is null) continue;
            Post(@event);
        }
    }

    private Queue<WebPanel.PanelEvent> Backlog { get; } = new();
    public void Post(WebPanel.PanelEvent value)
    {
        if (!View.Loaded)
        {
            Backlog.Enqueue(value);
            return;
        }

        string script = $"window.rhinoAI && window.rhinoAI.receive({NewPanelJson.Serialize(value)});";
        Task task = View.ExecuteScriptAsync(script);
        task.ContinueWith(
            static t => RhinoApp.WriteLine($"[rhino-ai] panel script failed: {t.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

}
