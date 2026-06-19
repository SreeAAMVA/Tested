using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;
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
        "Render a Highcharts map config as an interactive map preview so you can see what the map will look like. " +
        "Pass either the filename without extension as returned by list_maps " +
        "(e.g. 'us-s2s-implementation') or a full absolute path. " +
        "Returns a fully self-contained interactive HTML map rendered inline.")]
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
        jsConfigPath = MapTools.ResolveJsConfigPath(jsConfigPath) ?? jsConfigPath;

        if (!File.Exists(jsConfigPath))
            return [new TextContentBlock { Text = $"File not found: {jsConfigPath}. Use list_maps to see valid filenames." }];

        var jsConfig    = await File.ReadAllTextAsync(jsConfigPath);
        var containerId = Regex.Match(jsConfig, @"mapTagSelector\s*:\s*'([^']+)'") is { Success: true } m
                          ? m.Groups[1].Value
                          : "map-container";
        var legendId    = containerId.Replace("container", "legendcontainer");
        var sampleData  = string.IsNullOrWhiteSpace(sampleDataJson) ? "null" : sampleDataJson;

        // Build a fully self-contained HTML document. Everything — jQuery,
        // Highmaps, the shared helpers, all geo JSON, and the map config — is
        // inlined as <script> blocks. fetch() and $.getJSON are overridden to
        // serve the inlined geo JSON and sample data, so the page makes zero
        // network requests. This is required because the Claude Desktop MCP-app
        // webview is sandboxed and cannot reach localhost or external origins.
        var sb = new StringBuilder();
        sb.Append("""
<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<style>
  body { margin: 0; padding: 16px; background: #fff; font-family: sans-serif; }
</style>
</head>
<body>
""");
        sb.Append($"<div id=\"{containerId}\" style=\"width:100%;min-height:480px;\"></div>\n");
        sb.Append($"<div id=\"{legendId}\"></div>\n");

        // jQuery, Highmaps, pattern-fill
        AppendScriptFile(sb, "Scripts/jquery-3.6.0.min.js");
        AppendScriptFile(sb, "Scripts/vendor/highmaps.js");
        AppendScriptFile(sb, "Scripts/vendor/pattern-fill.js");

        // mapPrefix must be defined before geo-helper.js (it reads it at load)
        sb.Append("<script>var mapPrefix = '/';</script>\n");

        // Shared helpers
        AppendScriptFile(sb, "Scripts/shared/color-helper.js");
        AppendScriptFile(sb, "Scripts/shared/geo-helper.js");
        AppendScriptFile(sb, "Scripts/shared/external-legend-helper.js");
        AppendScriptFile(sb, "Scripts/shared/map-helper.js");

        // Inline all geo JSON + sample data, then override fetch & $.getJSON so
        // the config's data calls resolve from inlined content (no network).
        sb.Append("<script>\n(function(){\n");
        sb.Append("  var GEO = {\n");
        AppendGeoEntry(sb, "usonly-geo.json");
        AppendGeoEntry(sb, "us-territories-geo.json");
        AppendGeoEntry(sb, "canada-geo.json");
        AppendGeoEntry(sb, "us-territories-canada-geo.json");
        sb.Append("  };\n");
        sb.Append($"  var SAMPLE = {sampleData};\n");
        sb.Append("""
  function geoFor(u){ u = String(u); for (var k in GEO){ if (u.indexOf(k) !== -1) return GEO[k]; } return null; }
  var _fetch = window.fetch;
  window.fetch = function(url){
    var g = geoFor(url);
    if (g) return Promise.resolve(new Response(JSON.stringify(g), { headers: { 'Content-Type': 'application/json' } }));
    if (String(url).indexOf('api/mapdata') !== -1)
      return Promise.resolve(new Response(JSON.stringify(SAMPLE), { headers: { 'Content-Type': 'application/json' } }));
    return _fetch ? _fetch.apply(this, arguments) : Promise.reject(new Error('blocked'));
  };
  if (window.jQuery) {
    jQuery.getJSON = function(url, cb){
      var g = geoFor(url);
      if (g) { setTimeout(function(){ cb(g); }, 0); return { done:function(){}, fail:function(){} }; }
      if (String(url).indexOf('api/mapdata') !== -1) { setTimeout(function(){ cb(SAMPLE); }, 0); return { done:function(){}, fail:function(){} }; }
      setTimeout(function(){ cb(null); }, 0); return { done:function(){}, fail:function(){} };
    };
  }
})();
</script>
""");

        // The map config itself
        sb.Append("\n<script>\n");
        sb.Append(jsConfig);
        sb.Append("\n</script>\n</body>\n</html>");

        var html = sb.ToString();

        return
        [
            new EmbeddedResourceBlock
            {
                Resource = new TextResourceContents
                {
                    Uri      = $"ui://map-preview/{Path.GetFileNameWithoutExtension(jsConfigPath)}",
                    MimeType = "text/html;profile=mcp-app",
                    Text     = html
                }
            },
            new TextContentBlock
            {
                Text = $"Interactive preview: {Path.GetFileName(jsConfigPath)}"
                     + (sampleData != "null" ? " (with sample data)" : " (no data — all states grey)")
            }
        ];
    }

    // Read a wwwroot file and append it wrapped in a <script> tag.
    private static void AppendScriptFile(StringBuilder sb, string relativePath)
    {
        var path = ResolveCaseInsensitive(WwwRoot, relativePath);
        if (path is null) return;
        sb.Append("<script>\n");
        sb.Append(File.ReadAllText(path));
        sb.Append("\n</script>\n");
    }

    // Append a "'filename.json': <json>," entry for the inlined GEO object.
    private static void AppendGeoEntry(StringBuilder sb, string fileName)
    {
        var path = ResolveCaseInsensitive(WwwRoot, "Scripts/maps-geo/" + fileName);
        if (path is null) return;
        sb.Append($"    '{fileName}': ");
        sb.Append(File.ReadAllText(path).Trim());
        sb.Append(",\n");
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
