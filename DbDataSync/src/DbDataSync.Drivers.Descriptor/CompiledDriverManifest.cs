using System.Text;
using DbDataKit.Scripting;
using DbDataSync.Libraries;

namespace DbDataSync.Drivers.Descriptor;

/// <summary>
/// <c>&lt;repo&gt;/drivers/&lt;id&gt;/driver.csx</c> — the compiled-plugin counterpart to <c>driver.yaml</c>: a C# script that asks for its packages with <c>#r "nuget: Id, 1.2.3"</c>
/// (exact versions), may <c>#load</c> helper scripts from the repository, declares what it needs and ends with an expression that is the <see cref="Abstractions.IDriver"/>
/// (<c>new MyDriver()</c>). <see cref="DriverLoader"/> tells the two apart by which file is present. The hashes of the packages are in <c>driver.lock</c> beside it, and
/// <c>lib/</c> (not tracked in git) holds the package store and the compiled script.
/// </summary>
/// <param name="Id">The folder name.</param>
/// <param name="Packages">The packages the script names, in order.</param>
/// <param name="Source">The script text.</param>
public sealed record CompiledDriverManifest(string Id, IReadOnlyList<PackageRef> Packages, string Source)
{
    public const string FileName = "driver.csx";
    public const string LockFileName = "driver.lock";

    /// <summary>The format this replaced: a manifest naming an assembly and a type in a package. Refused, not read.</summary>
    public const string RefusedFileName = "driver.json";

    public static CompiledDriverManifest Read(string path)
    {
        var text = File.ReadAllText(path);
        var id = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path)))!;
        try
        {
            var (requests, _) = ScriptDirectives.Extract(text, exactOnly: true);
            return new CompiledDriverManifest(id, [.. requests.Select(r => new PackageRef(r.Id, r.Range.MinVersion!.ToNormalizedString()))], text);
        }
        catch (ScriptDirectiveException ex)
        {
            throw new InvalidDataException($"'{path}': {ex.Message}", ex);
        }
    }

    /// <summary>A driver that is one package: the script asks for it and ends with <c>new DriverType()</c>.</summary>
    public static CompiledDriverManifest ForPackage(string id, PackageRef package, string driverType) => new(id, [package],
        new StringBuilder()
            .Append("// A DbDataSync driver: the packages it needs (exact versions) and an expression that is the IDriver.\n")
            .Append("#r \"nuget: ").Append(package.Id).Append(", ").Append(package.Version).Append("\"\n")
            .Append("new ").Append(driverType).Append("()\n").ToString());

    public void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, Source);
    }
}
