using Microsoft.Playwright;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.RegularExpressions;

[McpServerToolType]
public static class RenderTools
{
    // Fake HTTP origin — Playwright intercepts every request to it and serves
    // files directly from the wwwroot folder on disk. We NAVIGATE to this origin
    // (rather than SetContent) so the page, its scripts, and its fetch/XHR calls
    // are all same-origin — otherwise cross-origin fetches of the geo JSON and
    // /api/mapdata would be blocked by CORS.
    private const string FakeOrigin = "http://aamva-maps-preview";

    // Sentinel path for the generated HTML document itself.
    private const string DocPath = "__preview__.html";

    private static string WwwRoot =>
        Path.Combine(
            Environment.GetEnvironmentVariable("MAPS_REPO_ROOT")
            ?? throw new InvalidOperationException("MAPS_REPO_ROOT not set."),
            "AAMVA.Website.Maps", "AAMVA.Website.Maps", "wwwroot");

    [McpServerTool]
    [Description(
        "Render a Highcharts map config as a PNG image so you can see what the map will look like. " +
        "Pass either the filename without extension as returned by list_maps " +
        "(e.g. 'us-s2s-implementation') or a full absolute path. " +
        "Returns a PNG image.")]
    public static async Task<IEnumerable<ContentBlock>> RenderMapPreview(
        [Description(
            "Filename without extension as returned by list_maps (e.g. 'us-s2s-implementation'), " +
            "or a full absolute path to the JS config file.")]
        string jsConfigPath,

        [Description(
            "Optional JSON array of data points to colour the map. " +
            "Each object: {\"hc_key\":\"us-ca\",\"name\":\"California\",\"value\":1}. " +
            "If omitted the map renders with no data (all states grey).")]
        string? sampleDataJson = null)
    {
        // Resolve filename → absolute path
        jsConfigPath = MapTools.ResolveJsConfigPath(jsConfigPath) ?? jsConfigPath;

        if (!File.Exists(jsConfigPath))
            return [new TextContentBlock { Text = $"File not found: {jsConfigPath}. Use list_maps to see valid filenames." }];

        var jsConfig    = await File.ReadAllTextAsync(jsConfigPath);
        var containerId = Regex.Match(jsConfig, @"mapTagSelector\s*:\s*'([^']+)'") is { Success: true } m
                          ? m.Groups[1].Value
                          : "map-container";
        var legendId    = containerId.Replace("container", "legendcontainer");
        var dataJson    = sampleDataJson ?? "null";

        // Minimal HTML. Scripts load via <script src> from the fake origin so
        // Playwright serves them from disk. We load the REAL jQuery the site
        // ships with (the map configs call $j.getJSON, .html(), .each, .val,
        // .append — a hand-rolled shim cannot cover all of these). mapPrefix is
        // set before geo-helper.js because that file reads it at load time.
        var html = $$"""
<!DOCTYPE html>
<html>
<head>
  <meta charset="utf-8">
  <style>
    body { margin: 0; padding: 20px; background: #fff; }
    #{{containerId}} { min-height: 500px; width: 900px; }
  </style>
</head>
<body>
  <div id="{{containerId}}"></div>
  <div id="{{legendId}}"></div>

  <script src="/Scripts/jquery-3.6.0.min.js"></script>
  <script src="/Scripts/vendor/highmaps.js"></script>
  <script src="/Scripts/vendor/pattern-fill.js"></script>

  <!-- mapPrefix must be defined before geo-helper.js loads -->
  <script>var mapPrefix = '/';</script>

  <script src="/Scripts/shared/color-helper.js"></script>
  <script src="/Scripts/shared/geo-helper.js"></script>
  <script src="/Scripts/shared/external-legend-helper.js"></script>
  <script src="/Scripts/shared/map-helper.js"></script>

  <script>{{jsConfig}}</script>
</body>
</html>
""";

        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = ["--no-sandbox", "--disable-setuid-sandbox"]
            });

            var page = await browser.NewPageAsync(new BrowserNewPageOptions
            {
                ViewportSize = new ViewportSize { Width = 960, Height = 600 }
            });

            // Intercept every request to FakeOrigin: serve the document, mock the
            // data API, or serve a static file from wwwroot (case-insensitive).
            await page.RouteAsync($"{FakeOrigin}/**", async route =>
            {
                var path = new Uri(route.Request.Url).AbsolutePath.TrimStart('/');

                // The generated HTML document itself
                if (path.Equals(DocPath, StringComparison.OrdinalIgnoreCase))
                {
                    await route.FulfillAsync(new RouteFulfillOptions
                    {
                        ContentType = "text/html; charset=utf-8",
                        Body        = html
                    });
                    return;
                }

                // API map data endpoint → return sample data (or null)
                if (path.StartsWith("api/mapdata", StringComparison.OrdinalIgnoreCase))
                {
                    await route.FulfillAsync(new RouteFulfillOptions
                    {
                        ContentType = "application/json",
                        Body        = dataJson
                    });
                    return;
                }

                // Static file → serve from wwwroot (case-insensitive: geo-helper
                // requests 'scripts/...' lowercase but the folder is 'Scripts')
                var filePath = ResolveCaseInsensitive(WwwRoot, path);
                if (filePath is not null)
                {
                    var ct = Path.GetExtension(filePath).ToLower() switch
                    {
                        ".js"   => "application/javascript",
                        ".json" => "application/json",
                        ".css"  => "text/css",
                        _       => "text/plain"
                    };
                    await route.FulfillAsync(new RouteFulfillOptions
                    {
                        ContentType = ct,
                        BodyBytes   = await File.ReadAllBytesAsync(filePath)
                    });
                }
                else
                {
                    await route.FulfillAsync(new RouteFulfillOptions
                    {
                        Status = 404,
                        Body   = $"Not found: {path}"
                    });
                }
            });

            // Collect browser console messages and page errors for diagnostics
            var consoleMessages = new System.Collections.Generic.List<string>();
            page.Console  += (_, e) => consoleMessages.Add($"[{e.Type}] {e.Text}");
            page.PageError += (_, e) => consoleMessages.Add($"[pageerror] {e}");

            // Track 404s from the route handler
            var notFound = new System.Collections.Generic.List<string>();
            page.Response += (_, r) => { if (r.Status == 404) notFound.Add(r.Url); };

            // Navigate to the fake origin so the page and all its fetch/XHR calls
            // are same-origin (no CORS blocking on the geo JSON or /api/mapdata).
            // Use Load (not NetworkIdle) — the map config's async geo-JSON fetch
            // keeps the network active indefinitely from Playwright's perspective,
            // causing a NetworkIdle wait to hit the 30s navigation timeout.
            await page.GotoAsync($"{FakeOrigin}/{DocPath}", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.Load,
                Timeout   = 15_000
            });

            // Wait for Highcharts SVG (generous timeout — geo JSON can be a few MB).
            // On timeout, fall through and return a diagnostic screenshot + console log.
            byte[]? imageData = null;
            string  renderNote;
            try
            {
                await page.WaitForFunctionAsync(
                    $"() => document.querySelector('#{containerId} svg') !== null",
                    null,
                    new PageWaitForFunctionOptions { Timeout = 30_000 });

                await page.WaitForTimeoutAsync(500);

                var element = await page.QuerySelectorAsync($"#{containerId}");
                var raw    = element is not null
                    ? await element.ScreenshotAsync()
                    : await page.ScreenshotAsync(new PageScreenshotOptions { FullPage = true });
                // MCP protocol requires base64 string in "data". The SDK serialises
                // ReadOnlyMemory<byte> as raw characters, not base64, so we encode
                // the base64 string to ASCII bytes — those bytes become the base64
                // string in JSON.
                imageData  = System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(raw));
                renderNote = $"Rendered: {Path.GetFileName(jsConfigPath)}";
            }
            catch (TimeoutException)
            {
                // SVG never appeared — grab a screenshot anyway so we can see the state
                var rawTimeout = await page.ScreenshotAsync(new PageScreenshotOptions { FullPage = true });
                imageData  = System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(rawTimeout));
                renderNote = $"TIMEOUT — SVG not found after 30s. Screenshot shows page state.";
            }

            var diagnostics = new System.Text.StringBuilder();
            if (notFound.Count > 0)
                diagnostics.AppendLine("404s: " + string.Join(", ", notFound));
            if (consoleMessages.Count > 0)
                diagnostics.AppendLine("Console:\n" + string.Join("\n", consoleMessages));

            var result = new System.Collections.Generic.List<ContentBlock>
            {
                new ImageContentBlock { Data = imageData, MimeType = "image/png" },
                new TextContentBlock  { Text = renderNote }
            };
            if (diagnostics.Length > 0)
                result.Add(new TextContentBlock { Text = diagnostics.ToString().Trim() });

            return result;
        }
        catch (Exception ex)
        {
            return [new TextContentBlock { Text = $"Render failed: {ex.Message}" }];
        }
    }

    // Resolve a forward-slash relative URL path to a file on disk, matching each
    // segment case-insensitively. Returns null if no matching file exists.
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
}
