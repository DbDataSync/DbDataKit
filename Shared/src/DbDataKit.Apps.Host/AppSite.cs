using DbDataKit.State;
using System.Net;
using System.Text;

namespace DbDataKit.Apps;

/// <summary>What the host sends for one request: a status, the headers an app's document needs, and the body.</summary>
public sealed record AppResponse(int Status, string ContentType, IReadOnlyDictionary<string, string> Headers, byte[] Body);

/// <summary>
/// Serves an app instance's files by subpath (plan 26, AP05a): <c>/apps/&lt;instance&gt;/_app/</c> is the entry document, a path with a file extension is that file of the stored package, and any other path is a route of the app
/// and gets the entry document too (the app's own router reads it). Every answer carries the Content-Security-Policy built from what the administrator approved of the manifest's egress requests (<see cref="EgressPolicy"/>),
/// and an app whose origin is <c>sandbox</c> gets the <c>sandbox</c> directive, which makes its document an opaque origin that cannot read the host's cookies or storage.
/// </summary>
public sealed class AppSite(AppInstanceStore instances, Func<string, AppEgress>? approvedEgress = null, string channel = DefinitionStore.Production, ViewsRuntime? views = null, IViewDocumentChecker? documents = null)
{
    public const string BasePath = "/apps/";

    /// <summary>The folder under an instance that holds its bundle; the instance's own address is the shell page that frames it.</summary>
    public const string BundleFolder = "_app/";

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".js"] = "text/javascript; charset=utf-8", [".mjs"] = "text/javascript; charset=utf-8", [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8", [".svg"] = "image/svg+xml", [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif",
        [".webp"] = "image/webp", [".ico"] = "image/x-icon", [".woff"] = "font/woff", [".woff2"] = "font/woff2", [".ttf"] = "font/ttf", [".txt"] = "text/plain; charset=utf-8",
        [".map"] = "application/json; charset=utf-8", [".yaml"] = "text/yaml; charset=utf-8", [".wasm"] = "application/wasm",
    };

    /// <summary>The answer for <paramref name="subpath"/> (what follows <c>/apps/&lt;instance&gt;/_app/</c>, without a query string).</summary>
    public AppResponse Get(string instanceName, string subpath) => Get(instanceName, subpath, hostOrigin: false);

    /// <summary>
    /// The entry document of an approved trusted portal, served as the page at the portal's own address, on the host's origin (plan 26 section 4a, AP16): no <c>sandbox</c> directive, and its frames may be this host's own pages
    /// (the apps it mounts). The caller has checked the approval; the document's other files are the ordinary bundle.
    /// </summary>
    public AppResponse PortalPage(string instanceName) => Get(instanceName, "", hostOrigin: true);

    private AppResponse Get(string instanceName, string subpath, bool hostOrigin)
    {
        var instance = instances.Resolve(instanceName, channel);
        if (instance is null) return Text(404, "No such app.");
        var entry = instance.Manifest.Entry;
        // a portal is declared data and has no page of its own here, but its files (logo, favicon, the images of its home view) are served from its bundle (AP30)
        var portalFile = entry is null && instance.Manifest.Kind == AppKind.Portal && Path.HasExtension(WebUtility.UrlDecode(subpath).Trim('/'));
        if (entry is null && !portalFile) return instance.Manifest.Views.Count == 0 ? Text(404, "This app has no page.") : ViewsGet(instance, instanceName, subpath, hostOrigin);

        var path = WebUtility.UrlDecode(subpath).Trim('/');
        if (path.Length > 0 && !AppPackage.IsPlain(path)) return Text(400, "That is not a path inside the app.");

        var files = instance.Package.Files;
        var isFile = path.Length > 0 && Path.HasExtension(path);
        if (isFile && !files.ContainsKey(path)) return Text(404, "No such file.");
        var name = isFile ? path : entry ?? "";
        if (!files.TryGetValue(name, out var bytes)) return Text(404, $"The package does not contain its entry '{entry}'.");

        var contentType = Types.GetValueOrDefault(Path.GetExtension(name), "application/octet-stream");
        var document = contentType.StartsWith("text/html", StringComparison.Ordinal);
        if (document) bytes = WithBase(bytes, BasePath + Uri.EscapeDataString(instanceName) + "/" + BundleFolder);

        var egress = EgressPolicy.Effective(instance.Manifest.Egress ?? AppEgress.None, approvedEgress?.Invoke(instanceName) ?? AppEgress.None);
        var headers = new Dictionary<string, string>
        {
            ["Content-Security-Policy"] = EgressPolicy.Build(egress, sandboxed: !hostOrigin && instance.Manifest.Origin == AppOrigin.Sandbox, framesSelf: hostOrigin),
            ["X-Content-Type-Options"] = "nosniff",
            ["Referrer-Policy"] = "no-referrer",
            ["Cache-Control"] = "no-cache",
            ["ETag"] = $"\"{instance.AppVersion.Hash}-{(document ? "d" : "f")}-{name.GetHashCode():x}\"",
        };
        return new AppResponse(200, contentType, headers, bytes);
    }

    /// <summary>
    /// An app of views only (AP25): the generated document, <c>views.json</c> (its specs) and the host's runtime. Served with the policy of any app of the instance, so the page is sandboxed and reaches only the host. A host without the runtime
    /// says so (503); a path that is none of these is a route of the app and gets the document, as for a code app.
    /// </summary>
    private AppResponse ViewsGet(ResolvedInstance instance, string instanceName, string subpath, bool hostOrigin)
    {
        if (views is null) return Text(503, "This host has no runtime for apps of views: the Gateway was published without Shared/web/view-app (build it with `npm run build -w @dbdatakit/view-app`).");
        var path = WebUtility.UrlDecode(subpath).Trim('/');
        if (path.Length > 0 && !AppPackage.IsPlain(path)) return Text(400, "That is not a path inside the app.");
        (byte[] Body, string Type)? served = path switch
        {
            ViewsRuntime.ScriptName => (views.Script, Types[".js"]),
            ViewsRuntime.StyleName when views.Style is not null => (views.Style, Types[".css"]),
            ViewsApp.ViewsFile => (ViewsApp.Json(instance, FrameOrigins(instance, instanceName), documents), Types[".json"]),
            _ when path.Length > 0 && Path.HasExtension(path) => null,
            _ => (ViewsApp.Document(instance.Instance.Title ?? instance.Manifest.Title ?? instanceName, BasePath + Uri.EscapeDataString(instanceName) + "/" + BundleFolder, views.Style is not null, ViewsApp.Json(instance, FrameOrigins(instance, instanceName), documents)), Types[".html"]),
        };
        if (served is not { } file) return Text(404, "No such file.");
        var egress = EgressPolicy.Effective(instance.Manifest.Egress ?? AppEgress.None, approvedEgress?.Invoke(instanceName) ?? AppEgress.None);
        var headers = new Dictionary<string, string>
        {
            ["Content-Security-Policy"] = EgressPolicy.Build(egress, sandboxed: !hostOrigin && instance.Manifest.Origin == AppOrigin.Sandbox, framesSelf: hostOrigin),
            ["X-Content-Type-Options"] = "nosniff",
            ["Referrer-Policy"] = "no-referrer",
            ["Cache-Control"] = "no-cache",
            ["ETag"] = $"\"{instance.AppVersion.Hash}-{views.Hash}-v-{(path.Length > 0 && Path.HasExtension(path) ? path : "doc")}\"",
        };
        return new AppResponse(200, file.Type, headers, file.Body);
    }

    /// <summary>The origins an app's embed widgets may frame: what an administrator approved of the manifest's <c>egress.frame</c>, which is also the document's <c>frame-src</c> (plan 26 AP17).</summary>
    private IReadOnlyList<string> FrameOrigins(ResolvedInstance instance, string instanceName) =>
        EgressPolicy.Effective(instance.Manifest.Egress ?? AppEgress.None, approvedEgress?.Invoke(instanceName) ?? AppEgress.None).Frame;

    /// <summary>Puts <c>&lt;base href&gt;</c> first in the head so relative links in the bundle resolve under the instance's path; a document that already has one is left alone.</summary>
    public static byte[] WithBase(byte[] html, string href)
    {
        var text = Encoding.UTF8.GetString(html);
        if (text.Contains("<base ", StringComparison.OrdinalIgnoreCase)) return html;
        var at = text.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return html;
        at += "<head>".Length;
        return Encoding.UTF8.GetBytes(text.Insert(at, $"<base href=\"{WebUtility.HtmlEncode(href)}\">"));
    }

    private static AppResponse Text(int status, string message) =>
        new(status, "text/plain; charset=utf-8", new Dictionary<string, string> { ["Cache-Control"] = "no-store", ["X-Content-Type-Options"] = "nosniff" }, Encoding.UTF8.GetBytes(message));
}
