using DbDataKit.Apps;
using DbDataKit.State;
using DbDataKit.Views.Export;
using DbDataKit.Views.Hosting;

namespace DbDataGateway.Server.Serving;

/// <summary>
/// The apps the Gateway serves, as the views module draws their custom views in an export or a subscription (plan 32 DBR31, DBR32, DBR34): the manifest entry and the built files of an app version the group's definitions serve
/// (<see cref="AppInstanceStore"/>), nothing the apps host's own routes (<c>AppShell</c>, <c>AppSite</c>) do. A custom widget's <c>component</c> (<c>&lt;app&gt;/&lt;Export&gt;</c>) is found in the first instance of that app; a page by the instance and the view's name.
/// <para>
/// <b>Who may open an instance</b> is what the identity home says; the reading rule of an app is any role on it (<c>AppScope.Narrow</c>). The principal a view is exported as carries the person's roles at the home, and <see cref="MayOpen"/> asks for one: a person with
/// none is refused, whatever the page's data would say. (The per-instance role grant the apps host's token exchange checks is the home's; this is the same rule with the roles the principal already has, and a stricter check is the apps host's to add.)
/// </para>
/// </summary>
public sealed class GatewayCustomViewApps(Func<AppInstanceStore> instances) : ICustomViewApps
{
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".js"] = "text/javascript; charset=utf-8", [".mjs"] = "text/javascript; charset=utf-8", [".css"] = "text/css; charset=utf-8", [".json"] = "application/json; charset=utf-8",
        [".svg"] = "image/svg+xml", [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp", [".woff"] = "font/woff", [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf", [".txt"] = "text/plain; charset=utf-8",
    };

    private static (string App, string Export)? Split(string component)
    {
        var slash = component.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 && slash < component.Length - 1 ? (component[..slash], component[(slash + 1)..]) : null;
    }

    private ResolvedInstance? InstanceOf(string app)
    {
        var store = instances();
        var instance = store.List().FirstOrDefault(i => string.Equals(i.App, app, StringComparison.Ordinal));
        return instance is null ? null : store.Resolve(instance.Name);
    }

    public CustomView? Find(string component) =>
        Split(component) is { } c && InstanceOf(c.App) is { } resolved ? resolved.Manifest.CustomViews.FirstOrDefault(v => v.Export == c.Export) : null;

    public CustomAppFiles? Files(string component) =>
        Split(component) is { } c && InstanceOf(c.App) is { } resolved ? FilesOf(resolved) : null;

    public CustomPageApp? FindPage(string instance, string name)
    {
        if (instances().Resolve(instance) is not { } resolved) return null;
        var view = resolved.Manifest.CustomViews.FirstOrDefault(v => v.Name == name && v.Kind == CustomViewKind.Page);
        return view is null || FilesOf(resolved) is not { } files ? null : new CustomPageApp(instance, view, files);
    }

    public bool MayOpen(ViewsPrincipal who, string instance) => who.Caller.Roles.Count > 0 && instances().Get(instance) is not null;

    private static CustomAppFiles? FilesOf(ResolvedInstance resolved)
    {
        if (resolved.Manifest.Entry is not { } entry || !resolved.Package.Files.ContainsKey(entry)) return null;
        var files = resolved.Package.Files.ToDictionary(f => f.Key, f => new PrintAsset(f.Value, Types.GetValueOrDefault(Path.GetExtension(f.Key), "application/octet-stream")), StringComparer.Ordinal);
        return new CustomAppFiles(resolved.Manifest.Name, files, entry);
    }
}
