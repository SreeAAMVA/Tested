using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

[McpServerToolType]
public static class RenderTools
{
    private static string WwwRoot =>
        Path.Combine(
            Environment.GetEnvironmentVariable("MAPS_REPO_ROOT")
            ?? throw new InvalidOperationException("MAPS_REPO_ROOT not set."),
            "AAMVA.Website.Maps", "AAMVA.Website.Maps", "wwwroot");

    // Maps the geo-helper.js variable names to their geo JSON files so we can
    // inline only the one geography a config actually uses (keeps us under the
    // Claude Desktop MCP-app ~150K-character inline limit).
    private static readonly Dictionary<string, string> GeoVarToFile = new()
    {
        ["geoUnitedStatesFilename"]                = "usonly-geo.json",
        ["geoUnitedStatesWithTerritoriesFilename"] = "us-territories-geo.json",
        ["geoCanadaFilename"]                      = "canada-geo.json",
        ["geoUnitedStatesWithCanadaFilename"]      = "us-territories-canada-geo.json",
    };

    [McpServerTool]
    [Description(
        "Render a Highcharts map config as an interactive map preview so you can see what the map will look like. " +
        "Pass either the filename without extension as returned by list_maps " +
        "(e.g. 'us-s2s-implementation') or a full absolute path. " +
        "Returns an interactive inline SVG choropleth (hover for state + category).")]
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

        // Determine which geo file(s) to inline. Inline only the one the config
        // references; fall back to all four if it sets geoFilename dynamically.
        var geoFiles = new List<string>();
        var geoVarMatch = Regex.Match(jsConfig, @"geoFilename\s*:\s*(geo\w+)");
        if (geoVarMatch.Success && GeoVarToFile.TryGetValue(geoVarMatch.Groups[1].Value, out var gf))
            geoFiles.Add(gf);
        else
            geoFiles.AddRange(GeoVarToFile.Values);

        // Build a fully self-contained, lightweight HTML document. We do NOT
        // ship Highcharts (~350KB) or jQuery (~88KB) — both blow past the inline
        // limit. Instead we inline the site's real helper scripts + config, stub
        // jQuery and Highcharts.mapChart, and render the choropleth as SVG. This
        // reuses the exact category/colour/data-join logic the site uses.
        var sb = new StringBuilder();
        sb.Append("""
<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<style>
  body { margin: 0; padding: 12px; background: #fff; font-family: sans-serif; }
  table { border-collapse: collapse; }
</style>
</head>
<body>
""");
        sb.Append($"<div id=\"{containerId}\" style=\"width:100%;\"></div>\n");
        sb.Append($"<div id=\"{legendId}\"></div>\n");

        // 1. jQuery + Highcharts.mapChart SVG-renderer stubs
        sb.Append("<script>\n").Append(StubJs).Append("\n</script>\n");

        // 2. Real site helpers (small): colours, geo names, legend, map config builder
        AppendScriptFile(sb, "Scripts/shared/color-helper.js");
        sb.Append("<script>var mapPrefix = '/';</script>\n");
        AppendScriptFile(sb, "Scripts/shared/geo-helper.js");
        AppendScriptFile(sb, "Scripts/shared/external-legend-helper.js");
        AppendScriptFile(sb, "Scripts/shared/map-helper.js");

        // 3. Inline geo JSON + sample data, override fetch & $.getJSON
        sb.Append("<script>\n(function(){\n  var GEO = {\n");
        foreach (var f in geoFiles) AppendGeoEntry(sb, f);
        sb.Append("  };\n");
        sb.Append($"  var SAMPLE = {sampleData};\n");
        sb.Append("""
  function geoFor(u){ u = String(u); for (var k in GEO){ if (u.indexOf(k) !== -1) return GEO[k]; } return null; }
  window.fetch = function(url){
    var g = geoFor(url);
    if (g) return Promise.resolve(new Response(JSON.stringify(g), { headers: { 'Content-Type': 'application/json' } }));
    if (String(url).indexOf('api/mapdata') !== -1)
      return Promise.resolve(new Response(JSON.stringify(SAMPLE), { headers: { 'Content-Type': 'application/json' } }));
    return Promise.reject(new Error('blocked: ' + url));
  };
  jQuery.getJSON = function(url, cb){
    var g = geoFor(url);
    if (g) { setTimeout(function(){ cb(g); }, 0); return { done:function(){}, fail:function(){} }; }
    if (String(url).indexOf('api/mapdata') !== -1) { setTimeout(function(){ cb(SAMPLE); }, 0); return { done:function(){}, fail:function(){} }; }
    setTimeout(function(){ cb(null); }, 0); return { done:function(){}, fail:function(){} };
  };
})();
</script>
""");

