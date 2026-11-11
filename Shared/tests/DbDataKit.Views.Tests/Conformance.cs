using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Config.Yaml;
using DbDataKit.Views;

namespace DbDataKit.Views.Tests;

/// <summary>The shared documents and goldens: <c>conformance/views</c>, which the TypeScript tests of <c>@dbdatakit/visuals</c> read too.</summary>
internal static class Conformance
{
    public static string Root { get; } = Path.Combine(AppContext.BaseDirectory, "conformance", "views");
    public static string SchemaRoot { get; } = Path.Combine(AppContext.BaseDirectory, "schemas");

    private static readonly object RegistryGate = new();
    private static bool registered;

    /// <summary>Registers the schemas by their <c>$id</c> once, so a schema that references another finds it.</summary>
    public static void RegisterSchemas()
    {
        lock (RegistryGate)
        {
            if (registered) return;
            foreach (var name in new[] { "core", "dashboard", "report", "view", "vega-lite-subset" })
                if (TryRead(name) is { } text) Json.Schema.SchemaRegistry.Global.Register(Json.Schema.JsonSchema.FromText(text));
            registered = true;
        }
    }

    private static string? TryRead(string name) { try { return ViewsLoader.Schema(name); } catch (ArgumentException) { return null; } }

    public static string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative));

    public static JsonNode Cases(string relative = "cases.json") => JsonNode.Parse(Read(relative))!;

    public static ViewKind? Kind(string? text) => text switch { "dashboard" => ViewKind.Dashboard, "report" => ViewKind.Report, _ => null };

    public static IReadOnlyDictionary<string, IReadOnlyList<ViewParameter>>? Targets(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        return o.ToDictionary(e => e.Key, e => (IReadOnlyList<ViewParameter>)e.Value!.AsArray()
            .Select(p => new ViewParameter(p!["name"]!.GetValue<string>(), p["type"]?.GetValue<string>() ?? "string", null, p["many"]?.GetValue<bool>() ?? false, p["required"]?.GetValue<bool>() ?? false, p["default"] is not null, null, 0)).ToList());
    }

    public static string Describe(IEnumerable<ConfigDiagnostic> diagnostics) => string.Join("\n", diagnostics.Select(d => $"{d} [{d.KeyPath}]"));

    /// <summary>The cases as xunit theory rows: the file and its place in the list.</summary>
    public static IEnumerable<object[]> Rows(string section, string cases = "cases.json") => Cases(cases)[section]!.AsArray().Select(c => new object[] { c!["file"]!.GetValue<string>() });
}
