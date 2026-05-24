using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

[McpServerToolType]
public static class MapTools
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string RepoRoot =>
        Environment.GetEnvironmentVariable("MAPS_REPO_ROOT")
        ?? throw new InvalidOperationException(
            "MAPS_REPO_ROOT environment variable is not set. " +
            "Set it to the root of the PublicWebsite-Mapping repository.");

    private static string ProjectRoot =>
        Path.Combine(RepoRoot, "AAMVA.Website.Maps", "AAMVA.Website.Maps");

    private static string WwwRoot =>
        Path.Combine(ProjectRoot, "wwwroot");

    private static string MapsConfigRoot =>
        Path.Combine(WwwRoot, "Scripts", "maps-config");

    private static string PagesRoot =>
        Path.Combine(ProjectRoot, "Pages");

    private static string DataServicePath =>
        Path.Combine(ProjectRoot, "Services", "Data", "MapsDataService.cs");

    private static string ColorToLegendClass(string colorConstant) =>
        "maps-" + Regex.Replace(colorConstant, "^color", "").ToLower();

    private static string ToKebabCase(string pascal) =>
        Regex.Replace(pascal, "([A-Z])", "-$1").TrimStart('-').ToLower();

    private static string PascalToCamel(string pascal) =>
        char.ToLower(pascal[0]) + pascal[1..];

    // Maps JS category folder name to the Blazor Pages subfolder
    private static readonly Dictionary<string, string> CategoryToPageFolder = new()
    {
        ["driver-systems"]       = "DriversSystems",
        ["vehicle-systems"]      = "VehicleSystems",
        ["verification-systems"] = "VerificationSystems",
        ["membership"]           = "Membership"
    };

    // ── Tools ─────────────────────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "List all existing maps organised by category. " +
        "Returns the JS config filename (without extension) for each map.")]
    public static string ListMaps()
    {
        if (!Directory.Exists(MapsConfigRoot))
            return $"Maps config directory not found at: {MapsConfigRoot}";

        var sb = new StringBuilder();
        foreach (var dir in Directory.GetDirectories(MapsConfigRoot).OrderBy(d => d))
        {
            var category = Path.GetFileName(dir);
            sb.AppendLine($"## {category}");
            foreach (var file in Directory.GetFiles(dir, "*.js").OrderBy(f => f))
                sb.AppendLine($"  - {Path.GetFileNameWithoutExtension(file)}");
        }
        return sb.ToString();
    }

    [McpServerTool]
    [Description(
        "Read the JavaScript config for an existing map. " +
        "Pass the filename without extension, e.g. 'us-s2s-implementation'.")]
    public static string GetMapConfig(
        [Description("JS config filename without extension, e.g. 'us-s2s-implementation'")]
        string filename)
    {
        var files = Directory.GetFiles(MapsConfigRoot, $"{filename}.js", SearchOption.AllDirectories);
        if (files.Length == 0)
            return $"Map config '{filename}' not found under {MapsConfigRoot}";
        return File.ReadAllText(files[0]);
    }

    [McpServerTool]
    [Description("List all AAMVA brand colours available for map categories.")]
    public static string ListColors() => """
        AAMVA Style Guide Colours (use the exact JavaScript constant name):

        Standard:
          colorDarkBlue       #14377D
          colorBlack          #000000
          colorSecondaryBlue  #021D49
          colorBlue           #1E55C3
          colorRed            #BA0C2F
          colorPurple         #5F1E99
          colorOrange         #EC8B26
          colorOlive          #76992A
          colorTurquoise      #229199
          colorDarkerGrey     #4C5463
          colorDarkGrey       #757A85
          colorGrey           #95A2BD
          colorLightGrey      #C6D6F5
          colorLighterGrey    #F1F5FD
          colorGrey229        #E5E5E5
          colorLightBlue      #9eb5db
          colorDarkTurquoise  #23555A
          colorMediumGrey     #AFAEAE
          colorLightPurple    #9251cc

        Application-specific (approved by AAMVA communications):
          colorLightGreen     #acc17f
          colorDarkGreen      #526b1d
          colorDarkerGreen    #1f5c10
        """;

    [McpServerTool]
    [Description("List the geography file variable names available for use in map configs.")]
    public static string ListGeoFiles() => """
        Geography file variables (use the JS variable name in the geoFilename property):

          geoUnitedStatesFilename                   US states only
          geoUnitedStatesWithTerritoriesFilename     US + territories
          geoUnitedStatesWithCanadaFilename          US + territories + Canada
          geoCanadaFilename                          Canada only
        """;

    [McpServerTool]
    [Description("List the map category folder names and their corresponding Blazor page folders.")]
    public static string ListCategories() => """
        Category (JS folder)     →  Blazor Pages folder
        driver-systems           →  DriversSystems
        vehicle-systems          →  VehicleSystems
        verification-systems     →  VerificationSystems
        membership               →  Membership
        """;

    [McpServerTool]
    [Description(
        "Read any source file in the mapping application by its path relative to the repository root. " +
        "Example: 'AAMVA.Website.Maps/AAMVA.Website.Maps/Services/Data/MapsDataService.cs'")]
    public static string ReadSourceFile(
        [Description("Path relative to MAPS_REPO_ROOT")]
        string relativePath)
    {
        var full = Path.Combine(RepoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(full) ? File.ReadAllText(full) : $"File not found: {full}";
    }

    [McpServerTool]
    [Description(
        "Create a new map. Generates two artefacts: " +
        "(1) the JavaScript Highcharts config file and " +
        "(2) the Blazor Razor page. " +
        "Does NOT register the map in MapsDataService.cs — call register_map after you are " +
        "happy with the preview. Returns the paths of the files written.")]
    public static string CreateMap(
        [Description("Unique PascalCase identifier, e.g. 'USS2SImplementationStatus'. Used for the Razor page name and MapIdentifier.")]
        string identifier,

        [Description("Short lowercase abbreviation, e.g. 's2s'. Used to name the map container element.")]
        string abbreviation,

        [Description("Full display title shown in the map, e.g. 'State-to-State (S2S) Implementation Status'.")]
        string title,

        [Description("Category folder: driver-systems | vehicle-systems | verification-systems | membership")]
        string category,

        [Description("Geography variable: geoUnitedStatesFilename | geoUnitedStatesWithTerritoriesFilename | geoUnitedStatesWithCanadaFilename | geoCanadaFilename")]
        string geoFilename,

        [Description("GUID of the data API endpoint row in the database, e.g. '79419253-A023-4A8D-B4BB-6F61844ED9E8'.")]
        string dataGuid,

        [Description(@"JSON array of category objects. Each object: {""name"":""Label"",""color"":""colorBlue"",""value"":1}. Values must be sequential integers starting at 1.")]
        string categoriesJson,

        [Description("Show a header above the map.")]
        bool showHeader = false,

        [Description("Header text (only used when showHeader is true).")]
        string header = "",

        [Description("Show a geo-scope changer dropdown (US only / US+territories / US+Canada etc.).")]
        bool showGeoChanger = false,

        [Description("Number of rows in the legend table.")]
        int legendNumRows = 1,

        [Description("Number of columns in the legend table.")]
        int legendNumColumns = 3,

        [Description("CSS width of the legend, e.g. '33%' or '75%'.")]
        string legendWidth = "33%",

        [Description("CSS width of the label column inside the legend, e.g. '100%'.")]
        string legendLabelColumnWidth = "100%",

        [Description("CSS width of the count/percent columns inside the legend.")]
        string legendColumnWidth = "100%",

        [Description("Optional footnote message shown below the legend.")]
        string legendMessage = "",

        [Description("Show jurisdiction counts column in legend.")]
        bool legendIncludeJurisdictionCounts = false,

        [Description("Show jurisdiction percent column in legend.")]
        bool legendIncludeJurisdictionPercents = false,

        [Description("Show population percent column in legend.")]
        bool legendIncludePopulationPercents = false,

        [Description("Show cumulative columns in legend.")]
        bool legendIncludeCumulativeColumns = false
    )
    {
        // ── Validate ──────────────────────────────────────────────────────────
        if (!CategoryToPageFolder.TryGetValue(category, out var pageFolder))
            return $"Unknown category '{category}'. Valid: {string.Join(", ", CategoryToPageFolder.Keys)}";

        List<MapCategory> cats;
        try
        {
            cats = JsonSerializer.Deserialize<List<MapCategory>>(categoriesJson,
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new JsonException("null result");
        }
        catch (Exception ex)
        {
            return $"Invalid categoriesJson: {ex.Message}";
        }

        // ── Derive names ──────────────────────────────────────────────────────
        var jsFilename  = ToKebabCase(identifier);
        var varName     = PascalToCamel(identifier) + "Config";
        var containerId = abbreviation + "-container";

        // ── 1. JS config file ─────────────────────────────────────────────────
        var categoriesJs = string.Join(",\n        ", cats.Select((c, i) =>
            $"buildCategory('{EscapeJs(c.Name)}', {c.Color}, {c.Value}, " +
            $"'#{containerId}-Category{i + 1}Label', '{ColorToLegendClass(c.Color)}')"));

        var jsContent = $$"""
const {{varName}} = {
    title: '{{EscapeJs(title)}}',
    updatedDate: '',
    geoFilename: {{geoFilename}},
    dataFilename: mapPrefix + 'api/mapdata/{{dataGuid}}',
    topologyJoinColumn: 'hc_key',
    dataJoinColumn: 'hc_key',
    jurisdictionDataLabel: '{point.properties.postal_cod}',
    mapTagSelector: '{{containerId}}',
    includeLegendCount: {{Bool(legendIncludeJurisdictionCounts)}},
    includeLegendPercent: {{Bool(legendIncludeJurisdictionPercents)}},
    showNegativeOptions: false,
    categorys: [
        {{categoriesJs}}
    ],
    popupFormatter: null
};

var $j = jQuery.noConflict();

(async () => {

    var geoData = await fetch({{varName}}.geoFilename).then(response => response.json());
    var populationData = null;

    $j.getJSON({{varName}}.dataFilename, function (data) {

        var mapData = data;

        renderMap(geoData, mapData, populationData, {{varName}});

    });
})();
""";

        var jsDir  = Path.Combine(MapsConfigRoot, category);
        var jsPath = Path.Combine(jsDir, $"{jsFilename}.js");
        Directory.CreateDirectory(jsDir);
        File.WriteAllText(jsPath, jsContent);

        // ── 2. Razor page ─────────────────────────────────────────────────────
        var razorContent = $"""
@page "/{pageFolder}/{identifier}"

<MapViewer MapIdentifier="{identifier}" />
""";
        var razorDir  = Path.Combine(PagesRoot, pageFolder);
        var razorPath = Path.Combine(razorDir, $"{identifier}.razor");
        Directory.CreateDirectory(razorDir);
        File.WriteAllText(razorPath, razorContent);

        // ── Summary ───────────────────────────────────────────────────────────
        var relJs    = jsPath.Replace(RepoRoot, "").TrimStart(Path.DirectorySeparatorChar);
        var relRazor = razorPath.Replace(RepoRoot, "").TrimStart(Path.DirectorySeparatorChar);

        return $"""
✅ Map '{identifier}' files written:
  {relJs}
  {relRazor}

Review the map with render_map_preview, then call register_map to add it to MapsDataService.cs.
""";
    }

    [McpServerTool]
    [Description(
        "Register a previously created map in MapsDataService.cs. " +
        "Call this only after you have reviewed the map preview and are happy with it. " +
        "Inserts the C# Map entry into the GetAll() list so the application serves the map.")]
    public static string RegisterMap(
        [Description("The same identifier used in create_map, e.g. 'USS2SImplementationStatus'.")]
        string identifier,

        [Description("The same abbreviation used in create_map, e.g. 's2s'.")]
        string abbreviation,

        [Description("The same category folder used in create_map, e.g. 'driver-systems'.")]
        string category,

        [Description("Whether to show a header above the map.")]
        bool showHeader = false,

        [Description("Header text (only used when showHeader is true).")]
        string header = "",

        [Description("Whether to show a geo-scope changer dropdown.")]
        bool showGeoChanger = false,

        [Description("Number of rows in the legend table.")]
        int legendNumRows = 1,

        [Description("Number of columns in the legend table.")]
        int legendNumColumns = 3,

        [Description("CSS width of the legend, e.g. '33%'.")]
        string legendWidth = "33%",

        [Description("CSS width of the label column inside the legend.")]
        string legendLabelColumnWidth = "100%",

        [Description("CSS width of the count/percent columns inside the legend.")]
        string legendColumnWidth = "100%",

        [Description("Optional footnote message shown below the legend.")]
        string legendMessage = "",

        [Description("Show jurisdiction counts column in legend.")]
        bool legendIncludeJurisdictionCounts = false,

        [Description("Show jurisdiction percent column in legend.")]
        bool legendIncludeJurisdictionPercents = false,

        [Description("Show population percent column in legend.")]
        bool legendIncludePopulationPercents = false,

        [Description("Show cumulative columns in legend.")]
        bool legendIncludeCumulativeColumns = false
    )
    {
        var jsFilename = ToKebabCase(identifier);

        // Guard: confirm the JS config file was already created
        var jsFiles = Directory.GetFiles(MapsConfigRoot, $"{jsFilename}.js", SearchOption.AllDirectories);
        if (jsFiles.Length == 0)
            return $"JS config '{jsFilename}.js' not found. Run create_map first.";

        var serviceSource = File.ReadAllText(DataServicePath);

        // Guard: don't insert twice
        if (Regex.IsMatch(serviceSource, $@"Identifier\s*=\s*""{Regex.Escape(identifier)}"""))
            return $"'{identifier}' is already registered in MapsDataService.cs.";

        var nextId = GetNextMapId(serviceSource);
        var indent = "                ";

        var newEntry = $$"""

{{indent}}new Map
{{indent}}{
{{indent}}    Id = {{nextId}},
{{indent}}    Identifier = "{{identifier}}",
{{indent}}    Abbreviation = "{{abbreviation}}",
{{indent}}    MapContainerClass = "ust-maps-container",
{{indent}}    ShowHeader = {{Bool(showHeader)}}, Header = "{{EscapeVerbatim(header)}}",
{{indent}}    ShowGeoChanger = {{Bool(showGeoChanger)}},
{{indent}}    ShowChanger = false,
{{indent}}    ShowLegend = true,
{{indent}}    LegendNumRows = {{legendNumRows}}, LegendNumColumns = {{legendNumColumns}},
{{indent}}    LegendWidth = "{{legendWidth}}", LegendLabelColumnWidth = "{{legendLabelColumnWidth}}", LegendColumnWidth = "{{legendColumnWidth}}",
{{indent}}    LegendMessage = "{{EscapeVerbatim(legendMessage)}}",
{{indent}}    LegendIncludeJurisdictionCounts = {{Bool(legendIncludeJurisdictionCounts)}},
{{indent}}    LegendIncludeJurisdictionPercents = {{Bool(legendIncludeJurisdictionPercents)}},
{{indent}}    LegendIncludePopulationPercents = {{Bool(legendIncludePopulationPercents)}},
{{indent}}    LegendIncludeCumulativeColumns = {{Bool(legendIncludeCumulativeColumns)}},
{{indent}}    MapConfigScriptFilePath = $"{ConfigurationHelper.MapsUrlPrefix}scripts/maps-config/{{category}}/{{jsFilename}}.js"
{{indent}}},
""";

        var patched = Regex.Replace(
            serviceSource,
            @"(\s+return maps;)",
            newEntry + "$1",
            RegexOptions.Multiline);

        File.WriteAllText(DataServicePath, patched);

        var relSvc = DataServicePath.Replace(RepoRoot, "").TrimStart(Path.DirectorySeparatorChar);
        return $"""
✅ '{identifier}' registered in MapsDataService.cs (Id = {nextId}).
  {relSvc}

⚠️  Remember to add per-jurisdiction rows to the database before the map will display data.
""";
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static int GetNextMapId(string source)
    {
        var matches = Regex.Matches(source, @"Id\s*=\s*(\d+)");
        return matches.Count == 0
            ? 1
            : matches.Cast<Match>().Select(m => int.Parse(m.Groups[1].Value)).Max() + 1;
    }

    private static string Bool(bool v) => v ? "true" : "false";

    private static string EscapeJs(string s) => s.Replace("'", "\\'");

    // Escape characters that would break a C# verbatim-style string already delimited by "
    private static string EscapeVerbatim(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // ── Data model for categories JSON ────────────────────────────────────────
    private record MapCategory(
        [property: JsonPropertyName("name")]  string Name,
        [property: JsonPropertyName("color")] string Color,
        [property: JsonPropertyName("value")] int    Value);
}
