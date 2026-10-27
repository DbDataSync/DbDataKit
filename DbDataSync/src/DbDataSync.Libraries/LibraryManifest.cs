using System.Text;
using System.Text.RegularExpressions;
using DbDataKit.Scripting;

namespace DbDataSync.Libraries;

/// <param name="Id">The name a consumer asks <see cref="LibraryRegistry.GetFactory"/> for, and the
/// directory name under <c>&lt;repo&gt;/libraries/</c>. Conventionally the primary NuGet package id
/// (<c>MySqlConnector</c>, <c>Microsoft.Data.SqlClient</c>).</param>
/// <param name="FactoryType">An assembly-qualified type name — <c>"MySqlConnector.MySqlConnectorFactory,
/// MySqlConnector"</c> — passed to <see cref="System.Data.Common.DbProviderFactories.RegisterFactory(string,string)"/>
/// verbatim. Empty when the file does not name one; the registry then looks for the one factory the packages
/// carry.</param>
/// <param name="Packages">A list, not a single id: a library can span assemblies or ship native-asset
/// packages that are not transitive dependencies of the primary one (Oracle, DB2).</param>
/// <remarks>
/// A library is a <c>library.csx</c> script that holds only <c>#r "nuget: Id, 1.2.3"</c> lines (exact versions) and, optionally, one
/// <c>// factory: Type, Assembly</c> comment naming the <see cref="System.Data.Common.DbProviderFactory"/>. The hashes of what it resolves to are in
/// <c>library.lock</c> beside it.
/// </remarks>
public sealed record LibraryManifest(string Id, string FactoryType, IReadOnlyList<PackageRef> Packages)
{
    private static readonly Regex FactoryLine = new(@"^//\s*factory:\s*(?<type>\S.*?)\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Reads a <c>library.csx</c>; the library's id is the name of the folder it is in.</summary>
    public static LibraryManifest Read(string path)
    {
        var text = File.ReadAllText(path);
        var id = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path)))!;
        IReadOnlyList<PackageRequest> requests;
        string remaining;
        try { (requests, remaining) = ScriptDirectives.Extract(text, exactOnly: true); }
        catch (ScriptDirectiveException ex) { throw new InvalidDataException($"'{path}': {ex.Message}", ex); }

        string factory = "";
        foreach (var raw in remaining.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (FactoryLine.Match(line) is { Success: true } match) { factory = match.Groups["type"].Value; continue; }
            if (line.StartsWith("//", StringComparison.Ordinal)) continue;
            throw new InvalidDataException($"'{path}' holds more than #r \"nuget:\" lines: a library is only the packages it needs (found: {(line.Length > 60 ? line[..60] + "…" : line)}).");
        }
        if (factory.Length > 0) LibraryValidation.ValidateFactoryType(factory);
        if (requests.Count == 0) throw new InvalidDataException($"'{path}' asks for no package: add a line such as #r \"nuget: {id}, 1.2.3\".");
        return new LibraryManifest(id, factory, [.. requests.Select(r => new PackageRef(r.Id, r.Range.MinVersion!.ToNormalizedString()))]);
    }

    public string ToScript()
    {
        var text = new StringBuilder();
        text.Append("// A DbDataSync library: the packages it needs, with exact versions. `library install` writes this file and library.lock beside it;\n");
        text.Append("// after a hand edit, `library sync` restores what it names.\n");
        if (FactoryType.Length > 0) text.Append("// factory: ").Append(FactoryType).Append('\n');
        foreach (var package in Packages) text.Append("#r \"nuget: ").Append(package.Id).Append(", ").Append(package.Version).Append("\"\n");
        return text.ToString();
    }

    public void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, ToScript());
    }
}

/// <param name="Id">The NuGet package id.</param>
/// <param name="Version">Pinned exactly — <c>library sync</c> restores this version, not "latest".</param>
public sealed record PackageRef(string Id, string Version);
