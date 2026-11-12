using System.Globalization;
using System.Text.Json.Nodes;

namespace DbDataKit.Apps;

/// <summary>What resolving a custom page's rows gave: the status the host answers with and the JSON body (<c>{data, params}</c> on 200, <c>{code, message}</c> otherwise).</summary>
public sealed record CustomPageAnswer(int Status, JsonObject Body);

/// <summary>
/// What runs the queries a custom page declares, as the signed-in person (plan 32 DBR33): in the serving layer, the same core every widget and every query goes through, so access, limits and audit are the host's, never the page's.
/// The apps host checked the sign-in, the role on the instance and the approval; the implementation checks that the person is one it can run queries for.
/// </summary>
public interface ICustomPageRows
{
    /// <param name="session">The person's sign-in at this host.</param>
    /// <param name="instance">The app instance the page belongs to.</param>
    /// <param name="view">The manifest's entry of the page (its declared queries and parameters).</param>
    /// <param name="parameters">The address's parameter values, already checked against the entry's declaration.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    Task<CustomPageAnswer> ResolveAsync(AppSession session, ResolvedInstance instance, CustomView view, IReadOnlyDictionary<string, JsonNode?> parameters, CancellationToken cancellationToken);
}

/// <summary>
/// A custom <b>page</b> of an app on the apps host (plan 32 section 9f, DBR33): a manifest <c>views</c> entry of <c>kind: page</c>, opened at <c>/apps/&lt;instance&gt;/_views/&lt;name&gt;</c> with its parameters in the query string.
/// The host's page frames the app's own document (<c>_app/#view=&lt;Export&gt;&amp;mode=screen</c>, sandboxed like every app) and plays the host's side of the SDK's conversation: the frame says <c>views:hello</c>, the page fetches the rows
/// <b>as the person</b> (<c>.../_rows</c>) and answers <c>views:init</c>; the code never fetches. This class is what is pure about it: finding the entry, reading the address's parameters against the declaration, listing the pages.
/// </summary>
public static class CustomPages
{
    /// <summary>The path segment under an instance that holds the pages.</summary>
    public const string Segment = "_views";

    /// <summary>The address of a page (no parameters).</summary>
    public static string Address(string instance, string name) => $"/apps/{Uri.EscapeDataString(instance)}/{Segment}/{Uri.EscapeDataString(name)}";

    /// <summary>The address the page's frame fetches rows from.</summary>
    public static string RowsAddress(string instance, string name) => Address(instance, name) + "/_rows";

    /// <summary>The page the instance's manifest lists under that name; null for a name it does not have and for a widget (a widget is placed by a dashboard, it has no address).</summary>
    public static CustomView? Find(ResolvedInstance instance, string name) =>
        instance.Manifest.CustomViews.FirstOrDefault(v => v.Kind == CustomViewKind.Page && string.Equals(v.Name, name, StringComparison.Ordinal));

    /// <summary>One page in the instance catalog's feed: what a portal or a report browser lists it by.</summary>
    public sealed record Listing(string Name, string? Title, string Address, IReadOnlyList<string> Formats, IReadOnlyList<string> Parameters);

    /// <summary>The formats of a page's export when its entry names none.</summary>
    public static readonly IReadOnlyList<string> DefaultFormats = ["pdf"];

    /// <summary>The instance's pages by their manifest entries: its name and title, the address, the formats it offers and its parameters.</summary>
    public static IReadOnlyList<Listing> Pages(ResolvedInstance instance, string instanceName) =>
        [.. instance.Manifest.CustomViews.Where(v => v.Kind == CustomViewKind.Page)
            .Select(v => new Listing(v.Name, v.Title, Address(instanceName, v.Name), v.Formats.Count == 0 ? DefaultFormats : v.Formats, [.. v.Params.Select(p => p.Name)]))];

    /// <summary>
    /// The address's parameters read against the entry's declaration: each declared name by its type (<c>string</c>, <c>integer</c>, <c>number</c>, <c>boolean</c>; a <c>many</c> parameter takes every value given, any other takes one). A name the entry does not
    /// declare is not read: it reaches nothing. A value of the wrong type, two values for a single parameter and a required parameter with no value are a problem in words, naming the parameter, and no values.
    /// </summary>
    public static (IReadOnlyDictionary<string, JsonNode?>? Values, string? Problem) Parameters(CustomView view, IEnumerable<KeyValuePair<string, string[]>> query)
    {
        var given = query.GroupBy(q => q.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.SelectMany(q => q.Value).ToArray(), StringComparer.Ordinal);
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var p in view.Params)
        {
            var texts = given.GetValueOrDefault(p.Name)?.Where(t => t.Length > 0).ToArray() ?? [];
            if (texts.Length == 0)
            {
                if (p.Required) return (null, $"The page '{view.Name}' needs the parameter `{p.Name}`: add `?{p.Name}=...` to the address.");
                continue;
            }
            if (!p.Many && texts.Length > 1) return (null, $"The parameter `{p.Name}` of the page '{view.Name}' takes one value, not {texts.Length}.");
            var nodes = new List<JsonNode?>();
            foreach (var text in texts)
            {
                var node = Coerce(p.Type, text);
                if (node is null) return (null, $"The parameter `{p.Name}` of the page '{view.Name}' is {(p.Type == "integer" ? "an integer" : p.Type == "number" ? "a number" : p.Type == "boolean" ? "true or false" : "text")}, not '{text}'.");
                nodes.Add(node);
            }
            values[p.Name] = p.Many ? new JsonArray([.. nodes]) : nodes[0];
        }
        return (values, null);
    }

    private static JsonNode? Coerce(string type, string text) => type switch
    {
        "integer" => long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l) ? JsonValue.Create(l) : null,
        "number" => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? JsonValue.Create(d) : null,
        "boolean" => text switch { "true" => JsonValue.Create(true), "false" => JsonValue.Create(false), _ => null },
        _ => JsonValue.Create(text),
    };

    /// <summary>The ink of a view's <c>var(--sui-text)</c> and the others on screen, by scheme: the same colours as the chart palette of <c>@dbdatakit/visuals</c> (<c>themeTokens</c>), which a test keeps equal. The shell hands them to the frame.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Tokens = new Dictionary<string, IReadOnlyDictionary<string, string>>
    {
        ["light"] = new Dictionary<string, string> { ["sui-text"] = "#1f2328", ["sui-muted"] = "#59636e", ["sui-grid"] = "#d8dee4", ["sui-axis"] = "#8c959f", ["sui-accent"] = "#0072B2", ["sui-bg"] = "#ffffff" },
        ["dark"] = new Dictionary<string, string> { ["sui-text"] = "#e6edf3", ["sui-muted"] = "#9da7b3", ["sui-grid"] = "#30363d", ["sui-axis"] = "#6e7681", ["sui-accent"] = "#56B4E9", ["sui-bg"] = "#0d1117" },
    };

    /// <summary>The declaration the page's script checks a <c>setParam</c> against, as JSON (<c>{name: {type, many}}</c>).</summary>
    public static string DeclarationJson(CustomView view) =>
        new JsonObject(view.Params.Select(p => KeyValuePair.Create(p.Name, (JsonNode?)new JsonObject { ["type"] = p.Type, ["many"] = p.Many }))).ToJsonString();

    /// <summary>The token table as the shell script's literal.</summary>
    public static string TokensJson() =>
        new JsonObject(Tokens.Select(s => KeyValuePair.Create(s.Key, (JsonNode?)new JsonObject(s.Value.Select(t => KeyValuePair.Create(t.Key, (JsonNode?)JsonValue.Create(t.Value)))))) ).ToJsonString();
}
