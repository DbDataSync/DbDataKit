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
            verb="$1 $2"; out=""; root="."; pkg="orders-set"; dest=""; to="runner"; content=0; rows=50; seed=1; limit=20; models=""; third="$3"; col=""
            if [ "$1 $2 $3" = "project create starter" ]; then dest="$4"; mkdir -p "$dest/rendered" "$dest/models"; printf 'schema: dbdatabuild.project/2\ntitle: starter\n' > "$dest/dbdatabuild.yml"; printf 'cron: "*/15 * * * *"\n' > "$dest/rendered/refresh.plan.yaml"; echo "{}"; exit 0; fi
            if [ "$verb" = "project compile" ] || [ "$verb" = "project sample" ]; then shift; shift; fi
            while [ $# -gt 0 ]; do case "$1" in --out) out="$2"; shift;; --project) root="$2"; shift;; --package) pkg="$2"; shift;; --to) to="$2"; shift;; --content) content=1;; --column) col="$2"; shift;; --rows) rows="$2"; shift;; --seed) seed="$2"; shift;; --limit) limit="$2"; shift;; models/*) models="$models $1";; esac; shift; done
            if [ "$verb" = "project compile" ]; then
              if grep -q 'broken' "$root/models/marts/orders.sql" 2>/dev/null; then echo '{"ok":false,"diagnostics":[{"code":"DDB-301","severity":"error","title":"A table is not defined","found":"nowhere","location":{"file":"models/marts/orders.sql","line":1}}],"data":{}}'; exit 1; fi
              if [ "$content" = "1" ]; then echo '{"ok":true,"data":{"files":[{"path":"rendered/sqlserver/orders.sql","hash":"h","content":"-- lowered\nSELECT 1 AS id"}]},"diagnostics":[]}'; exit 0; fi
              mkdir -p "$root/rendered/sqlserver"; printf -- '-- lowered\nSELECT 1 AS id\n' > "$root/rendered/sqlserver/orders.sql"; rm -f "$root/rendered/stale.sql"
              echo '{"ok":true,"data":{"files":[]},"diagnostics":[{"code":"DDB-900","severity":"note","title":"Note","found":"compiled"}]}'; exit 0
            fi
            if [ "$verb" = "project sample" ]; then
              if grep -q 'broken' "$root/models/marts/orders.sql" 2>/dev/null; then echo '{"ok":false,"diagnostics":[{"code":"DDB-301","severity":"error","title":"A table is not defined","found":"nowhere","location":{"file":"models/marts/orders.sql","line":1}}],"data":{}}'; exit 1; fi
              echo "{\"ok\":true,\"data\":{\"seed\":$seed,\"tables\":[{\"name\":\"marts.orders\",\"kind\":\"view\",\"origin\":null,\"columns\":[{\"name\":\"id\",\"type\":\"BIGINT\"}],\"row_count\":$rows,\"rows\":[[\"1\"],[\"2\"]],\"error\":null,\"warnings\":[\"models:$models limit:$limit\"]}]},\"diagnostics\":[]}"; exit 0
            fi
            if [ "$verb" = "project show" ] && [ "$third" = "graph" ]; then
              if grep -q 'broken' "$root/models/marts/orders.sql" 2>/dev/null; then echo '{"ok":false,"exit_code":1,"data":{},"diagnostics":[{"code":"DDB-301","severity":"error","title":"A table is not defined","found":"nowhere","location":{"file":"models/marts/orders.sql","line":1}}],"errors":[]}'; exit 1; fi
              if [ "$col" = "nosuch.x" ]; then echo '{"ok":false,"exit_code":2,"data":{},"diagnostics":[],"errors":["`nosuch` has no column `x` (a model of the project, and one of its declared columns)."]}'; exit 2; fi
              lineage='null'
              if [ -n "$col" ]; then lineage="{\"column\":\"$col\",\"upstream\":[{\"table\":\"staging.orders\",\"column\":\"order_id\",\"distance\":1}],\"downstream\":[]}"; fi
              echo "{\"ok\":true,\"exit_code\":0,\"data\":{\"nodes\":[{\"name\":\"staging.orders\",\"category\":\"model\",\"kind\":\"table\",\"level\":0,\"file\":\"models/staging/orders.sql\",\"connections\":[\"sqlserver\"]},{\"name\":\"marts.orders\",\"category\":\"model\",\"kind\":\"table\",\"level\":1,\"file\":\"models/marts/orders.sql\",\"connections\":[\"sqlserver\"]}],\"edges\":[{\"from\":\"staging.orders\",\"to\":\"marts.orders\"}],\"unknown_tables\":[],\"column_edges\":[{\"from_table\":\"staging.orders\",\"from_column\":\"order_id\",\"to_model\":\"marts.orders\",\"to_column\":\"order_id\",\"transform\":\"direct\"}],\"column_lineage\":$lineage,\"diagram\":null},\"diagnostics\":[],\"errors\":[]}"; exit 0
            fi
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
