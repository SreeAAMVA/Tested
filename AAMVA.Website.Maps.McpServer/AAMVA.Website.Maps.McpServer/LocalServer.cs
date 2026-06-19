using System.Net;
using System.Net.Sockets;

// Lightweight static-file HTTP server that runs inside the MCP server process.
// The HTML returned by render_map_preview references scripts via
// http://localhost:{Port}/Scripts/... so the rendered page can load Highcharts,
// jQuery, and the geo JSON without inlining megabytes into the HTML.
public sealed class LocalServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string       _wwwRoot;
    private volatile bool         _disposed;

    public int Port { get; }

    public static LocalServer? Instance { get; private set; }

    public static void Initialize(string wwwRoot)
    {
        Instance = new LocalServer(wwwRoot);
    }

    private LocalServer(string wwwRoot)
    {
        _wwwRoot = wwwRoot;
        Port     = GetFreePort();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    private async Task ServeAsync()
    {
        while (!_disposed)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleAsync(ctx));
            }
            catch when (_disposed) { break; }
            catch { /* keep serving */ }
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url!.AbsolutePath.TrimStart('/');

            ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");

            // Mock map-data API — returns whatever the caller stored, or null
            if (path.StartsWith("api/mapdata", StringComparison.OrdinalIgnoreCase))
            {
                var key  = ctx.Request.Url.AbsolutePath;
                var data = _dataStore.TryGetValue(key, out var v) ? v : "null";
                ctx.Response.ContentType = "application/json";
                var bytes = System.Text.Encoding.UTF8.GetBytes(data);
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                return;
            }

            // Static file from wwwroot (case-insensitive segment matching)
            var filePath = ResolveCaseInsensitive(_wwwRoot, path);
            if (filePath is not null)
            {
                ctx.Response.ContentType = GetContentType(filePath);
                var bytes = await File.ReadAllBytesAsync(filePath);
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
            }
            else
            {
                ctx.Response.StatusCode = 404;
            }
        }
        catch { ctx.Response.StatusCode = 500; }
        finally { ctx.Response.Close(); }
    }

    // Store sample data keyed by the API path so render_map_preview can inject it
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _dataStore = new();

    public void SetDataForPath(string apiPath, string jsonData) =>
        _dataStore[apiPath] = jsonData;

    public void ClearData(string apiPath) =>
        _dataStore.TryRemove(apiPath, out _);

    private static string? ResolveCaseInsensitive(string root, string relativeUrlPath)
    {
        var direct = Path.Combine(root, relativeUrlPath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(direct)) return direct;

        var current = root;
        foreach (var seg in relativeUrlPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Directory.Exists(current)) return null;
            var match = Directory.EnumerateFileSystemEntries(current)
                .FirstOrDefault(e => string.Equals(Path.GetFileName(e), seg, StringComparison.OrdinalIgnoreCase));
            if (match is null) return null;
            current = match;
        }
        return File.Exists(current) ? current : null;
    }

    private static string GetContentType(string path) =>
        Path.GetExtension(path).ToLower() switch
        {
            ".js"   => "application/javascript",
            ".json" => "application/json",
            ".css"  => "text/css",
            ".html" => "text/html",
            _       => "text/plain"
        };

    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose()
    {
        _disposed = true;
        _listener.Stop();
    }
}
