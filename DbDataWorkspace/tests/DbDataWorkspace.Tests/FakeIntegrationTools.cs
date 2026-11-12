using System.Text;

namespace DbDataWorkspace.Tests;

/// <summary>
/// Stand-ins for the two programs the <c>integration</c> kind runs (plan 27a IX20), shell scripts that answer the way the real ones do, so the Workspace's tests need neither a Runner build nor an SDK. <see cref="Runner"/> is
/// <c>dbdatarunner package build|test</c> with <c>--format json</c>: a source file holding <c>BROKEN</c> does not compile (finding with file and line), a test file holding <c>FAILING</c> fails, otherwise <c>build --out</c> writes
/// <c>package.yaml</c> (the project's own) and <c>files/bin/&lt;name&gt;.dll</c>. <see cref="Dotnet"/> is <c>dotnet --list-sdks</c> and <c>dotnet publish -o</c>. The real tool is the test of <c>DBDATAKIT_TEST_DBDATARUNNER</c>.
/// </summary>
internal static class FakeIntegrationTools
{
    private static string Write(string name, string script)
    {
        var folder = Directory.CreateTempSubdirectory("ddw-fake-int-").FullName;
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, script.ReplaceLineEndings("\n") + "\n", new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>The stand-in <c>dbdatarunner</c>, or null where there is no shell (Windows). It writes the arguments it was given to <c>arguments.txt</c> beside itself.</summary>
    public static string? Runner()
    {
        if (OperatingSystem.IsWindows()) return null;
        return Write("dbdatarunner", """
            #!/bin/bash
            echo "$@" >> "$(dirname "$0")/arguments.txt"
            verb="$2"; root="$3"; out=""
            shift 3
            while [ $# -gt 0 ]; do case "$1" in --out) out="$2"; shift;; esac; shift; done
            if [ -n "$DBDATAKIT_TEST_MARKER" ]; then echo '{"ok":false,"data":{},"messages":[],"errors":["the environment of the server reached the program"]}'; exit 1; fi
            name=$(grep -m1 '^name:' "$root/package.yaml" | sed 's/name: *//')
            if [ "$verb" = "test" ]; then
              bad=$(grep -rl 'FAILING' "$root/tests" 2>/dev/null | head -1)
              if [ -n "$bad" ]; then
                rel="${bad#$root/}"
                echo "{\"ok\":false,\"data\":{\"tests\":1,\"failed\":1,\"results\":[{\"file\":\"$rel\",\"passed\":false,\"message\":\"The values differ. Expected 1, got 2.\"}],\"diagnostics\":[]},\"messages\":[],\"errors\":[\"FAILED $rel: The values differ. Expected 1, got 2.\",\"1 of 1 test(s) failed.\"]}"; exit 1
              fi
              echo '{"ok":true,"data":{"tests":1,"failed":0,"results":[{"file":"tests/hello.csx","passed":true}],"diagnostics":[]},"messages":["passed tests/hello.csx","1 test(s) passed."],"errors":[]}'; exit 0
            fi
            bad=$(grep -rln 'BROKEN' "$root/src" 2>/dev/null | head -1)
            if [ -n "$bad" ]; then
              rel="${bad#$root/}"
              echo "{\"ok\":false,\"data\":{\"diagnostics\":[{\"severity\":\"error\",\"code\":\"DBD-INT-CS1002\",\"message\":\"; expected\",\"file\":\"$rel\",\"line\":3}]},\"messages\":[],\"errors\":[\"$rel:3: ; expected\",\"The code does not compile (1 error(s)).\"]}"; exit 1
            fi
            if [ -n "$out" ]; then
              mkdir -p "$out/files/bin"; cp "$root/package.yaml" "$out/package.yaml"; echo "dll" > "$out/files/bin/$name.dll"
            fi
            echo "{\"ok\":true,\"data\":{\"package\":\"$name\",\"files\":1,\"tasks\":[\"hello\"],\"diagnostics\":[]},\"messages\":[\"The package $name: 1 task(s), 1 file(s), built with Roslyn and no MSBuild.\"],\"errors\":[]}"
            """);
    }

    /// <summary>The stand-in <c>dotnet</c>: <c>--list-sdks</c> names one SDK (or none when <paramref name="withSdk"/> is false: a machine with only the runtime), <c>publish &lt;project&gt; -o &lt;folder&gt;</c> writes <c>&lt;folder&gt;/Orders.dll</c>.</summary>
    public static string? Dotnet(bool withSdk = true)
    {
        if (OperatingSystem.IsWindows()) return null;
        return Write("dotnet", $$"""
            #!/bin/bash
            if [ "$1" = "--list-sdks" ]; then {{(withSdk ? "echo '10.0.114 [/usr/lib/dotnet/sdk]'" : "true")}}; exit 0; fi
            if [ "$1" = "publish" ]; then
              out=""; while [ $# -gt 0 ]; do case "$1" in -o) out="$2"; shift;; esac; shift; done
              mkdir -p "$out"; echo dll > "$out/Orders.dll"; exit 0
            fi
            exit 1
            """);
    }
}