        // 4. The map config itself (runs its IIFE -> renderMap -> our stub)
        sb.Append("\n<script>\n").Append(jsConfig).Append("\n</script>\n</body>\n</html>");

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
                     + $" — {html.Length:N0} chars"
            }
        ];
    }

    private static void AppendScriptFile(StringBuilder sb, string relativePath)
    {
        var path = ResolveCaseInsensitive(WwwRoot, relativePath);
        if (path is null) return;
        sb.Append("<script>\n").Append(File.ReadAllText(path)).Append("\n</script>\n");
    }

    // Properties the SVG renderer actually needs (everything else is dropped).
    private static readonly string[] KeepProps =
        { "hc_key", "name", "postal_cod", "hc-middle-x", "hc-middle-y" };

    // Inline a geo file, slimmed to stay under the MCP-app ~150K char limit:
    // keep only the properties the renderer uses and round coordinates to whole
    // units (the planar bbox is ~7000 wide, so rounding is visually lossless).
    private static void AppendGeoEntry(StringBuilder sb, string fileName)
    {
        var path = ResolveCaseInsensitive(WwwRoot, "Scripts/maps-geo/" + fileName);
        if (path is null) return;
        var raw = File.ReadAllText(path);
        string compact;
        try
        {
            var root  = JsonNode.Parse(raw)!.AsObject();
            var slim  = new JsonObject { ["type"] = root["type"]?.DeepClone() ?? "FeatureCollection" };
            var feats = new JsonArray();
            foreach (var f in root["features"]!.AsArray())
            {
                var fo    = f!.AsObject();
                var props = fo["properties"]?.AsObject();
                var np    = new JsonObject();
                if (props is not null)
                    foreach (var k in KeepProps)
                        if (props.TryGetPropertyValue(k, out var v))
                            np[k] = v?.DeepClone();

                var geom = fo["geometry"]!.AsObject();
                feats.Add(new JsonObject
                {
                    ["type"]       = "Feature",
                    ["properties"] = np,
                    ["geometry"]   = new JsonObject
                    {
                        ["type"]        = geom["type"]!.DeepClone(),
                        ["coordinates"] = RoundCoords(geom["coordinates"]!),
                    }
                });
            }
            slim["features"] = feats;
            compact = slim.ToJsonString();
        }
        catch
        {
            compact = raw.Trim();
        }
        sb.Append($"    '{fileName}': ").Append(compact).Append(",\n");
    }

    // Recursively round coordinate numbers to whole units. A position is an array
    // whose first element is a number; anything else is a nested array of positions.
    private static JsonNode RoundCoords(JsonNode node)
    {
        var arr = node.AsArray();
        if (arr.Count > 0 && arr[0] is JsonValue first && first.TryGetValue<double>(out _))
        {
            var pos = new JsonArray();
            foreach (var n in arr) pos.Add((long)Math.Round(n!.GetValue<double>()));
            return pos;
        }
        var nested = new JsonArray();
        foreach (var n in arr) nested.Add(RoundCoords(n!));
        return nested;
    }

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

    // Minimal jQuery stub (only what the map helpers touch at render time) plus a
    // Highcharts.mapChart implementation that renders the prepared chart config as
    // an inline SVG choropleth. Geo coordinates are pre-projected planar values,
    // so rendering is a simple Y-flip via a group transform + viewBox.
    private const string StubJs = """
(function () {
  function JQ(nodes) { this.nodes = nodes || []; }
  JQ.prototype.html   = function (h) { this.nodes.forEach(function (n) { n.innerHTML = h; }); return this; };
  JQ.prototype.append = function (h) { this.nodes.forEach(function (n) { if (typeof h === 'string') n.insertAdjacentHTML('beforeend', h); else if (h && h.nodes) h.nodes.forEach(function (c) { n.appendChild(c); }); else if (h) n.appendChild(h); }); return this; };
  JQ.prototype.val    = function (v) { if (v === undefined) return this.nodes[0] ? this.nodes[0].value : undefined; this.nodes.forEach(function (n) { n.value = v; }); return this; };
  JQ.prototype.text   = function (t) { if (t === undefined) return this.nodes[0] ? this.nodes[0].textContent : ''; this.nodes.forEach(function (n) { n.textContent = t; }); return this; };
  JQ.prototype.dialog = function () { return this; };
  // Interaction methods the configs wire up (dropdowns, dialogs); no-ops in a
  // static preview so the config IIFE doesn't throw.
  ['on', 'change', 'click', 'ready', 'hide', 'show', 'css', 'attr', 'addClass', 'removeClass'].forEach(function (m) {
    JQ.prototype[m] = function () { return this; };
  });
  function jq(sel) {
    if (typeof sel === 'function') { if (document.readyState !== 'loading') sel(); else document.addEventListener('DOMContentLoaded', sel); return; }
    if (typeof sel === 'string') {
      if (sel.charAt(0) === '<') { var d = document.createElement('div'); d.innerHTML = sel.trim(); return new JQ([d.firstChild]); }
      return new JQ(Array.prototype.slice.call(document.querySelectorAll(sel)));
    }
    if (sel && sel.nodeType) return new JQ([sel]);
    return new JQ([]);
  }
  jq.noConflict = function () { return jq; };
  jq.each = function (c, cb) { if (Array.isArray(c)) { for (var i = 0; i < c.length; i++) cb(i, c[i]); } else { for (var k in c) cb(k, c[k]); } };
  jq.getJSON = function (url, cb) { setTimeout(function () { cb(null); }, 0); return { done: function () {}, fail: function () {} }; };
  window.jQuery = window.$ = jq;
})();

window.Highcharts = {
  mapChart: function (containerId, cfg) {
    var el = document.getElementById(containerId);
    if (!el) return;
    var geo = (cfg.chart && cfg.chart.map) || { features: [] };
    var features = geo.features || [];
    var series = cfg.series || [];
    var dataSeries = null;
    // The data series is the one that isn't the allAreas base layer. Pick it by
    // that flag (not by truthy .data) so dataLabels/colours still apply when the
    // map has no data (all-grey preview).
    for (var i = 0; i < series.length; i++) { if (series[i] && !series[i].allAreas) { dataSeries = series[i]; break; } }
    if (!dataSeries && series.length) dataSeries = series[series.length - 1];
    var mapData = (dataSeries && dataSeries.data) || [];
    var nullColor = (dataSeries && dataSeries.nullColor) || 'rgba(220,220,220,1)';
    var dataClasses = (cfg.colorAxis && cfg.colorAxis.dataClasses) || [];
    var lblFmt = (dataSeries && dataSeries.dataLabels && dataSeries.dataLabels.format) || '';
    var lm = lblFmt.match(/\{point\.properties\.(\w+)\}/);
    var labelProp = lm ? lm[1] : null;

    function resolveColor(c) {
      if (!c) return nullColor;
      if (typeof c === 'object') { return (c.pattern && c.pattern.backgroundColor) ? c.pattern.backgroundColor : nullColor; }
      return c;
    }
    var byKey = {};
    mapData.forEach(function (d) { if (d && d.hc_key != null) byKey[d.hc_key] = d; });
    function classOf(val) { var v = +val; for (var i = 0; i < dataClasses.length; i++) { var dc = dataClasses[i]; if (v >= dc.from && v <= dc.to) return dc; } return null; }

    var minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    function scan(c) { if (typeof c[0] === 'number') { if (c[0] < minX) minX = c[0]; if (c[0] > maxX) maxX = c[0]; if (c[1] < minY) minY = c[1]; if (c[1] > maxY) maxY = c[1]; } else { for (var i = 0; i < c.length; i++) scan(c[i]); } }
    features.forEach(function (f) { if (f.geometry && f.geometry.coordinates) scan(f.geometry.coordinates); });

    function ringPath(r) { var d = 'M' + r[0][0] + ' ' + r[0][1]; for (var i = 1; i < r.length; i++) d += 'L' + r[i][0] + ' ' + r[i][1]; return d + 'Z'; }
    function geomPath(g) { var d = ''; if (g.type === 'Polygon') { g.coordinates.forEach(function (r) { d += ringPath(r); }); } else if (g.type === 'MultiPolygon') { g.coordinates.forEach(function (p) { p.forEach(function (r) { d += ringPath(r); }); }); } return d; }
    function esc(s) { return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;'); }

    var paths = '', labels = '';
    features.forEach(function (f) {
      var props = f.properties || {};
      var rec = byKey[props.hc_key];
      var hasVal = rec && rec.value != null;
      var dc = hasVal ? classOf(rec.value) : null;
      var fill = dc ? resolveColor(dc.color) : nullColor;
      var nm = props.name || props.hc_key || '';
      var title = nm + (dc && dc.name ? (' — ' + dc.name) : '');
      paths += '<path d="' + geomPath(f.geometry) + '" fill="' + fill + '" stroke="#fff" stroke-width="1" vector-effect="non-scaling-stroke"><title>' + esc(title) + '</title></path>';
      if (labelProp && props[labelProp] != null) {
        var fx0 = Infinity, fy0 = Infinity, fx1 = -Infinity, fy1 = -Infinity;
        (function () { function s(c) { if (typeof c[0] === 'number') { if (c[0] < fx0) fx0 = c[0]; if (c[0] > fx1) fx1 = c[0]; if (c[1] < fy0) fy0 = c[1]; if (c[1] > fy1) fy1 = c[1]; } else for (var i = 0; i < c.length; i++) s(c[i]); } s(f.geometry.coordinates); })();
        var mx = (props['hc-middle-x'] != null) ? props['hc-middle-x'] : 0.5;
        var my = (props['hc-middle-y'] != null) ? props['hc-middle-y'] : 0.5;
        var lx = fx0 + (fx1 - fx0) * mx;
        var ly = fy0 + (fy1 - fy0) * my;
        labels += '<text x="' + lx + '" y="' + (-ly) + '" font-size="11" font-family="monospace" font-weight="bold" text-anchor="middle" dominant-baseline="middle" fill="' + (dc ? '#fff' : '#333') + '" pointer-events="none">' + esc(props[labelProp]) + '</text>';
      }
    });
    var vb = minX + ' ' + (-maxY) + ' ' + (maxX - minX) + ' ' + (maxY - minY);
    el.innerHTML =
      '<svg xmlns="http://www.w3.org/2000/svg" viewBox="' + vb + '" width="100%" style="max-height:70vh">' +
      '<style>path:hover{opacity:.72;cursor:pointer}</style>' +
      '<g transform="scale(1,-1)">' + paths + '</g>' + labels + '</svg>';
  }
};
""";
}
