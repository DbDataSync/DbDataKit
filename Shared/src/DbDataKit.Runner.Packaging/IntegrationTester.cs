using System.Diagnostics;
using System.Reflection;
using DbDataKit.Runner.Kit;
using DbDataKit.Runner.Kit.Testing;
using DbDataKit.Scripting;

namespace DbDataKit.Runner.Packaging;

/// <summary>A test script failed: its message is what the author wrote or what the check found.</summary>
public sealed class IntegrationTestFailure(string message) : Exception(message);

/// <summary>
/// What a test script (<c>tests/*.csx</c>) can call by name: a handful of checks that throw, and the project's folder for fixtures. A script is a test; it passes when it finishes and fails when it throws. The job classes of
/// <c>src/</c> are in scope by their own names, so a test is <c>var sim = new RunSimulator&lt;Extract&gt;(...); await sim.RunAsync(); True(sim.Outcome == RunOutcome.Succeeded);</c>.
/// </summary>
public sealed class IntegrationTestGlobals(string projectFolder)
{
    /// <summary>The project's folder, for a fixture (<c>Path.Combine(Project, "tests", "data", "page1.json")</c>).</summary>
    public string Project { get; } = projectFolder;

    public void True(bool condition, string? message = null)
    {
        if (!condition) throw new IntegrationTestFailure(message ?? "A check was not true.");
    }

    public void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new IntegrationTestFailure($"{message ?? "The values differ."} Expected {Show(expected)}, got {Show(actual)}.");
    }

    public void Fail(string message) => throw new IntegrationTestFailure(message);

    private static string Show<T>(T value) => value is null ? "null" : value is string s ? $"\"{s}\"" : value.ToString() ?? "";
}

/// <summary>One test script's result.</summary>
public sealed record TestOutcome(string File, bool Passed, string? Message, TimeSpan Elapsed);

/// <summary>What <see cref="IntegrationTester"/> did: the build it made first and one outcome per test script.</summary>
public sealed record PackageTestResult(bool Succeeded, string Message, IReadOnlyList<BuildDiagnostic> Diagnostics, IReadOnlyList<TestOutcome> Tests, PackageBuildResult Build);

/// <summary>
/// Runs the tests of a csx integration with no SDK (docs/integrations.md): builds the package into a scratch folder, then compiles each <c>tests/**/*.csx</c> as a script against the built assembly, the kit and <c>RunSimulator</c>, and runs it in a
/// load context of its own beside the integration's packages. The kit's types come from this process, so a job and the simulator agree on them. A test runs trusted code in the tool's process; it is not a sandbox (the Workspace's child process is).
/// </summary>
public static class IntegrationTester
{
    public static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);

    public static async Task<PackageTestResult> TestAsync(string root, PackageBuildOptions options, CancellationToken cancellationToken = default)
    {
        root = Path.GetFullPath(root);
        var scratch = Path.Combine(options.CacheDirectory, "test-build", Guid.NewGuid().ToString("N")[..12]);
        try
        {
            var build = await CsxPackageBuilder.BuildAsync(root, scratch, options, cancellationToken).ConfigureAwait(false);
            if (!build.Succeeded) return new(false, build.Message, build.Diagnostics, [], build);
            var diagnostics = new List<BuildDiagnostic>(build.Diagnostics);

            var tests = IntegrationProject.TestFiles(root).ToList();
            if (tests.Count == 0)
            {
                diagnostics.Add(new BuildDiagnostic("warning", "DBD-INT-NOTESTS", "The project has no tests: add a script to tests/ (tests/<name>.csx); it passes when it finishes and fails when it throws."));
                return new(true, "The package builds; there are no tests.", diagnostics, [], build);
            }

            var outcomes = new List<TestOutcome>();
            var closure = new Closure();
            foreach (var (key, value) in build.Closure!.Compile) closure.Compile[key] = value;
            foreach (var (key, value) in build.Closure.Managed) closure.Managed[key] = value;
            closure.Managed[Path.GetFileNameWithoutExtension(build.AssemblyPath!)] = build.AssemblyPath!;
            var hostReferences = CsxPackageBuilder.KitReferences(typeof(RunSimulator<>).Assembly, typeof(IntegrationTester).Assembly)
                .Append(build.AssemblyPath!).Concat(build.Closure.Compile.Values).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var compiler = new CsxCompiler(new CsxCompilerOptions
            {
                CacheDirectory = Path.Combine(options.CacheDirectory, "csx"),
                HostReferences = hostReferences,
                BannedNamespaces = [],
                GlobalsType = typeof(IntegrationTestGlobals),
                Imports = ["System", "System.IO", "System.Linq", "System.Collections.Generic", "System.Threading.Tasks", "DbDataKit.Runner", "DbDataKit.Runner.Kit", "DbDataKit.Runner.Kit.Testing", "DbDataKit.State"],
            });

            foreach (var file in tests)
            {
                var relative = IntegrationProject.Relative(root, file);
                var clock = Stopwatch.StartNew();
                outcomes.Add(await RunOneAsync(compiler, closure, file, relative, root, clock, cancellationToken).ConfigureAwait(false));
            }

            var failed = outcomes.Count(o => !o.Passed);
            foreach (var outcome in outcomes.Where(o => !o.Passed)) diagnostics.Add(BuildDiagnostic.Error("DBD-INT-TEST", outcome.Message ?? "The test failed.", outcome.File));
            return new(failed == 0, failed == 0 ? $"{outcomes.Count} test(s) passed." : $"{failed} of {outcomes.Count} test(s) failed.", diagnostics, outcomes, build);
        }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* the cache folder is the tool's; a leftover is not a failure */ }
        }
    }

    private static async Task<TestOutcome> RunOneAsync(CsxCompiler compiler, Closure closure, string file, string relative, string root, Stopwatch clock, CancellationToken cancellationToken)
    {
        string text;
        try { text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false); }
        catch (IOException ex) { return new(relative, false, $"{relative} cannot be read: {ex.Message}", clock.Elapsed); }

        IReadOnlyList<PackageRequest> asked;
        string source;
        try { (asked, source) = ScriptDirectives.Extract(text, exactOnly: true); }
        catch (ScriptDirectiveException ex) { return new(relative, false, $"{relative}:{ex.Line}: {ex.Message}", clock.Elapsed); }
        if (asked.Count > 0) return new(relative, false, $"{relative}:{asked[0].Line}: a test uses the packages of the integration; ask for '{asked[0].Id}' with #r \"nuget:\" in a file of src/.", clock.Elapsed);

        var compiled = compiler.Compile(source, relative, closure);
        if (!compiled.Success)
        {
            var errors = compiled.Diagnostics.Where(d => d.IsError).Select(d => $"{relative}:{d.Line}: {d.Message}");
            return new(relative, false, "It does not compile: " + string.Join(" | ", errors), clock.Elapsed);
        }

        var context = new ClosureLoadContext("integration-test:" + relative, closure, CsxPackageBuilder.Shared);
        try
        {
            await CsxCompiler.RunAsync(compiled.Image!, context, new IntegrationTestGlobals(root)).WaitAsync(TestTimeout, cancellationToken).ConfigureAwait(false);
            return new(relative, true, null, clock.Elapsed);
        }
        catch (TimeoutException) { return new(relative, false, $"It ran longer than {TestTimeout.TotalSeconds:0} s.", clock.Elapsed); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var inner = ex is TargetInvocationException { InnerException: { } i } ? i : ex;
            return new(relative, false, inner is IntegrationTestFailure ? inner.Message : $"{inner.GetType().Name}: {inner.Message}", clock.Elapsed);
        }
        finally { context.Unload(); }
    }
}
