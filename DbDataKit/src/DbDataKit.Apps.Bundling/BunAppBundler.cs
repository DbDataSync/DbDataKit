using System.Diagnostics;
using System.Text;

namespace DbDataKit.Apps.Bundling;

/// <summary>
/// Bundles with <c>bun</c> run as a child process: <c>bun install</c> first when the source has a <c>package.json</c> with dependencies (Bun does not run a dependency's install scripts unless the package
/// lists it as trusted, and <c>--ignore-scripts</c> is passed besides), then <c>bun build</c> for the browser as ES modules with code splitting. The child gets a minimal environment (no secrets of the server's), the source
/// folder as its working directory, and is stopped when the timeout passes. The entry point must be inside the source folder.
/// </summary>
public sealed class BunAppBundler(string bunPath = "bun") : IAppBundler
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);

    public async Task<AppBundleResult> BundleAsync(AppBundleRequest request, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.StartNew();
        var source = Path.GetFullPath(request.SourceDirectory);
        if (!Directory.Exists(source)) return AppBundleResult.Failed(started.Elapsed, $"The source folder {request.SourceDirectory} does not exist.");
        var entry = Path.GetFullPath(Path.Combine(source, request.EntryPoint));
        if (!entry.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return AppBundleResult.Failed(started.Elapsed, $"The entry point {request.EntryPoint} is outside the app's source folder.");
        if (!File.Exists(entry)) return AppBundleResult.Failed(started.Elapsed, $"The entry point {request.EntryPoint} does not exist.");

        var output = Path.GetFullPath(request.OutputDirectory);
        if (output == source || source.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return AppBundleResult.Failed(started.Elapsed, "The output folder must not be the source folder or hold it.");
        if (Directory.Exists(output)) Directory.Delete(output, true);
        Directory.CreateDirectory(output);

        using var timeout = new CancellationTokenSource(request.Timeout ?? DefaultTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            if (HasDependencies(Path.Combine(source, "package.json")))
            {
                var install = await RunAsync(["install", "--ignore-scripts"], source, linked.Token);
                if (install.ExitCode != 0) return Failure(started, "Installing the app's packages failed.", install);
            }
            var arguments = new List<string> { "build", entry, "--outdir", output, "--target", "browser", "--format", "esm", "--splitting", "--entry-naming", "[name].[ext]" };
            if (request.Minify) arguments.Add("--minify");
            var build = await RunAsync(arguments, source, linked.Token);
            if (build.ExitCode != 0) return Failure(started, "The bundler refused the source.", build);

            var files = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(output, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();
            var total = files.Sum(f => new FileInfo(Path.Combine(output, f)).Length);
            if (total > request.MaxBundleBytes) return AppBundleResult.Failed(started.Elapsed, $"The bundle is {total:N0} bytes, over the {request.MaxBundleBytes:N0} allowed.");
            return files.Count == 0
                ? AppBundleResult.Failed(started.Elapsed, "The bundler wrote nothing.")
                : new AppBundleResult(true, files, total, [], started.Elapsed);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return AppBundleResult.Failed(started.Elapsed, $"The build took longer than {(request.Timeout ?? DefaultTimeout).TotalSeconds:N0} seconds and was stopped.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return AppBundleResult.Failed(started.Elapsed, $"The bundler '{bunPath}' could not be started: it is not installed or not on the path.");
        }
    }

    private static bool HasDependencies(string packageJson)
    {
        if (!File.Exists(packageJson)) return false;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(packageJson));
            return new[] { "dependencies", "devDependencies" }.Any(k => document.RootElement.TryGetProperty(k, out var d) && d.ValueKind == System.Text.Json.JsonValueKind.Object && d.EnumerateObject().Any());
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    private static AppBundleResult Failure(Stopwatch started, string headline, (int ExitCode, string Output) run) =>
        new(false, [], 0, [new AppBundleDiagnostic(headline), .. run.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(40).Select(l => new AppBundleDiagnostic(l))], started.Elapsed);

    private async Task<(int ExitCode, string Output)> RunAsync(IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(bunPath) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        // only what a build needs: the server's own environment (tokens, secrets, connection strings) stays out of the child
        info.Environment.Clear();
        foreach (var name in new[] { "PATH", "HOME", "USERPROFILE", "TEMP", "TMP", "TMPDIR", "SystemRoot", "LOCALAPPDATA", "APPDATA" })
            if (Environment.GetEnvironmentVariable(name) is { } value) info.Environment[name] = value;
        info.Environment["CI"] = "1";
        info.Environment["NO_COLOR"] = "1";

        using var process = Process.Start(info) ?? throw new System.ComponentModel.Win32Exception("the process did not start");
        process.StandardInput.Close();
        var text = new StringBuilder();
        async Task Pump(StreamReader reader) { string? line; while ((line = await reader.ReadLineAsync(CancellationToken.None)) is not null) lock (text) text.AppendLine(line); }
        var pumps = Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError));
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch (InvalidOperationException) { } throw; }
        await pumps;
        return (process.ExitCode, text.ToString());
    }
}
