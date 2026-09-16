using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Rhino.AI.Server;

namespace Rhino.AI;

internal sealed class McpServer : IDisposable
{
    private const string ExternalRoute = "/";
    private const string AgentRoute = "/agent";

    private HttpListener? ExternalListener { get; set; }
    private HttpListener? AgentListener { get; set; }
    private CancellationTokenSource Cts { get; } = new CancellationTokenSource();

    public bool HasStarted => ExternalListener is not null;

    public int Port { get; private set; }

    public DateTime StartTime { get; private set; } = DateTime.UtcNow;

    public bool Start(RhinoDoc doc, int port)
    {
        if (HasStarted)
            return true;
        Port = port;
        try
        {
            DocumentServices services = new(doc);

            ExternalListener = Listen($"http://localhost:{port}/");
            AgentListener = Listen($"http://localhost:{port}/agent/");

            _ = AcceptAsync(ExternalListener, new McpDispatcher(services, filtered: false), ExternalRoute);
            _ = AcceptAsync(AgentListener, new McpDispatcher(services, filtered: true), AgentRoute);

            StartTime = DateTime.UtcNow;

            RhinoApp.WriteLine($"[RhinoAI] MCP server currently running on http://localhost:{port}/ (in-Rhino agents use /agent)");
            return true;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[RhinoAI] Failed to start: {DescribeException(ex)}");
            Stop();
            return false;
        }
    }

    // localhost, not 127.0.0.1: Windows HTTP.SYS grants that host to unelevated processes without a URL reservation.
    private static HttpListener Listen(string prefix)
    {
        HttpListener listener = new();
        listener.Prefixes.Add(prefix);
        listener.Start();
        return listener;
    }

    private async Task AcceptAsync(HttpListener listener, McpDispatcher dispatcher, string route)
    {
        while (listener.IsListening && !Cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            { return; }

            _ = Task.Run(() => ServeAsync(ctx, dispatcher, route));
        }
    }

    private async Task ServeAsync(HttpListenerContext ctx, McpDispatcher dispatcher, string route)
    {
        try
        {
            if (!string.Equals(NormalizePath(ctx.Request.Url), route, StringComparison.Ordinal))
                ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
            else if (!string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                ctx.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
            else
                await dispatcher.HandleAsync(ctx, Cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
        { }
        finally
        {
            try
            { ctx.Response.Close(); }
            catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
            { }
        }
    }

    private static string NormalizePath(Uri? url)
    {
        string path = url?.AbsolutePath ?? ExternalRoute;
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    private static string DescribeException(Exception ex)
    {
        List<string> parts = [];
        for (Exception? cur = ex; cur is not null; cur = cur.InnerException)
            parts.Add($"{cur.GetType().FullName}: {cur.Message}");
        return string.Join(" --> ", parts);
    }

    public void Stop()
    {
        try
        { Cts.Cancel(); }
        catch { }

        CloseListener(ExternalListener);
        CloseListener(AgentListener);
        ExternalListener = null;
        AgentListener = null;
    }

    private static void CloseListener(HttpListener? listener)
    {
        try
        { listener?.Close(); }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
        { }
    }

    public void Dispose() => Stop();
}
