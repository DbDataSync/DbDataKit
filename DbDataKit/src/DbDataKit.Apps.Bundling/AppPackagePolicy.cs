using System.Text.Json;
using System.Text.RegularExpressions;

namespace DbDataKit.Apps.Bundling;

/// <summary>One thing the policy refuses, with where it is (a file and line of the source) and why.</summary>
public sealed record AppPackageViolation(string File, int Line, string Package, string Message);

/// <summary>
/// Which packages an app's source may import (plan 22a GP33, QG12): the store's library (the default set), plus the packages the administrator allowed (<c>apps.allowedPackages</c>) that the store holds. The check reads the source's
/// <b>literal</b> specifiers (<c>import</c>, <c>export ... from</c>, <c>import()</c>, <c>require()</c>, CSS <c>@import</c>) and the <c>dependencies</c> of its <c>package.json</c>; a package that is on neither list is refused
/// <b>by name</b>, with the file and line, before anything is built. It is a policy and a courtesy, not the barrier: nothing is installed in any case (the build's <c>node_modules</c> is a copy of the store), so a package that is not in the
/// store cannot resolve. What the check adds is that a package the store holds only as another package's dependency (<c>vega</c>, <c>scheduler</c>) is not something an app may import on its own, and the refusal says why.
/// </summary>
public static partial class AppPackagePolicy
{
    private static readonly string[] SourceExtensions = [".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs", ".css"];

    /// <summary>The names an app may import: the library, and the allowed packages the store holds.</summary>
    public static IReadOnlySet<string> Importable(VendoredPackageStore store, IEnumerable<string>? allowed)
    {
        var names = new HashSet<string>(store.Library, StringComparer.Ordinal);
        foreach (var name in allowed ?? [])
            if (PackageName().IsMatch(name) && store.Packages.ContainsKey(name)) names.Add(name);
        return names;
    }

    /// <summary>Checks the source folder. Empty when every import is allowed.</summary>
    public static IReadOnlyList<AppPackageViolation> Check(string sourceDirectory, VendoredPackageStore store, IEnumerable<string>? allowed)
    {
        var allow = allowed?.ToList() ?? [];
        var importable = Importable(store, allow);
        var violations = new List<AppPackageViolation>();
        var root = Path.GetFullPath(sourceDirectory);

        void Refuse(string file, int line, string package, string specifier)
        {
            var why = allow.Contains(package, StringComparer.Ordinal) && !store.Packages.ContainsKey(package)
                ? $"which is on the allow-list (apps.allowedPackages) but is not in the vendored package store; ask the administrator to add it to the store"
                : $"which is not in the default library set ({string.Join(", ", store.Library)}) and not on the administrator's allow-list (apps.allowedPackages)";
            violations.Add(new AppPackageViolation(file, line, package, $"{file}{(line > 0 ? ":" + line : "")}: imports '{specifier}', {why}."));
        }

        foreach (var path in Files(root))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                var text = lines[i].TrimStart();
                if (text.StartsWith("//", StringComparison.Ordinal) || text.StartsWith('*') || text.StartsWith("/*", StringComparison.Ordinal)) continue;
                foreach (var specifier in Specifiers(lines[i], relative.EndsWith(".css", StringComparison.Ordinal)))
                {
                    var outside = Reaches(specifier, relative, root);
                    if (outside is not null) { violations.Add(new AppPackageViolation(relative, i + 1, specifier, $"{relative}:{i + 1}: imports '{specifier}', which {outside}.")); continue; }
                    if (Package(specifier) is { } package && !importable.Contains(package)) Refuse(relative, i + 1, package, specifier);
                }
            }
        }

        var manifest = Path.Combine(root, "package.json");
        if (File.Exists(manifest))
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifest));
                foreach (var section in new[] { "dependencies", "optionalDependencies" })
                    if (document.RootElement.TryGetProperty(section, out var listed) && listed.ValueKind == JsonValueKind.Object)
                        foreach (var dependency in listed.EnumerateObject())
                            if (!importable.Contains(dependency.Name)) Refuse("package.json", 0, dependency.Name, dependency.Name);
            }
            catch (JsonException) { /* the bundler reports a package.json that does not read */ }
        return violations.DistinctBy(v => v.Message).ToList();
    }

    private static IEnumerable<string> Files(string root)
    {
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(entry);
                if (name.StartsWith('.') || name == "node_modules") continue;
                if (Directory.Exists(entry)) pending.Push(entry);
                else if (SourceExtensions.Contains(Path.GetExtension(entry), StringComparer.OrdinalIgnoreCase)) yield return entry;
            }
        }
    }

    /// <summary>The literal specifiers on one line.</summary>
    private static IEnumerable<string> Specifiers(string line, bool css)
    {
        foreach (Match m in (css ? CssImport() : ScriptImport()).Matches(line)) yield return m.Groups["s"].Value;
    }

    /// <summary>Why a relative or absolute specifier is refused (null when it is a plain package name or a relative path inside the source).</summary>
    private static string? Reaches(string specifier, string file, string root)
    {
        if (specifier.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || specifier.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || specifier.StartsWith("//", StringComparison.Ordinal))
            return "is an address on the network; a build reaches none (vendor the code instead)";
        if (specifier.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || specifier.StartsWith("node:", StringComparison.Ordinal) || specifier.StartsWith("bun:", StringComparison.Ordinal) || specifier.StartsWith('#')) return null;
        if (specifier.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || specifier.StartsWith('/') || specifier.StartsWith('\\') || Regex.IsMatch(specifier, @"^[A-Za-z]:[\\/]"))
            return "is a path outside the app's source folder";
        if (!specifier.StartsWith("./", StringComparison.Ordinal) && !specifier.StartsWith("../", StringComparison.Ordinal) && specifier != "." && specifier != "..") return null;
        if (specifier.Replace('\\', '/').Split('/').Contains("node_modules")) return "reaches into node_modules by path; import the package by its name";
        var target = Path.GetFullPath(Path.Combine(root, Path.GetDirectoryName(file.Replace('/', Path.DirectorySeparatorChar)) ?? "", specifier.Replace('/', Path.DirectorySeparatorChar)));
        return target == root || target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? null : "is a path outside the app's source folder";
    }

    /// <summary>The package a bare specifier names (<c>@scope/name</c> or <c>name</c>), or null for a path, an address or a builtin.</summary>
    private static string? Package(string specifier)
    {
        if (specifier.StartsWith('.') || specifier.StartsWith('/') || specifier.StartsWith('#') || specifier.Contains(':')) return null;
        var parts = specifier.Split('/');
        return specifier.StartsWith('@') ? (parts.Length >= 2 ? parts[0] + "/" + parts[1] : specifier) : parts[0];
    }

    [GeneratedRegex(@"(?:\bfrom\s*|\bimport\s*\(\s*|\brequire\s*\(\s*|\bimport\s+)(?<q>['""])(?<s>[^'""\r\n]+)\k<q>")]
    private static partial Regex ScriptImport();

    [GeneratedRegex(@"@import\s+(?:url\(\s*)?(?<q>['""]?)(?<s>[^'"")\s;]+)\k<q>")]
    private static partial Regex CssImport();

    [GeneratedRegex(@"^(@[a-z0-9][a-z0-9._-]*/)?[a-z0-9][a-z0-9._-]*$")]
    private static partial Regex PackageName();
}
