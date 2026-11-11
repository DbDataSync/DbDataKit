using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace DbDataKit.Scripting;

/// <summary>
/// What <c>#load</c> may reach: <c>.csx</c> files inside one root folder (the configuration repository), named relative to the file that loads them. An absolute path, a path that climbs out of the root and a link that points out of it are refused,
/// so a script cannot load a file the repository does not hold. (A lint like the rest of the guard, not a sandbox: a loaded package can read any file.)
/// </summary>
public sealed class RestrictedSourceResolver : SourceReferenceResolver
{
    private readonly string _root;

    public RestrictedSourceResolver(string root) => _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    public override string NormalizePath(string path, string? baseFilePath) => path;

    public override string? ResolveReference(string path, string? baseFilePath)
    {
        if (Path.IsPathRooted(path) || !path.EndsWith(".csx", StringComparison.OrdinalIgnoreCase)) return null;
        var directory = baseFilePath is { Length: > 0 } && Path.IsPathRooted(baseFilePath) ? Path.GetDirectoryName(baseFilePath)! : _root;
        var full = Path.GetFullPath(Path.Combine(directory, path));
        if (!Inside(full) || !File.Exists(full)) return null;
        // a link inside the root that points outside it
        var target = new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true);
        return target is not null && !Inside(target.FullName) ? null : full;
    }

    private bool Inside(string full) => full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    public override Stream OpenRead(string resolvedPath) => File.OpenRead(resolvedPath);

    public override bool Equals(object? other) => other is RestrictedSourceResolver r && r._root == _root;

    public override int GetHashCode() => _root.GetHashCode();
}

/// <summary>Resolves no <c>#r</c> of a file: a package is asked for with <c>#r "nuget:"</c>, which the resolver has already answered, and a file path would reach outside the closure.</summary>
internal sealed class NoFileReferences : MetadataReferenceResolver
{
    public static readonly NoFileReferences Instance = new();

    public override ImmutableArray<PortableExecutableReference> ResolveReference(string reference, string? baseFilePath, MetadataReferenceProperties properties) => [];

    public override bool Equals(object? other) => other is NoFileReferences;

    public override int GetHashCode() => 1;
}
