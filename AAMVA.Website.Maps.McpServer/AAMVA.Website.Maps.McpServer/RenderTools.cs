using Microsoft.Extensions.AI;
using Microsoft.Playwright;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.RegularExpressions;

[McpServerToolType]
public static class RenderTools
{
    private static string WwwRoot =>
        Path.Combine(
            Environment.GetEnvironmentVariable("MAPS_REPO_ROOT")
            ?? throw new InvalidOperationException("MAPS_REPO_ROOT not set."),
            "AAMVA.Website.Maps", "AAMVA.Website.Maps", "wwwroot");

    [McpServerTool]
    [Description(
        "Render a Highcharts map config as a PNG image so you can see what the map will look like. " +
        "Pass either the filename without extension (exactly as returned by list_maps, e.g. 'us-s2s-implementation') " +
        "or a full absolute path. Returns a PNG image.")]
    public static async Task<IEnumerable<AIContent>> RenderMapPreview(
        [Description(
            "Filename without extension as returned by list_maps (e.g. 'us-s2s-implementation'), " +
            "or a full absolute path to the JS config file.")]
        string jsConfigPath,

        [Description(
            "Optional JSON array of data points to show on the map. " +
            "Each object: {\"hc_key\":\"us-ca\",\"name\":\"California\",\"value\":1}. " +
            "If omitted the map renders with no data (all states grey).")]
        string? sampleDataJson = null)
    {
        // Accept filename (from list_maps) or absolute path
        jsConfigPath = MapTools.ResolveJsConfigPath(jsConfigPath)
                       ?? jsConfigPath; // keep original so error message is useful

        if (!File.Exists(jsConfigPath))
            return [new TextContent($"File not found: {jsConfigPath}. Use list_maps to see valid filenames.")];

        var jsConfig     = File.ReadAllText(jsConfigPath);
        var scriptsDir   = Path.Combine(WwwRoot, "Scripts");
        var vendorDir    = Path.Combine(scriptsDir, "vendor");
        var sharedDir    = Path.Combine(scriptsDir, "shared");

        string ReadScript(string p) =>
            File.Exists(p) ? File.ReadAllText(p) : $"/* missing: {p} */";

        var highmapsJs     = ReadScript(Path.Combine(vendorDir, "highmaps.js"));
        var patternFillJs  = ReadScript(Path.Combine(vendorDir, "pattern-fill.js"));
        var colorHelperJs  = ReadScript(Path.Combine(sharedDir, "color-helper.js"));
        var geoHelperJs    = ReadScript(Path.Combine(sharedDir, "geo-helper.js"));
        var legendHelperJs = ReadScript(Path.Combine(sharedDir, "external-legend-helper.js"));
        var mapHelperJs    = ReadScript(Path.Combine(sharedDir, "map-helper.js"));
        var geoJson        = ResolveGeoJson(jsConfig);

        var dataScript = sampleDataJson is not null
            ? $"window.__PREVIEW_DATA__ = {sampleDataJson};"
            : "window.__PREVIEW_DATA__ = null;";

        var containerIdMatch = Regex.Match(jsConfig, @"mapTagSelector\s*:\s*'([^']+)'");
        var containerId      = containerIdMatch.Success ? containerIdMatch.Groups[1].Value : "map-container";
        var legendId         = containerId.Replace("container", "legendcontainer");

        // $$""" means C# interpolation uses {{expr}}, so { and } are literal JS braces
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

  <script>{{highmapsJs}}</script>
  <script>{{patternFillJs}}</script>

  <!-- Minimal jQuery shim (enough for $.getJSON and $.noConflict) -->
  <script>
    (function() {
      if (typeof window.jQuery !== 'undefined') return;
      function jq(sel) {
        if (typeof sel === 'function') { document.addEventListener('DOMContentLoaded', sel); return; }
        return document.querySelectorAll(sel);
      }
      jq.noConflict = function() { return jq; };
      jq.getJSON = function(url, cb) {
        fetch(url).then(function(r) { return r.json(); }).then(cb);
        return { done: function() {}, fail: function() {} };
      };
      window.jQuery = window.$ = jq;
    })();
  </script>

  <!-- Inject preview data -->
  <script>{{dataScript}}</script>

  <!-- Intercept $.getJSON so map data comes from __PREVIEW_DATA__ -->
  <script>
    (function() {
      var _orig = jQuery.getJSON;
      jQuery.getJSON = function(url, callback) {
        if (url && url.indexOf('api/mapdata') !== -1) {
          setTimeout(function() { callback(window.__PREVIEW_DATA__); }, 0);
          return { done: function() {}, fail: function() {} };
        }
        return _orig.apply(this, arguments);
      };
    })();
  </script>

  <!-- Inline geo JSON so fetch() calls resolve without a server -->
  <script>window.__GEO_DATA__ = {{geoJson}};</script>
  <script>
    (function() {
      var _origFetch = window.fetch;
      window.fetch = function(url) {
        if (url && (url.indexOf('maps-geo') !== -1 || url.indexOf('.json') !== -1)) {
          return Promise.resolve({
            json: function() { return Promise.resolve(window.__GEO_DATA__); }
          });
        }
        return _origFetch.apply(this, arguments);
      };
    })();
  </script>

  <!-- Shared helpers -->
  <script>{{colorHelperJs}}</script>
  <script>var mapPrefix = '/';</script>
  <script>{{geoHelperJs}}</script>
  <script>{{legendHelperJs}}</script>
  <script>{{mapHelperJs}}</script>

  <!-- Map-specific config -->
  <script>{{jsConfig}}</script>
</body>
</html>
""";

        var tmpHtml = Path.Combine(Path.GetTempPath(), $"aamva-preview-{Guid.NewGuid():N}.html");
        try
        {
            await File.WriteAllTextAsync(tmpHtml, html);

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

            await page.GotoAsync("file://" + tmpHtml,
                new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

            // Wait for Highcharts SVG to appear
            await page.WaitForFunctionAsync(
                "() => document.querySelector('svg') !== null",
                null,
                new PageWaitForFunctionOptions { Timeout = 10_000 });

            await page.WaitForTimeoutAsync(600);

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
            return [new TextContent($"Render failed: {ex.Message}\n{ex.StackTrace}")];
        }
        finally
        {
            if (File.Exists(tmpHtml)) File.Delete(tmpHtml);
        }
    }

    private static string ResolveGeoJson(string jsConfig)
    {
        var geoFile = jsConfig.Contains("geoUnitedStatesWithCanadaFilename")
            ? "us-territories-canada-geo.json"
            : jsConfig.Contains("geoUnitedStatesWithTerritoriesFilename")
                ? "us-territories-geo.json"
                : jsConfig.Contains("geoCanadaFilename")
                    ? "canada-geo.json"
                    : "usonly-geo.json";

        var path = Path.Combine(WwwRoot, "Scripts", "maps-geo", geoFile);
        return File.Exists(path) ? File.ReadAllText(path) : "{}";
    }
}
