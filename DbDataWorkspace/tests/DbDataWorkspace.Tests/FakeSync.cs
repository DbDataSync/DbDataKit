using System.Text;
using DbDataKit.Runner;
using DbDataKit.Scheduling;
using DbDataKit.State;

namespace DbDataWorkspace.Tests;

/// <summary>
/// A stand-in <c>dbdatasync</c> (a shell script) that answers the one verb the Workspace runs, <c>package publish --to runner</c>, the way Sync's does (plan 28c E08) without the engine: <c>--check</c> reads the repository, refuses a mapping whose column cache is empty in
/// Sync's words (<c>path: message</c> on the error output, exit 1) and otherwise prints the one JSON line; <c>--out</c> writes <c>package.yaml</c> (a real <c>package/1</c> manifest the tests make with the manifest's own writer) and <c>files/repo/config</c>.
/// It refuses to run when a marker variable of the server's environment reaches it, which is how the tests see that the child's environment is cleared. The real program is the test of <c>DBDATAKIT_TEST_DBDATASYNC</c>.
/// </summary>
internal static class FakeSync
{
    public const string Package = "orders-set";

    public static string Manifest(string package = Package) => PackageManifestWriter.Write(new PackageManifest(package, null, new PackageLaunch(LaunchKind.Assembly, Assembly: "x.dll"), new BuiltAgainst("10.0", null, "dbdatakit.runner.v1"),
        [new PackageTask("orders", new TaskEntry("T"), Schedule.Interval(TimeSpan.FromMinutes(15)), [], [], [], ProgressDeclaration.Empty, TaskLimits.None, [], [])]));

    /// <summary>The script in a new folder, or null where there is no shell to run it (Windows).</summary>
    public static string? Create()
    {
        if (OperatingSystem.IsWindows()) return null;
        var folder = Directory.CreateTempSubdirectory("ddw-fake-sync-").FullName;
        var path = Path.Combine(folder, "dbdatasync");
        var script = """
            #!/bin/bash
            out=""; root=""; pkg="orders-set"
            echo "$@" >> "$(dirname "$0")/arguments.txt"
            verb="$1 $2"; word="$3"
            while [ $# -gt 0 ]; do case "$1" in --out) out="$2"; shift;; --repo) root="$2"; shift;; --package) pkg="$2"; shift;; esac; shift; done
            if [ -n "$DBDATAKIT_TEST_MARKER" ]; then echo "$root/config/replications/orders/task.yaml: the environment of the server reached the program" >&2; exit 1; fi
            if [ "$verb" = "script list" ]; then
              names=""; sep=""
              for f in "$root"/config/scripts/*.yaml; do [ -e "$f" ] || continue; n=$(basename "$f" .yaml); names="$names$sep{\"name\":\"$n\",\"kind\":\"sqlColumnExpression\",\"language\":\"CSharp\",\"error\":null}"; sep=","; done
              echo "{\"scripts\":[$names]}"; exit 0
            fi
            if [ "$verb" = "script test" ]; then
              code="$root/config/scripts/$word.csx"
              if [ ! -e "$code" ]; then echo "{\"name\":\"$word\",\"kind\":\"\",\"ok\":false,\"source\":\"\",\"cases\":[],\"log\":[],\"error\":\"Script '$word' does not exist in $root/config/scripts.\"}"; exit 1; fi
              if grep -q 'not C#' "$code"; then echo "{\"name\":\"$word\",\"kind\":\"sqlColumnExpression\",\"ok\":false,\"source\":\"generated sample\",\"cases\":[],\"log\":[],\"error\":\"Script '$word' did not compile: (1,1) not C#\"}"; exit 1; fi
              echo "{\"name\":\"$word\",\"kind\":\"sqlColumnExpression\",\"ok\":true,\"source\":\"generated sample, ANSI quoting\",\"cases\":[{\"input\":\"Name -> Name (nvarchar(50))\",\"output\":\"UPPER(\\\"Name\\\")\",\"note\":null}],\"log\":[],\"error\":null}"; exit 0
            fi
            bad=$(grep -rl 'sourceColumns: \[\]' "$root/config/replications" 2>/dev/null | head -1)
            if [ -n "$bad" ]; then echo "$bad: The metadata for the table mapping 'orders' (source) has not been cached; refresh the mapping's columns first." >&2; echo "Nothing was sent." >&2; exit 1; fi
            files=$(find "$root/config" -type f | wc -l)
            if [ -n "$out" ]; then
              mkdir -p "$out/files/repo"; cp -r "$root/config" "$out/files/repo/config"
              cat > "$out/package.yaml" <<'MANIFEST'
            @MANIFEST@
            MANIFEST
            fi
            echo "{\"package\":\"$pkg\",\"mode\":\"check\",\"files\":$files,\"replications\":[{\"replication\":\"orders\",\"task\":\"orders\"}]}"
            """.Replace("@MANIFEST@", Manifest().TrimEnd().ReplaceLineEndings("\n"));
        File.WriteAllText(path, script.ReplaceLineEndings("\n") + "\n", new UTF8Encoding(false));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
