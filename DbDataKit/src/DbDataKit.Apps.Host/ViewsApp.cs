using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace DbDataKit.Apps;

/// <summary>
/// The host's own script and style that draw an app of views only (plan 26, AP25): <c>views-app.js</c> and <c>views-app.css</c>, built from <c>DbDataKit/web/view-app</c> (the shared controls of <c>@dbdatakit/store-ui</c> and the renderers of
/// <c>@dbdatakit/visuals</c>). They are the host's code, not the app's: the package of an app of views holds only YAML.
/// </summary>
public sealed class ViewsRuntime
{
    public const string ScriptName = "views-app.js";
    public const string StyleName = "views-app.css";

    public ViewsRuntime(byte[] script, byte[]? style = null)
    {
        Script = script;
        Style = style;
        Hash = Convert.ToHexString(SHA256.HashData(script.Concat(style ?? []).ToArray()))[..16].ToLowerInvariant();
    }

    public byte[] Script { get; }

    public byte[]? Style { get; }

    /// <summary>Identifies this build of the runtime, for the entity tag.</summary>
    public string Hash { get; }

    /// <summary>The runtime built into <paramref name="directory"/> (the folder the web build wrote), or null when it is not there: a host then says so rather than serving an empty page.</summary>
    public static ViewsRuntime? FromDirectory(string directory)
    {
        var script = Path.Combine(directory, ScriptName);
        if (!File.Exists(script)) return null;
        var style = Path.Combine(directory, StyleName);
        return new ViewsRuntime(File.ReadAllBytes(script), File.Exists(style) ? File.ReadAllBytes(style) : null);
    }
}

/// <summary>
/// The page of an app of views only: a generated document that loads the host's runtime and the app's specs as one JSON file (<c>views.json</c>). The document is served the way an app's own is (sandboxed, the instance's policy), so the
/// runtime reaches data only through the shell's token and the host's data route, and reaches no other host. The specs are read again here with <see cref="ViewSpecLoader"/>, so a stored package that does not read shows its error
/// on the page and not a half-drawn screen.
/// </summary>
public static class ViewsApp
{
    public const string ViewsFile = "views.json";

    /// <summary>The element of the document that holds the specs (<c>type="application/json"</c>: data, never run, so the policy's <c>script-src</c> has nothing to refuse).</summary>
    public const string DataElement = "views-data";

    /// <summary>
    /// The generated document. Everything named in it is a fixed path under the instance's bundle folder. The specs are in the page itself: a sandboxed document has an opaque origin, so a <c>fetch</c> of <c>views.json</c> would be a
    /// cross-origin read this host does not answer with CORS headers. <paramref name="views"/> is <see cref="Json(ResolvedInstance)"/>; its angle brackets are escaped by the serializer, and again here, so it cannot end the element.
    /// </summary>
    public static byte[] Document(string title, string basePath, bool style, byte[] views) => Encoding.UTF8.GetBytes($$"""
        <!doctype html>
        <html lang="en"><head><base href="{{WebUtility.HtmlEncode(basePath)}}"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{{WebUtility.HtmlEncode(title)}}</title>
        {{(style ? $"<link rel=\"stylesheet\" href=\"{ViewsRuntime.StyleName}\">" : "")}}
        </head><body>
        <div id="root"></div>
        <script type="application/json" id="{{DataElement}}">{{Encoding.UTF8.GetString(views).Replace("<", "\u003c")}}</script>
        <script src="{{ViewsRuntime.ScriptName}}"></script>
        </body></html>
        """);

    /// <summary>Where a source tree finds the specs of its <c>views</c> list when it is built (<c>import views from './views.generated.json'</c> in <c>src/</c>).</summary>
    public const string GeneratedPath = "src/views.generated.json";

    /// <summary>
    /// What a build of a code app with view specs gives the bundler as <c>AppBundleRequest.GeneratedFiles</c> (DbDataKit.Apps.Bundling) (plan 22a GP34): the specs as <see cref="GeneratedPath"/>, read by the host's own loader. Whoever builds an app (<c>apps build</c>,
    /// the Workspace server) calls this, so a bundle draws what the loader checked. <paramref name="read"/> answers a source file's bytes (null when it is not there).
    /// </summary>
    public static IReadOnlyDictionary<string, byte[]> Generated(AppManifest manifest, Func<string, byte[]?> read) =>
        new Dictionary<string, byte[]> { [GeneratedPath] = Json(manifest, read, "", manifest.Title ?? manifest.Name) };

    /// <summary>The app's specs as the runtime reads them: the file names in the manifest's order (the first is the home view), and each spec by its file name. A file that does not read is listed under <c>problems</c> with its diagnostics.</summary>
    public static byte[] Json(ResolvedInstance instance) =>
        Json(instance.Manifest, file => instance.Package.Files.TryGetValue(file, out var bytes) ? bytes : null, instance.Instance.Name, instance.Instance.Title ?? instance.Manifest.Title ?? instance.Instance.Name);

