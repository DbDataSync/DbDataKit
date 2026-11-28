using System.Text.RegularExpressions;

namespace DbDataKit.Sync.Core.Config;

/// <summary>
/// The namespaces Sync's shared libraries had before they were named after their assemblies (<c>DbDataSync.Scripting.Abstractions</c> is now
/// <c>DbDataKit.Sync.Scripting.Abstractions</c>, and so on). A transform script or a <c>driver.csx</c> written for the old names no longer compiles;
/// the set's check, the script compiler and the driver loader name each old <c>using</c> with the name that replaces it.
/// </summary>
public static partial class RenamedNamespaces
{
    /// <summary>Each old namespace and the one that replaces it.</summary>
    public static IReadOnlyDictionary<string, string> Renamed { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["DbDataSync.Core"] = "DbDataKit.Sync.Core",
        ["DbDataSync.Drivers.Abstractions"] = "DbDataKit.Sync.Drivers.Abstractions",
        ["DbDataSync.Drivers.Descriptor"] = "DbDataKit.Sync.Drivers.Descriptor",
        ["DbDataSync.Drivers.Generic"] = "DbDataKit.Sync.Drivers.Generic",
        ["DbDataSync.Provisioning"] = "DbDataKit.Sync.Provisioning",
        ["DbDataSync.Scripting.Abstractions"] = "DbDataKit.Sync.Scripting.Abstractions",
    };

    /// <summary>One <c>using</c> of an old namespace: its line (1-based), the namespace it names and the one to name instead.</summary>
    public sealed record OldUsing(int Line, string Old, string New)
    {
        public override string ToString() => $"line {Line}: `using {Old};` names a namespace that is now `{New}`; change the using.";
    }

    /// <summary>Every <c>using</c> directive of <paramref name="code"/> that names an old namespace or one inside it.</summary>
    public static IReadOnlyList<OldUsing> Find(string code)
    {
        var found = new List<OldUsing>();
        var lines = code.ReplaceLineEndings("\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var match = UsingDirective().Match(lines[i]);
            if (!match.Success) continue;
            var name = match.Groups["name"].Value;
            foreach (var (old, renamed) in Renamed)
                if (name == old || name.StartsWith(old + ".", StringComparison.Ordinal))
                {
                    found.Add(new OldUsing(i + 1, name, renamed + name[old.Length..]));
                    break;
                }
        }
        return found;
    }

    [GeneratedRegex(@"^\s*(?:global\s+)?using\s+(?:static\s+)?(?:\w+\s*=\s*)?(?<name>DbDataSync(?:\.\w+)+)\s*;")]
    private static partial Regex UsingDirective();
}
