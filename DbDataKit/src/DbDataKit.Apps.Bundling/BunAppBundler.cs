using System.Diagnostics;
using System.Text;

namespace DbDataKit.Apps.Bundling;

/// <summary>
/// Bundles with <c>bun</c> run as a child process: <c>bun install</c> first when the source has a <c>package.json</c> with dependencies (Bun does not run a dependency's install scripts unless the package
/// lists it as trusted, and <c>--ignore-scripts</c> is passed besides), then <c>bun build</c> for the browser as one script per entry: no code splitting, because the page is a sandboxed document with an opaque origin and a script that imports another file (a chunk, a dynamic <c>import()</c>) is fetched with CORS, which the host does not answer; a dynamic import is bundled in place. The child gets a minimal environment (no secrets of the server's), the source
/// folder as its working directory, and is stopped when the timeout passes. The entry point must be inside the source folder.
/// <para>With <see cref="AppBundleRequest.PackageStore"/> the build is the one of plan 22a GP33: a copy of the source and of a vendored store (checked against the store's hashes) in a folder of its own, no install, no registry settings, and the
/// child process in a network namespace with no route where the machine can make one.</para>
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
        string? generatedStage = null;
        try
        {
            if (request.PackageStore is not null) return await BundleFromStoreAsync(request, source, output, started, linked.Token);
            if (request.GeneratedFiles is { Count: > 0 })
            {
                // the build works on a copy that holds the generated files; the source folder is never written to
                generatedStage = Path.Combine(Path.GetTempPath(), "dbdata-appbuild-" + Guid.NewGuid().ToString("N")[..12]);
                CopySource(source, generatedStage, output, isolate: false);
                WriteGenerated(generatedStage, request.GeneratedFiles);
                source = generatedStage;
                entry = Path.GetFullPath(Path.Combine(source, request.EntryPoint));
            }
            if (HasDependencies(Path.Combine(source, "package.json")))
            {
                if (request.Offline)
                {
                    if (!Directory.Exists(Path.Combine(source, "node_modules")))
                        return AppBundleResult.Failed(started.Elapsed, "The app's package.json lists packages and the build is offline, so none can be installed, and there is no node_modules folder in the source. Install them where a network is (bun install --ignore-scripts) and build again, or build without --offline.");
                }
                else
                {
                    var install = await RunAsync(["install", "--ignore-scripts"], source, linked.Token);
                    if (install.ExitCode != 0) return Failure(started, "Installing the app's packages failed.", install);
                }
            }
            var arguments = new List<string> { "build", entry, "--outdir", output, "--target", "browser", "--format", "esm", "--entry-naming", "[name].[ext]" };
            if (request.Minify) arguments.AddRange(["--minify", "--define", "process.env.NODE_ENV=\"production\""]);   // a deployed build is the libraries' production build
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
        finally
        {
            if (generatedStage is not null) DeleteQuietly(generatedStage);
        }
    }

    private static void DeleteQuietly(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        catch (IOException) { /* a temporary folder left behind is the system's to clean */ }
        catch (UnauthorizedAccessException) { /* the same */ }
    }

    /// <summary>Writes the generated files into the copy; a path that leaves it is refused.</summary>
    private static void WriteGenerated(string stage, IReadOnlyDictionary<string, byte[]> files)
    {
        foreach (var (relative, bytes) in files)
        {
            var target = Path.GetFullPath(Path.Combine(stage, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new ArgumentException($"The generated file {relative} is outside the app's source folder.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
        }
    }

    private async Task<AppBundleResult> BundleFromStoreAsync(AppBundleRequest request, string source, string output, Stopwatch started, CancellationToken cancellationToken)
    {
        var store = request.PackageStore!;
        if (request.Isolation == NetworkIsolation.Required && !NetworkIsolator.IsAvailable)
            return AppBundleResult.Failed(started.Elapsed, $"The build must run with no network and {NetworkIsolator.WhyNot}.");

        // the store itself first (a changed file is named before any work is done), then the source's imports
        var damaged = store.Verify();
        if (damaged.Count > 0) return AppBundleResult.Failed(started.Elapsed, [$"The package store {store.Folder} does not match its {VendoredPackageStore.ManifestName}; nothing was built.", .. damaged]);
        var violations = AppPackagePolicy.Check(source, store, request.AllowedPackages);
        if (violations.Count > 0) return AppBundleResult.Failed(started.Elapsed, ["The source imports packages that are not allowed; nothing was built.", .. violations.Select(v => v.Message)]);

        // the build gets a copy of both, in a folder of its own: the source's node_modules, .npmrc and bunfig.toml are not in it, and nothing the build writes reaches the source or the store
        var stage = Path.Combine(Path.GetTempPath(), "dbdata-appbuild-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            CopySource(source, stage, output, isolate: true);
            if (request.GeneratedFiles is { Count: > 0 }) WriteGenerated(stage, request.GeneratedFiles);
            store.CopyTo(stage);
            var copied = store.Verify(stage);   // the copy is what runs, so the copy is what is checked
            if (copied.Count > 0) return AppBundleResult.Failed(started.Elapsed, [$"The copy of the package store made for this build does not match {VendoredPackageStore.ManifestName}; nothing was built.", .. copied]);
            await File.WriteAllTextAsync(Path.Combine(stage, "bunfig.toml"), "[install]\nauto = \"disable\"\n", cancellationToken);

            var arguments = new List<string> { "build", Path.Combine(stage, request.EntryPoint.Replace('/', Path.DirectorySeparatorChar)), "--outdir", output, "--target", "browser", "--format", "esm", "--entry-naming", "[name].[ext]" };
            if (request.Minify) arguments.AddRange(["--minify", "--define", "process.env.NODE_ENV=\"production\""]);   // a deployed build is the libraries' production build
            var isolated = request.Isolation != NetworkIsolation.None && NetworkIsolator.IsAvailable;
            var build = await RunAsync(arguments, stage, cancellationToken, isolated);
            if (build.ExitCode != 0) return Failure(started, "The bundler refused the source.", build);

            var files = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(output, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();
            var total = files.Sum(f => new FileInfo(Path.Combine(output, f)).Length);
            if (total > request.MaxBundleBytes) return AppBundleResult.Failed(started.Elapsed, $"The bundle is {total:N0} bytes, over the {request.MaxBundleBytes:N0} allowed.");
            var notes = new List<string>
            {
                $"Built from the package store {store.Folder} ({store.Packages.Count} packages, {VendoredPackageStore.ManifestName} {store.Digest[..12]}); nothing was installed.",
                isolated ? "The bundler ran in a network namespace with no route." : $"The bundler ran without network isolation: {NetworkIsolator.WhyNot}. Nothing is installed and no proxy is configured, but the program could open a socket.",
            };
            return files.Count == 0
                ? AppBundleResult.Failed(started.Elapsed, "The bundler wrote nothing.")
                : new AppBundleResult(true, files, total, [], started.Elapsed, notes);
        }
        finally
        {
            try { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
            catch (IOException) { /* a temporary folder left behind is the system's to clean */ }
            catch (UnauthorizedAccessException) { /* the same */ }
        }
    }

    /// <summary>Copies the source. With <paramref name="isolate"/> (a build from a package store) without what would change the build's reach: <c>node_modules</c>, hidden files and folders (<c>.npmrc</c>), <c>bunfig.toml</c>; always without <c>.git</c> and the output folder.</summary>
    private static void CopySource(string source, string stage, string output, bool isolate)
    {
        Directory.CreateDirectory(stage);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var parts = relative.Split(Path.DirectorySeparatorChar);
            if (Path.GetFullPath(file).StartsWith(output + Path.DirectorySeparatorChar, StringComparison.Ordinal) || parts.Contains(".git")) continue;
            if (isolate && (parts.Any(p => p.StartsWith('.') || p == "node_modules") || parts[^1].Equals("bunfig.toml", StringComparison.OrdinalIgnoreCase))) continue;
            var target = Path.Combine(stage, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
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

    private async Task<(int ExitCode, string Output)> RunAsync(IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken, bool isolated = false)
    {
        var wrapped = isolated ? NetworkIsolator.Wrap(bunPath, arguments) : null;
        var info = new ProcessStartInfo(wrapped?.FileName ?? bunPath) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in wrapped?.Arguments ?? arguments) info.ArgumentList.Add(argument);
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
