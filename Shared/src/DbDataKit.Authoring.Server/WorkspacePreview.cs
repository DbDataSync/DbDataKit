using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DbDataKit.State;
using Microsoft.AspNetCore.Http;

namespace DbDataKit.Authoring.Server;

/// <summary>
/// The preview of a build (plan 22a GP35, plan 21: previews are read only by default). The smallest mechanism that cannot write: this server answers <c>GET</c> for the files of the build's folder and nothing else, with a
/// <c>Content-Security-Policy</c> that leaves the page no network at all (<c>connect-src 'none'</c>, <c>form-action 'none'</c>, <c>default-src 'none'</c>) and a <c>sandbox</c> directive (an opaque origin: no cookies, no storage, no access to
/// the console that frames it). The entry document comes with its script and style inlined, so the page needs no second request (a sandboxed document has no cookie to send with one, and the relay needs it). What this shows is the
/// app's own screens as built; it is <b>not connected to data</b>: the host's data routes and the semantic route (<c>.../_data/api/v1/semantic/...</c>, built in the apps host, plan 22a GP35c) are not here, so a preview reads nothing and can write nothing.
/// A preview with live data is the apps host's preview channel (plan 21 section 2; plan 22a GP35d, open); until then publishing to a test Gateway is how an app is seen with data.
/// </summary>
public static partial class WorkspacePreview
{
    public const string Policy = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; font-src data:; connect-src 'none'; form-action 'none'; base-uri 'none'; frame-ancestors 'self'; sandbox allow-scripts";

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".js"] = "text/javascript; charset=utf-8", [".css"] = "text/css; charset=utf-8", [".json"] = "application/json; charset=utf-8",
        [".svg"] = "image/svg+xml", [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp", [".ico"] = "image/x-icon",
        [".woff"] = "font/woff", [".woff2"] = "font/woff2", [".ttf"] = "font/ttf", [".txt"] = "text/plain; charset=utf-8", [".map"] = "application/json; charset=utf-8", [".yaml"] = "text/yaml; charset=utf-8",
    };

    public static IResult Serve(WorkspaceBuildRow build, string appFolder, string? path)
    {
        if (build.Status != "succeeded") return Text(409, $"This build is {build.Status}, so there is no app to preview.");
        var relative = WebUtility.UrlDecode(path ?? "").Trim('/');
        if (relative.Length == 0) relative = "index.html";
        if (relative.Split('/').Any(p => p is "" or "." or ".." || p.Contains('\\') || p.Any(char.IsControl))) return Text(400, "That is not a path inside the app.");
        var root = Path.GetFullPath(appFolder);
        var file = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(file) || File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) return Text(404, "No such file in the built app.");

        var bytes = File.ReadAllBytes(file);
        var type = Types.GetValueOrDefault(Path.GetExtension(file), "application/octet-stream");
        if (relative == "index.html") bytes = Encoding.UTF8.GetBytes(Inline(Encoding.UTF8.GetString(bytes), root));
        return new Served(bytes, type);
    }

    private sealed class Served(byte[] body, string type) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = type;
            context.Response.Headers["Content-Security-Policy"] = Policy;
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Cache-Control"] = "no-store";
            await context.Response.Body.WriteAsync(body, context.RequestAborted);
        }
    }

    private static IResult Text(int status, string message) => Results.Text(message, "text/plain; charset=utf-8", statusCode: status);

    [GeneratedRegex(@"<script\s+src=""(?<src>[^"":]+)""\s*>\s*</script>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptTag();

    [GeneratedRegex(@"<link\s+[^>]*?rel=""stylesheet""[^>]*?href=""(?<href>[^"":]+)""[^>]*>|<link\s+[^>]*?href=""(?<href>[^"":]+)""[^>]*?rel=""stylesheet""[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StyleTag();

    /// <summary>The document with its local scripts and stylesheets put into it (what the build has; a reference to anything else is left, and the policy refuses it).</summary>
    public static string Inline(string html, string root)
    {
        string? Read(string reference)
        {
            var relative = reference.TrimStart('.', '/');
            var file = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            return file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(file) ? File.ReadAllText(file) : null;
        }
        html = ScriptTag().Replace(html, m => Read(m.Groups["src"].Value) is { } code ? "<script>" + code.Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase) + "</script>" : m.Value);
        return StyleTag().Replace(html, m => Read(m.Groups["href"].Value) is { } css ? "<style>" + css.Replace("</style", "<\\/style", StringComparison.OrdinalIgnoreCase) + "</style>" : m.Value);
    }
}
