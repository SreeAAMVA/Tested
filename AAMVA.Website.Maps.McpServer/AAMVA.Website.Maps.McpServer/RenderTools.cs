using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.RegularExpressions;

[McpServerToolType]
public static class RenderTools
{
    [McpServerTool]
    [Description(
        "Render a Highcharts map config as an interactive map preview so you can see what the map will look like. " +
        "Pass either the filename without extension as returned by list_maps " +
        "(e.g. 'us-s2s-implementation') or a full absolute path. " +
        "Returns an interactive HTML map rendered inline.")]
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
        var server = LocalServer.Instance
            ?? throw new InvalidOperationException("Local server is not running.");

        jsConfigPath = MapTools.ResolveJsConfigPath(jsConfigPath) ?? jsConfigPath;

        if (!File.Exists(jsConfigPath))
            return [new TextContentBlock { Text = $"File not found: {jsConfigPath}. Use list_maps to see valid filenames." }];

        var jsConfig    = await File.ReadAllTextAsync(jsConfigPath);
        var containerId = Regex.Match(jsConfig, @"mapTagSelector\s*:\s*'([^']+)'") is { Success: true } m
                          ? m.Groups[1].Value
                          : "map-container";
        var legendId    = containerId.Replace("container", "legendcontainer");
        var baseUrl     = $"http://localhost:{server.Port}";

        // If sample data supplied, store it so the local server returns it for
        // the /api/mapdata/* calls the map config makes.
        var dataApiKey = $"/api/mapdata/{containerId}";
        if (sampleDataJson is not null)
            server.SetDataForPath(dataApiKey, sampleDataJson);
        else
            server.ClearData(dataApiKey);

        // Inline sample data as a JS variable so it can also override $.getJSON
        // for the exact GUID-keyed endpoint in the config (without needing to know the GUID).
        var inlineDataScript = sampleDataJson is not null
            ? $$"""
              <script>
                (function () {
                  var _data = {{sampleDataJson}};
                  var _orig = jQuery.getJSON;
                  jQuery.getJSON = function (url, cb) {
                    if (url.indexOf('api/mapdata') !== -1) { setTimeout(function () { cb(_data); }, 0); return { done: function(){}, fail: function(){} }; }
                    return _orig.apply(this, arguments);
                  };
                })();
              </script>
              """
            : "";

        var html = $$"""
<!DOCTYPE html>
<html>
<head>
  <meta charset="utf-8">
  <style>
    body { margin: 0; padding: 16px; background: #fff; font-family: sans-serif; }
    #{{containerId}} { width: 100%; min-height: 480px; }
  </style>
</head>
<body>
  <div id="{{containerId}}"></div>
  <div id="{{legendId}}"></div>

  <script src="{{baseUrl}}/Scripts/jquery-3.6.0.min.js"></script>
  <script src="{{baseUrl}}/Scripts/vendor/highmaps.js"></script>
  <script src="{{baseUrl}}/Scripts/vendor/pattern-fill.js"></script>

  <!-- mapPrefix must be defined before geo-helper.js -->
  <script>var mapPrefix = '{{baseUrl}}/';</script>

  <script src="{{baseUrl}}/Scripts/shared/color-helper.js"></script>
  <script src="{{baseUrl}}/Scripts/shared/geo-helper.js"></script>
  <script src="{{baseUrl}}/Scripts/shared/external-legend-helper.js"></script>
  <script src="{{baseUrl}}/Scripts/shared/map-helper.js"></script>

  {{inlineDataScript}}
  <script>{{jsConfig}}</script>
</body>
</html>
""";

        return
        [
            new EmbeddedResourceBlock
            {
                Resource = new TextResourceContents
                {
                    Uri      = $"map-preview://{Path.GetFileNameWithoutExtension(jsConfigPath)}",
                    MimeType = "text/html;profile=mcp-app",
                    Text     = html
                }
            },
            new TextContentBlock
            {
                Text = $"Interactive preview: {Path.GetFileName(jsConfigPath)}"
                     + (sampleDataJson is not null ? " (with sample data)" : " (no data — all states grey)")
            }
        ];
    }
}
