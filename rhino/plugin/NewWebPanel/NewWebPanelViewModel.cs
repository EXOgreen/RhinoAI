using System.IO;
using System.Net;

using Eto.Forms;

using Rhino.AI.WebPanel;

namespace Rhino.AI.UI;

internal class NewWebPanelViewModel
{

    private HttpListener Listener { get; }

    public NewPanelBridge Bridge { get; }
    private uint DocumentSerialNumber { get; }
    private RhinoDoc? Document => RhinoDoc.FromRuntimeSerialNumber(DocumentSerialNumber);

    public NewWebPanelViewModel(WebView view, uint documentSerialNumber)
    {
        System.Net.Sockets.TcpListener tcp = new(IPAddress.Loopback, 0);
        tcp.Start();
        int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();

        Listener = new();
        Listener.Prefixes.Add($"http://127.0.0.1:{port}/");

        Bridge = new NewPanelBridge(view, IncomingCommand);
        DocumentSerialNumber = documentSerialNumber;
    }

#region FROM AI AGENT

    private void IncomingCommand(PanelCommand? command)
    {
        _ = command switch
        {
            // TODO : Events go here
            ReadyCommand => Ready(),
            CancelCommand => CancelCurrentAgentTurn(),
            
            _ => false
        };
    }

    private bool Ready() => false; // TODO : 

    private bool CancelCurrentAgentTurn()
    {
        if (!AgentHost.TryFor(Document, out IAgentRunner running)) return false;
        running.Cancel();
        return true;
    }

#endregion

}
