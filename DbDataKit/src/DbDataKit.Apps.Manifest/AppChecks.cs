using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Apps;

/// <summary>What <c>app validate</c> checks beyond the manifest text: the files it names, and a built bundle's paths (plan 26 section 3: an app must not assume where it is served).</summary>
public static class AppChecks
{
    /// <summary>The entry and view files the manifest names that are not in the source folder. A diagnostic names the manifest file; the message names the missing path.</summary>
    public static IReadOnlyList<ConfigDiagnostic> Files(AppManifest manifest, string sourceDirectory, string manifestFile = "app.yaml")
    {
        var found = new List<ConfigDiagnostic>();
        foreach (var path in manifest.Views.Prepend(manifest.Entry!).Where(p => p is not null))
            if (!File.Exists(Path.Combine(sourceDirectory, path.Replace('/', Path.DirectorySeparatorChar))))
                found.Add(DefaultDiagnosticCatalog.Instance.Create(AppManifestLoader.MissingFile, manifestFile, 1, 1, $"The manifest names `{path}`, which is not in the app's folder.", "Create the file, or take it out of the manifest."));
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
}