    /// <summary>
    /// The same document for a source tree that is not published yet (plan 22a GP34): <c>apps build</c> makes it from the manifest and the spec files and gives it to the bundler as <c>src/views.generated.json</c>, so a code app can
    /// draw its spec files with the same components the host does and the bundle holds what was checked. <paramref name="read"/> answers a file's bytes (null when the package does not hold it).
    /// </summary>
    public static byte[] Json(AppManifest manifest, Func<string, byte[]?> read, string instance, string title)
    {
        var views = new JsonObject();
        var problems = new JsonArray();
        foreach (var file in manifest.Views)
        {
            if (read(file) is not { } bytes) { problems.Add($"{file}: the package does not hold this file."); continue; }
            var loaded = ViewSpecLoader.Load(Encoding.UTF8.GetString(bytes), file, manifest.Name);
            if (loaded.Spec is { } spec && loaded.Ok) views[file] = Spec(spec);
            else foreach (var d in loaded.Diagnostics) problems.Add($"{d.Path}:{d.Line}:{d.Column}: {d.Message}");
        }
        var root = new JsonObject
        {
            ["format"] = ViewSpec.FormatId,
            ["app"] = manifest.Name,
            ["instance"] = instance,
            ["title"] = title,
            ["home"] = manifest.Views.Count > 0 ? manifest.Views[0] : null,
            ["files"] = new JsonArray([.. manifest.Views.Select(f => (JsonNode)f)]),
            ["views"] = views,
            ["problems"] = problems,
        };
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private static JsonNode Spec(ViewSpec spec)
    {
        var o = new JsonObject
        {
            ["kind"] = spec.Kind.ToString().ToLowerInvariant(),
            ["title"] = spec.Title,
            ["model"] = spec.Model,
            ["params"] = new JsonArray([.. spec.Params.Select(p => (JsonNode)new JsonObject
            {
                ["name"] = p.Name, ["type"] = p.Type.ToString().ToLowerInvariant(), ["required"] = p.Required, ["default"] = p.Default,
            })]),
            ["links"] = new JsonArray([.. spec.Links.Select(l => (JsonNode)new JsonObject
            {
                ["label"] = l.Label, ["to"] = l.To, ["row"] = l.PerRow,
                ["with"] = new JsonObject(l.With.Select(w => KeyValuePair.Create(w.Key, (JsonNode?)new JsonObject { ["kind"] = w.Value.Kind.ToString().ToLowerInvariant(), ["text"] = w.Value.Text }))),
            })]),
        };
        static JsonNode Attr(ViewAttribute a) => new JsonObject { ["name"] = a.Name, ["label"] = a.Label };
        switch (spec)
        {
            case GridView g:
                o["entity"] = g.Entity;
                o["columns"] = new JsonArray([.. g.Columns.Select(c => (JsonNode)new JsonObject { ["attribute"] = c.Attribute, ["label"] = c.Label, ["sortable"] = c.Sortable })]);
                o["filters"] = new JsonArray([.. g.Filters.Select(Attr)]);
                o["sort"] = new JsonArray([.. g.Sort.Select(s => (JsonNode)new JsonObject { ["attribute"] = s.Attribute, ["direction"] = s.Direction.ToString().ToLowerInvariant() })]);
                o["pageSize"] = g.PageSize;
                break;
            case FormView f:
                o["entity"] = f.Entity;
                o["mode"] = f.Mode.ToString().ToLowerInvariant();
                o["key"] = f.Key;
                o["submit"] = f.Submit;
                o["fields"] = new JsonArray([.. f.Fields.Select(x => (JsonNode)new JsonObject { ["attribute"] = x.Attribute, ["label"] = x.Label, ["readOnly"] = x.ReadOnly, ["required"] = x.Required, ["help"] = x.Help })]);
                break;
            case DetailView d:
                o["entity"] = d.Entity;
                o["key"] = d.Key;
                o["sections"] = new JsonArray([.. d.Sections.Select(s => (JsonNode)new JsonObject { ["title"] = s.Title, ["attributes"] = new JsonArray([.. s.Attributes.Select(Attr)]) })]);
                break;
            case DashboardView b:
                o["widgets"] = new JsonArray([.. b.Widgets.Select(w => (JsonNode)new JsonObject
                {
                    ["kind"] = w.Kind.ToString().ToLowerInvariant(), ["title"] = w.Title, ["entity"] = w.Entity, ["aggregate"] = w.Aggregate.ToString().ToLowerInvariant(),
                    ["measure"] = w.Measure is { } m ? Attr(m) : null, ["by"] = w.By is { } by ? Attr(by) : null,
                    ["columns"] = new JsonArray([.. w.Columns.Select(Attr)]), ["limit"] = w.Limit,
                })]);
                break;
        }
        return o;
    }
}
