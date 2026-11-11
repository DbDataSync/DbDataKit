using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace DbDataKit.Apps;

/// <summary>
/// The app template (plan 22a GP32): a code app on the default library set, with its screens as view-spec/1 files. It is inside the library, so every program that makes an app (the Gateway's <c>apps new</c>, a Workspace server's new
/// app project) starts from the same files.
/// </summary>
public static partial class AppTemplate
{
    private const string Prefix = "apps-templates/app/";

    /// <summary>The template's files by path (relative, with <c>/</c>).</summary>
    public static IReadOnlyDictionary<string, byte[]> Files()
    {
        var assembly = typeof(AppTemplate).Assembly;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var name = resource.Replace('\\', '/');
            if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            files[name[Prefix.Length..]] = memory.ToArray();
        }
        return files.Count > 0 ? files : throw new InvalidOperationException("The app template is not in this program.");
    }

    /// <summary>The template's files with the app's <paramref name="name"/> (and a title made from it) in the manifest; no name leaves the template as it is.</summary>
    public static IReadOnlyDictionary<string, byte[]> Files(string? name, string? title = null)
    {
        var files = Files();
        if (name is not { Length: > 0 }) return files;
        var renamed = new Dictionary<string, byte[]>(files, StringComparer.Ordinal);
        if (renamed.TryGetValue("app.yaml", out var manifest)) renamed["app.yaml"] = Encoding.UTF8.GetBytes(Rename(Encoding.UTF8.GetString(manifest), name, title));
        return renamed;
    }

    public static string Rename(string manifest, string name, string? title = null)
    {
        // a title made from the name is plain text; one a person wrote is quoted, so a colon or a quote in it cannot change the manifest
        var line = title is null ? "title: " + char.ToUpperInvariant(name[0]) + name[1..].Replace('-', ' ').Replace('_', ' ') : "title: " + System.Text.Json.JsonSerializer.Serialize(title);
        manifest = NameLine().Replace(manifest, $"name: {name}");
        return TitleLine().Replace(manifest, line.Replace("$", "$$"));
    }

    [GeneratedRegex(@"^name:.*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex NameLine();

    [GeneratedRegex(@"^title:.*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex TitleLine();
}
