using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Apps;

/// <summary>What <c>app validate</c> checks beyond the manifest text: the files it names, and a built bundle's paths (plan 26 section 3: an app must not assume where it is served).</summary>
public static partial class AppChecks
{
    /// <summary>The entry, view, home and logo files the manifest names that are not in the source folder. A diagnostic names the manifest file; the message names the missing path.</summary>
    public static IReadOnlyList<ConfigDiagnostic> Files(AppManifest manifest, string sourceDirectory, string manifestFile = "app.yaml") =>
        FilesWhere(manifest, path => File.Exists(Path.Combine(sourceDirectory, path.Replace('/', Path.DirectorySeparatorChar))), manifestFile);

    /// <summary><see cref="Files(AppManifest, string, string)"/> over any list of files, <paramref name="exists"/> saying whether the app has a file at a path with <c>/</c> between folders (the files of a package that is not in a folder, as the Gateway checks one sent to it).</summary>
    public static IReadOnlyList<ConfigDiagnostic> FilesWhere(AppManifest manifest, Func<string, bool> exists, string manifestFile = "app.yaml")
    {
        var found = new List<ConfigDiagnostic>();
        var named = manifest.Views.Prepend(manifest.Entry!).Concat(manifest.Portal is { } portal ? portal.Home.Append(portal.Identity.Logo!).Append(portal.Identity.Favicon!) : []);
        foreach (var path in named.Where(p => p is not null))
            if (!exists(path))
                found.Add(DefaultDiagnosticCatalog.Instance.Create(AppManifestLoader.MissingFile, manifestFile, 1, 1, $"The manifest names `{path}`, which is not in the app's folder.", "Create the file, or take it out of the manifest."));
        return found;
    }

    /// <summary>
    /// The portal's home view files (AP12) read and checked: each problem names the home file and its line (<c>DBD-APP-011</c> to <c>013</c>). <paramref name="read"/> gives a file's text by its path in the app, or null when there is none
    /// (that is <see cref="Files"/>' finding, not repeated here).
    /// </summary>
    public static IReadOnlyList<ConfigDiagnostic> HomeViews(AppManifest manifest, Func<string, string?> read)
    {
        var found = new List<ConfigDiagnostic>();
        if (manifest.Portal is not { } portal) return found;
        var mounted = portal.Mounts.Select(m => m.App).ToHashSet(StringComparer.Ordinal);
        foreach (var path in portal.Home)
            if (read(path) is { } text) found.AddRange(HomeViewLoader.Load(text, path, mounted).Diagnostics);
        return found;
    }

    // src="/x", href='/x', url(/x), import "/x": a path from the server's root. "//host/x" is another host's and is left to the content security policy.
    private static readonly Regex Attribute = new(@"\b(?:src|href|action|poster)\s*=\s*[""']/(?!/)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex CssUrl = new(@"url\(\s*[""']?/(?!/)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The built HTML and CSS files that point at a path from the server's root (<c>/assets/app.js</c>), which breaks when the app is served under a subpath. Relative paths (<c>./assets/app.js</c>) are fine.</summary>
    public static IReadOnlyList<ConfigDiagnostic> AbsolutePaths(string outputDirectory)
    {
        var found = new List<ConfigDiagnostic>();
        foreach (var file in Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories).Where(f => f.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".css", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            var text = File.ReadAllText(file);
            var match = Attribute.Match(text);
            if (!match.Success) match = CssUrl.Match(text);
            if (!match.Success) continue;
            var line = text.AsSpan(0, match.Index).Count('\n') + 1;
            found.Add(DefaultDiagnosticCatalog.Instance.Create(AppManifestLoader.AbsoluteAsset, Path.GetRelativePath(outputDirectory, file).Replace('\\', '/'), line, 1,
                "This file points at a path from the server's root, which breaks when the app is served under a subpath.", "Build with relative paths (`./assets/...`)."));
        }
        return found;
    }

    /// <summary>An address of another host in a built file: where, and which origin.</summary>
    public sealed record OutsideUrl(string File, int Line, string Origin);

    // namespaces and documentation links that bundles carry in strings and that nothing fetches
    private static readonly HashSet<string> NotFetched = new(StringComparer.OrdinalIgnoreCase) { "www.w3.org", "w3.org", "react.dev", "reactjs.org", "developer.mozilla.org", "json-schema.org", "schemas.microsoft.com", "purl.org" };
    private static readonly Regex Origin = new(@"\b(?<origin>(?:https?|wss?)://[A-Za-z0-9.-]+(?::\d{1,5})?)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// The addresses of other hosts in the built HTML, CSS and script that the manifest did not ask for (plan 26 section 2b). Advice for the author, so a dependency that phones home is found before a review; the content security policy
    /// is what enforces. Namespace and documentation links that nothing fetches are left out.
    /// </summary>
    public static IReadOnlyList<OutsideUrl> OutsideUrls(string outputDirectory, AppEgress asked)
    {
        var found = new List<OutsideUrl>();
        foreach (var file in Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories).Where(f => f.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".css", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(outputDirectory, file).Replace('\\', '/');
            var text = File.ReadAllText(file);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in Origin.Matches(text))
            {
                var origin = match.Groups["origin"].Value.ToLowerInvariant();
                var host = origin[(origin.IndexOf("://", StringComparison.Ordinal) + 3)..].Split(':')[0];
                if (NotFetched.Contains(host) || !seen.Add(origin) || asked.All.Any(a => EgressPolicy.Covers(a, origin))) continue;
                found.Add(new OutsideUrl(relative, text.AsSpan(0, match.Index).Count('\n') + 1, origin));
            }
        }
        return found;
    }
}
