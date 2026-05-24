using Microsoft.Extensions.AI;
using Microsoft.Playwright;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.RegularExpressions;

[McpServerToolType]
public static class RenderTools
{
    // Fake HTTP origin — Playwright intercepts all requests to it and serves
    // files directly from the wwwroot folder on disk.
    private const string FakeOrigin = "http://aamva-maps-preview";

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
    public static async Task<IEnumerable<AIContent>> RenderMapPreview(
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
            return [new TextContent($"File not found: {jsConfigPath}. Use list_maps to see valid filenames.")];

        var jsConfig    = await File.ReadAllTextAsync(jsConfigPath);
        var containerId = Regex.Match(jsConfig, @"mapTagSelector\s*:\s*'([^']+)'") is { Success: true } m
                          ? m.Groups[1].Value
                          : "map-container";
        var legendId    = containerId.Replace("container", "legendcontainer");
        var dataJson    = sampleDataJson ?? "null";

        // Minimal HTML — scripts are loaded via <script src> so Playwright
        // intercepts them and serves from disk (no inlining of large files).
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

  <script src="{{FakeOrigin}}/Scripts/vendor/highmaps.js"></script>
  <script src="{{FakeOrigin}}/Scripts/vendor/pattern-fill.js"></script>

  <!-- Minimal jQuery shim (Highcharts doesn't need jQuery; map configs do) -->
  <script>
    (function () {
      function jq(sel) {
        if (typeof sel === 'function') { document.addEventListener('DOMContentLoaded', sel); return; }
        return document.querySelectorAll(sel);
      }
      jq.noConflict = function () { return jq; };
      jq.getJSON = function (url, cb) {
        fetch(url)
          .then(function (r) { return r.json(); })
          .then(cb)
          .catch(function () { cb(null); });
        return { done: function () {}, fail: function () {} };
      };
      window.jQuery = window.$ = jq;
    })();
  </script>

  <!-- mapPrefix points to our fake origin so all asset URLs are interceptable -->
  <script>var mapPrefix = '{{FakeOrigin}}/';</script>

  <script src="{{FakeOrigin}}/Scripts/shared/color-helper.js"></script>
  <script src="{{FakeOrigin}}/Scripts/shared/geo-helper.js"></script>
  <script src="{{FakeOrigin}}/Scripts/shared/external-legend-helper.js"></script>
  <script src="{{FakeOrigin}}/Scripts/shared/map-helper.js"></script>

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

            // Intercept all requests to FakeOrigin and serve from wwwroot or return mock data
            await page.RouteAsync($"{FakeOrigin}/**", async route =>
            {
                var path = new Uri(route.Request.Url).AbsolutePath.TrimStart('/');

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

                // Static file → serve from wwwroot
                var filePath = Path.Combine(WwwRoot,
                    path.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(filePath))
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
                        Body   = $"Not found: {filePath}"
                    });
                }
            });

            // Inject HTML directly — no network request for the page itself
            await page.SetContentAsync(html, new PageSetContentOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle
            });

            // Wait for Highcharts SVG (generous timeout — geo JSON can be a few MB)
            await page.WaitForFunctionAsync(
                "() => document.querySelector('svg') !== null",
                null,
                new PageWaitForFunctionOptions { Timeout = 30_000 });

            await page.WaitForTimeoutAsync(500);

            var element   = await page.QuerySelectorAsync($"#{containerId}");
            var imageData = element is not null
                ? await element.ScreenshotAsync()
                : await page.ScreenshotAsync(new PageScreenshotOptions { FullPage = true });

            return
            [
                new DataContent(imageData, "image/png"),
                new TextContent($"Rendered: {Path.GetFileName(jsConfigPath)}")
            ];
        }
        catch (Exception ex)
        {
            return [new TextContent($"Render failed: {ex.Message}")];
        }
    }
}
