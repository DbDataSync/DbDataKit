using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Web.Trust;

namespace DbDataKit.Apps;

/// <summary>
/// One render call of an app's dashboard or report (plan 32 DBR13), checked by the host: the person (the subject of the app's token, with the roles the identity home gave them on the instance), the resolved instance (its manifest and
/// the files of its version), the file under <c>views</c> that holds the document, and the request body (<c>parameters</c>, <c>layout</c>, <c>only</c>, <c>order</c>, <c>expand</c>, <c>viewport</c>, <c>medium</c>).
/// </summary>
public sealed record AppViewsCall(string App, ResolvedInstance Instance, string File, string SubjectId, string? SubjectName, ServerRole Roles, byte[] Body, string? Via = null);

/// <summary>What renders an app's dashboards and reports: in the serving layer, the views library's resolver running the document's queries through the serving core as the person (the same run a published document gets).</summary>
public interface IAppViews
{
    Task<AppResponse> SendAsync(AppViewsCall call, CancellationToken cancellationToken);
}

/// <summary>
/// The views client's route of an app (plan 32 DBR13): <c>GET api/v1/dashboards/&lt;file&gt;</c> and <c>api/v1/reports/&lt;file&gt;</c> give the document's text and version, <c>POST .../render</c> its rendered widgets. <c>&lt;file&gt;</c> is a file
/// of the manifest's <c>views</c> list (percent-encoded, <c>views%2Fsales.yaml</c>, as the shared <c>moduleClient</c> writes a name), so the shared visuals draw an app's document through the client they use for a published one. Nothing here reads the
/// document: the text is the package's, and what is drawn is the serving layer's (<see cref="IAppViews"/>), as the person.
/// </summary>
public static class AppViewsRoute
{
    /// <summary>Reads the path after <c>api/v1/</c>: the collection (<c>dashboards</c> or <c>reports</c>), the file, and whether it is the render call. Null when the path is not this route's.</summary>
    public static (string Kind, string File, bool Render)? Parse(string trimmedPath)
    {
        const string Prefix = "api/v1/";
        if (!trimmedPath.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var rest = trimmedPath[Prefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash < 0) return null;
        var kind = rest[..slash] switch { "dashboards" => "dashboard", "reports" => "report", _ => null };
        if (kind is null) return null;
        var tail = rest[(slash + 1)..];
        var render = tail.EndsWith("/render", StringComparison.Ordinal);
        if (render) tail = tail[..^"/render".Length];
        var file = Uri.UnescapeDataString(tail);
        return file.Length == 0 ? null : (kind, file, render);
    }

    /// <summary>The document the manifest lists as <paramref name="file"/> and the package holds, as text; null when the manifest does not list it, the package does not hold it, or it is a view spec and not a dashboard or a report.</summary>
    public static string? TextOf(ResolvedInstance instance, string file)
    {
        if (!instance.Manifest.Views.Contains(file, StringComparer.Ordinal)) return null;
        if (!instance.Package.Files.TryGetValue(file, out var bytes)) return null;
        var text = Encoding.UTF8.GetString(bytes);
        return ViewSpecLoader.IsDocument(text) ? text : null;
    }

    /// <summary>The answer to a read of the document: <c>{kind, name, version, text}</c>, the version being the text's hash.</summary>
    public static AppResponse Open(string kind, string file, string text) =>
        new(200, "application/json; charset=utf-8", AppDataGateway.Cors, Encoding.UTF8.GetBytes(new JsonObject
        {
            ["kind"] = kind, ["name"] = file, ["version"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant(), ["text"] = text,
        }.ToJsonString()));
}
