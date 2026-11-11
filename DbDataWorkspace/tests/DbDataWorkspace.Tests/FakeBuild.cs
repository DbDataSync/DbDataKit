using System.Text;

namespace DbDataWorkspace.Tests;

/// <summary>
/// A stand-in <c>dbdatabuild</c> (a shell script) for the verbs the Workspace runs: <c>project create starter &lt;folder&gt;</c>, <c>project show metadata</c> (the check) and <c>project publish --to runner --out</c> (plan 28c E07), the way Build
/// answers them without the engines: the project is a folder with <c>dbdatabuild.yml</c> and a committed <c>rendered/refresh.plan.yaml</c>; a plan whose cron says <c>bad</c> is refused in Build's way (the file and the key, on the error output, exit 1).
/// The real program is the test of <c>DBDATAKIT_TEST_DBDATABUILD</c> (<see cref="WorkspaceBuildKindTests"/>).
/// </summary>
internal static class FakeBuild
{
    public static string? Create()
    {
        if (OperatingSystem.IsWindows()) return null;
        var folder = Directory.CreateTempSubdirectory("ddw-fake-build-").FullName;
        var path = Path.Combine(folder, "dbdatabuild");
        var script = """
            #!/bin/bash
            verb="$1 $2"; out=""; root="."; pkg="orders-set"; dest=""; to="runner"
            if [ "$1 $2 $3" = "project create starter" ]; then dest="$4"; mkdir -p "$dest/rendered" "$dest/models"; printf 'schema: dbdatabuild.project/2\ntitle: starter\n' > "$dest/dbdatabuild.yml"; printf 'cron: "*/15 * * * *"\n' > "$dest/rendered/refresh.plan.yaml"; echo "{}"; exit 0; fi
            while [ $# -gt 0 ]; do case "$1" in --out) out="$2"; shift;; --project) root="$2"; shift;; --package) pkg="$2"; shift;; --to) to="$2"; shift;; esac; shift; done
            if [ "$verb" = "project show" ]; then
              if grep -q 'garbage' "$root/dbdatabuild.yml"; then echo '{"diagnostics":[{"code":"DDB-101","severity":"error","title":"The project file does not parse","found":"garbage","location":{"file":"dbdatabuild.yml","line":3}}]}'; exit 1; fi
              echo '{"diagnostics":[]}'; exit 0
            fi
            if [ "$verb" = "project publish" ] && [ "$to" = "gateway" ]; then
              set -- "$root"/semantic/*.yml
              if [ ! -e "$1" ]; then echo "The project has no semantic model (semantic/*.yml), so there is nothing for a Gateway to serve. Nothing was written." >&2; exit 1; fi
              mkdir -p "$out/models" "$out/data-sources"; names=""; sep=""
              for f in "$@"; do n=$(basename "$f" .yml); cp "$f" "$out/models/$n.catalog.json"; names="$names$sep{\"name\":\"$n\",\"file\":\"models/$n.catalog.json\",\"sources\":{\"default\":\"warehouse\"},\"data_sources\":[\"warehouse\"]}"; sep=","; done
              printf 'schema: connection/2\nname: warehouse\nengine: postgres\naccess: readonly\n' > "$out/data-sources/warehouse.yaml"
              echo "{\"schema\":\"gateway-publish/1\",\"models\":[$names],\"data_sources\":[{\"name\":\"warehouse\",\"engine\":\"postgres\",\"file\":\"data-sources/warehouse.yaml\",\"access\":\"readonly\"}]}" > "$out/gateway.json"
              echo "{\"data\":{\"models\":[],\"data_sources\":[\"warehouse\"]}}"; exit 0
            fi
            if [ "$verb" = "project publish" ]; then
              if grep -q 'cron: bad' "$root/rendered/refresh.plan.yaml"; then echo "rendered/refresh.plan.yaml: schedule.cron: 'bad' is not a cron expression" >&2; echo "Nothing was sent." >&2; exit 1; fi
              mkdir -p "$out/files/rendered"; cp -r "$root/rendered/." "$out/files/rendered/"
              cat > "$out/package.yaml" <<'MANIFEST'
            @MANIFEST@
            MANIFEST
              echo "{\"data\":{\"package\":\"$pkg\",\"files\":1,\"tasks\":[{\"name\":\"orders\",\"plan\":\"refresh\"}]}}"
              exit 0
            fi
            echo "unknown verb $verb" >&2; exit 2
            """.Replace("@MANIFEST@", FakeSync.Manifest().TrimEnd().ReplaceLineEndings("\n"));
        File.WriteAllText(path, script.ReplaceLineEndings("\n") + "\n", new UTF8Encoding(false));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
