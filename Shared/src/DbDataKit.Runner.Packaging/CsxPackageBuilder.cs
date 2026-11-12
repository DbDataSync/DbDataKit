using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using DbDataKit.Runner.Kit;
using DbDataKit.Scripting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace DbDataKit.Runner.Packaging;

/// <summary>How a build finds its packages and where it keeps them.</summary>
public sealed class PackageBuildOptions
{
    /// <summary>Where downloaded packages are kept (<c>packages/</c> under it), under the product's data directory, never <c>%TEMP%</c> or a user profile.</summary>
    public required string CacheDirectory { get; init; }

    /// <summary>NuGet v3 index addresses or folders in the global-packages layout (an offline feed), with the id patterns of source mapping; nuget.org when none is given.</summary>
    public IReadOnlyList<PackageSource> Sources { get; init; } = [new PackageSource("https://api.nuget.org/v3/index.json")];

    /// <summary>Use the cache only: a package that is not there is an error and no source is asked.</summary>
    public bool Offline { get; init; }

    /// <summary>A package with no pin in <c>package.lock</c> is refused: for a build that publishes, where nobody is there to trust a first download.</summary>
    public bool RequirePinned { get; init; }

    /// <summary>Write <c>package.lock</c> into the project when the pins change (the default); false leaves the project untouched.</summary>
    public bool WriteLock { get; init; } = true;
}

/// <summary>What a build made: whether it worked, a sentence, every finding, the package, its tasks and the files of the package (relative to <c>files/</c>).</summary>
public sealed record PackageBuildResult(bool Succeeded, string Message, IReadOnlyList<BuildDiagnostic> Diagnostics, string? Package = null, IReadOnlyList<string>? Tasks = null, IReadOnlyList<string>? Files = null, string? AssemblyPath = null, Closure? Closure = null)
{
    public static PackageBuildResult Failed(string message, IEnumerable<BuildDiagnostic> diagnostics) => new(false, message, [.. diagnostics]);
}

/// <summary>
/// Builds a csx integration into a runner package with <b>no .NET SDK and no MSBuild</b> (docs/integrations.md): only Roslyn (<c>Microsoft.CodeAnalysis.CSharp</c>) and the NuGet client libraries <c>DbDataKit.Scripting</c> already uses. The steps:
/// <list type="number">
/// <item>read <c>package.yaml</c> with the runner's own strict reader;</item>
/// <item>read every <c>src/**/*.csx</c>, lift its <c>#r "nuget: Id, 1.2.3"</c> lines out (blanked, so line numbers survive) and restore those packages with the shared resolver: exact versions, hashes pinned in <c>package.lock</c>, offline from a cache or a folder feed;</item>
/// <item>compile all the files together as one library (they are ordinary C#: types, no top-level statements) against the runtime's own assemblies, the kit and the packages' compile assets, deterministically, with an embedded PDB so a stack trace names a file and a line;</item>
/// <item>load the result into a collectible context and check that the manifest's tasks and the <c>[RunnerTask]</c> job classes are the same set;</item>
/// <item>write <c>package.yaml</c> and <c>files/</c> (the assembly in <c>bin/</c>, the packages' run-time assemblies beside it, the project's own <c>files/</c> folder), which is what <c>package publish</c> takes.</item>
/// </list>
/// The code is trusted code (plan 27 section 4.7): there is no namespace guard, the author is the one who is trusted.
/// </summary>
public static class CsxPackageBuilder
{
    private static readonly string[] GlobalUsings =
    [
        "System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Net.Http", "System.Text", "System.Threading", "System.Threading.Tasks", "DbDataKit.Runner", "DbDataKit.Runner.Kit",
    ];

    public static async Task<PackageBuildResult> BuildAsync(string root, string outputFolder, PackageBuildOptions options, CancellationToken cancellationToken = default)
    {
        root = Path.GetFullPath(root);
        var diagnostics = new List<BuildDiagnostic>();

        var manifestPath = Path.Combine(root, IntegrationProject.ManifestFile);
        var load = PackageManifestReader.LoadFile(manifestPath);
        diagnostics.AddRange(load.Diagnostics.Where(d => d.Severity != DbDataKit.Config.Yaml.ConfigSeverity.Note).Select(d => BuildDiagnostic.From(d, IntegrationProject.ManifestFile)));
        if (!load.Ok)
        {
            if (!diagnostics.Any(d => d.IsError)) diagnostics.Add(BuildDiagnostic.Error("DBD-INT-MANIFEST", $"{IntegrationProject.ManifestFile} cannot be used.", IntegrationProject.ManifestFile));
            return PackageBuildResult.Failed($"{IntegrationProject.ManifestFile} has problems.", diagnostics);
        }

        var manifest = load.Manifest!;
        var launch = manifest.Launch;
        if (launch.Kind != LaunchKind.Assembly || launch.Assembly is not { } launchAssembly || !launchAssembly.Replace('\\', '/').StartsWith("bin/", StringComparison.Ordinal) || !launchAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return PackageBuildResult.Failed("A csx integration starts as an assembly in bin/.", [.. diagnostics, BuildDiagnostic.Error("DBD-INT-LAUNCH", "The launch of a csx integration is `launch: { kind: assembly, assembly: bin/<name>.dll }`; the build writes that assembly.", IntegrationProject.ManifestFile)]);
        launchAssembly = launchAssembly.Replace('\\', '/');
        var assemblyName = Path.GetFileNameWithoutExtension(launchAssembly);

        var files = IntegrationProject.CsxFiles(root).ToList();
        if (files.Count == 0) return PackageBuildResult.Failed("There is no code.", [.. diagnostics, BuildDiagnostic.Error("DBD-INT-NOCODE", "The project has no C# file: put the job classes in src/*.csx.", IntegrationProject.SourceFolder + "/")]);

        // 1. the packages the files ask for
        var requests = new Dictionary<string, PackageRequest>(StringComparer.OrdinalIgnoreCase);
        var sources = new List<(string Relative, string Text)>();
        foreach (var file in files)
        {
            var relative = IntegrationProject.Relative(root, file);
            string text;
            try { text = File.ReadAllText(file, new UTF8Encoding(false, true)); }
            catch (Exception ex) when (ex is IOException or DecoderFallbackException) { diagnostics.Add(BuildDiagnostic.Error("DBD-INT-READ", $"{relative} cannot be read as UTF-8 text: {ex.Message}", relative)); continue; }
            try
            {
                var (packages, blanked) = ScriptDirectives.Extract(text, exactOnly: true);
                foreach (var request in packages)
                {
                    if (requests.TryGetValue(request.Id, out var known) && known.Range.ToString() != request.Range.ToString())
                        diagnostics.Add(BuildDiagnostic.Error("DBD-INT-PACKAGE", $"{request.Id} is asked for with two versions ({known.Range.PrettyPrint()} and {request.Range.PrettyPrint()}); one project uses one version of a package.", relative, request.Line));
                    else requests[request.Id] = request;
                }
                sources.Add((relative, blanked));
            }
            catch (ScriptDirectiveException ex) { diagnostics.Add(BuildDiagnostic.Error("DBD-INT-PACKAGE", ex.Message, relative, ex.Line > 0 ? ex.Line : null)); }
        }
        if (diagnostics.Any(d => d.IsError)) return PackageBuildResult.Failed("The files could not be read.", diagnostics);

        var closure = new Closure();
        var lockPath = Path.Combine(root, IntegrationProject.LockFile);
        if (requests.Count > 0)
        {
            LockFile pins;
            try { pins = LockFile.Read(lockPath, IntegrationProject.LockFile); }
            catch (InvalidDataException ex) { return PackageBuildResult.Failed(ex.Message, [.. diagnostics, BuildDiagnostic.Error("DBD-INT-LOCK", ex.Message, IntegrationProject.LockFile)]); }
            try
            {
                closure = await new PackageResolver(new PackageResolverOptions
                {
                    Store = new PackageStore(Path.Combine(options.CacheDirectory, "packages")),
                    Sources = options.Sources,
                    HostProvides = name => FrameworkAssemblies().ContainsKey(name) || HostAssemblyNames().Contains(name),
                    Lock = pins,
                    RequirePinned = options.RequirePinned,
                    Offline = options.Offline,
                }).ResolveAsync(requests.Values, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is PackageResolutionException or PackageIntegrityException or IOException or UnauthorizedAccessException or HttpRequestException)
            {
                return PackageBuildResult.Failed("The packages could not be restored.", [.. diagnostics, BuildDiagnostic.Error("DBD-INT-RESTORE", $"The packages could not be restored: {ex.Message}")]);
            }
            diagnostics.AddRange(closure.Warnings.Select(w => new BuildDiagnostic("warning", "DBD-INT-PACKAGE", w.Message)));
            if (closure.Native.Count > 0)
                return PackageBuildResult.Failed("A package brings native libraries.", [.. diagnostics, BuildDiagnostic.Error("DBD-INT-NATIVE", $"The packages bring native libraries ({string.Join(", ", closure.Native.Keys.Order(StringComparer.Ordinal))}), which a csx build does not carry yet: the task host finds a native library only through a deps.json. Use a package without natives, or build the integration as a compiled project.")]);
            pins.Replace(closure.Packages);
            if (options.WriteLock) pins.Write(lockPath);
        }

        // 2. compile
        var parse = new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.None, SourceCodeKind.Regular);
        var trees = sources.Select(s => CSharpSyntaxTree.ParseText(SourceText.From(s.Text, Encoding.UTF8), parse, path: s.Relative)).ToList();
        trees.Add(CSharpSyntaxTree.ParseText(SourceText.From(string.Join("\n", GlobalUsings.Select(u => $"global using {u};")) + "\n", Encoding.UTF8), parse, path: "<global-usings>.cs"));
        var framework = FrameworkAssemblies();
        var reference = ReferencePack();
        if (framework.Count == 0)
            return PackageBuildResult.Failed("The runtime's assemblies were not found.", [.. diagnostics, BuildDiagnostic.Error("DBD-INT-RUNTIME", "The shared framework (Microsoft.NETCore.App) of the .NET runtime was not found next to this program or in DOTNET_ROOT, so there is nothing to compile against.")]);
        var references = (reference?.Values ?? framework.Values).Concat(KitReferences()).Concat(closure.Compile.Values).Distinct(StringComparer.OrdinalIgnoreCase).Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
        var compilation = CSharpCompilation.Create(assemblyName, trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Enable, deterministic: true, allowUnsafe: false));

        using var image = new MemoryStream();
        EmitResult emit = compilation.Emit(image, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded), cancellationToken: cancellationToken);
        diagnostics.AddRange(emit.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning && d.Location.IsInSource).Select(ToDiagnostic));
        diagnostics.AddRange(emit.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning && !d.Location.IsInSource).Select(d => new BuildDiagnostic(d.Severity == DiagnosticSeverity.Error ? "error" : "warning", "DBD-INT-" + d.Id, d.GetMessage())));
        if (!emit.Success) return PackageBuildResult.Failed($"The code does not compile ({diagnostics.Count(d => d.IsError)} error(s)).", diagnostics);
        var bytes = image.ToArray();

        // 3. the tasks the manifest lists and the job classes are one set
        var declared = DeclaredTasks(bytes, closure, manifest.Name, launch, out var loadProblem);
        if (loadProblem is not null) return PackageBuildResult.Failed("The built assembly could not be inspected.", [.. diagnostics, BuildDiagnostic.Error("DBD-INT-LOAD", loadProblem)]);
        var listed = manifest.Tasks.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var task in listed.Except(declared).Order(StringComparer.Ordinal))
            diagnostics.Add(BuildDiagnostic.Error("DBD-INT-TASK", $"{IntegrationProject.ManifestFile} lists the task '{task}' and no job class has [RunnerTask(\"{task}\")].", IntegrationProject.ManifestFile));
        foreach (var task in declared.Except(listed).Order(StringComparer.Ordinal))
            diagnostics.Add(BuildDiagnostic.Error("DBD-INT-TASK", $"A job class has [RunnerTask(\"{task}\")] and {IntegrationProject.ManifestFile} does not list that task (its schedule, triggers and limits are the manifest's).", IntegrationProject.ManifestFile));
        if (diagnostics.Any(d => d.IsError)) return PackageBuildResult.Failed("The manifest and the code disagree.", diagnostics);

        // 4. write the package
        var filesFolder = Path.Combine(outputFolder, "files");
        Directory.CreateDirectory(outputFolder);
        if (Directory.Exists(filesFolder)) Directory.Delete(filesFolder, recursive: true);
        Directory.CreateDirectory(Path.Combine(filesFolder, "bin"));
        var written = new List<string>();
        var assemblyPath = Path.Combine(filesFolder, launchAssembly.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllBytes(assemblyPath, bytes);
        written.Add(launchAssembly);
        foreach (var (_, path) in closure.Managed.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var relative = "bin/" + Path.GetFileName(path);
            if (written.Contains(relative, StringComparer.OrdinalIgnoreCase)) continue;
            File.Copy(path, Path.Combine(filesFolder, relative.Replace('/', Path.DirectorySeparatorChar)), overwrite: true);
            written.Add(relative);
        }
        var extra = Path.Combine(root, IntegrationProject.ExtraFilesFolder);
        if (Directory.Exists(extra))
            foreach (var file in Directory.EnumerateFiles(extra, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var relative = IntegrationProject.Relative(extra, file);
                if (written.Contains(relative, StringComparer.OrdinalIgnoreCase)) return PackageBuildResult.Failed("A file of files/ would replace a built one.", [.. diagnostics, BuildDiagnostic.Error("DBD-INT-FILES", $"files/{relative} has the path of a file the build writes.", $"files/{relative}")]);
                var target = Path.Combine(filesFolder, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
                written.Add(relative);
            }
        File.Copy(manifestPath, Path.Combine(outputFolder, IntegrationProject.ManifestFile), overwrite: true);
        var taskNames = manifest.Tasks.Select(t => t.Name).Order(StringComparer.Ordinal).ToList();
        if (reference is null) diagnostics.Add(new BuildDiagnostic("info", "DBD-INT-REFS", "No reference pack of the runtime was found (it ships with the SDK), so the code was compiled against the runtime's implementation assemblies: the package runs on this major version of .NET, not on others."));
        return new PackageBuildResult(true, $"The package {manifest.Name}: {taskNames.Count} task(s), {written.Count} file(s), built with Roslyn and no MSBuild.", diagnostics, manifest.Name, taskNames, written, assemblyPath, closure);
    }

    /// <summary>The task names the job classes of the image declare; <paramref name="problem"/> says why it could not be read.</summary>
    private static IReadOnlySet<string> DeclaredTasks(byte[] image, Closure closure, string package, PackageLaunch launch, out string? problem)
    {
        problem = null;
        var context = new ClosureLoadContext("integration-build:" + package, closure, Shared);
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(image));
            return ManifestGenerator.ForAssembly(assembly, package, launch).Tasks.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is ReflectionTypeLoadException or FileNotFoundException or FileLoadException or BadImageFormatException or InvalidOperationException or TypeLoadException or TargetInvocationException)
        {
            problem = ex is ReflectionTypeLoadException { LoaderExceptions.Length: > 0 } r ? string.Join("; ", r.LoaderExceptions.Where(e => e is not null).Select(e => e!.Message).Distinct()) : ex.Message;
            return new HashSet<string>();
        }
        finally { context.Unload(); }
    }

    internal static bool Shared(string name) => AssemblyLoadContext.Default.Assemblies.Any(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));

    private static BuildDiagnostic ToDiagnostic(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetLineSpan();
        return new BuildDiagnostic(diagnostic.Severity == DiagnosticSeverity.Error ? "error" : "warning", "DBD-INT-" + diagnostic.Id, diagnostic.GetMessage(), span.Path.Replace('\\', '/'), span.StartLinePosition.Line + 1);
    }

    // ---- what the code is compiled against ----

    /// <summary>
    /// The assemblies of the runtime's shared framework <c>Microsoft.NETCore.App</c>, by simple name: the ones this process runs on when it is a framework-dependent program, else the install the runtime lives in
    /// (<c>DOTNET_ROOT</c>, the folder of <c>dotnet</c> on <c>PATH</c>, the usual places) at this process's major version. These are <b>implementation</b> assemblies: the reference assemblies are part of the SDK's packs, and with no SDK there are none.
    /// </summary>
    public static IReadOnlyDictionary<string, string> FrameworkAssemblies()
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var marker = $"{Path.DirectorySeparatorChar}shared{Path.DirectorySeparatorChar}Microsoft.NETCore.App{Path.DirectorySeparatorChar}";
        foreach (var path in ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (path.Contains(marker, StringComparison.Ordinal)) found[Path.GetFileNameWithoutExtension(path)] = path;
        if (found.Count > 0) return found;

        foreach (var root in DotnetRoots())
            try
            {
                var shared = Path.Combine(root, "shared", "Microsoft.NETCore.App");
                if (!Directory.Exists(shared)) continue;
                var best = Directory.EnumerateDirectories(shared).Select(d => (Directory: d, Version: Version.TryParse(Path.GetFileName(d).Split('-')[0], out var v) ? v : null))
                    .Where(d => d.Version is { } v && v.Major == Environment.Version.Major).OrderByDescending(d => d.Version).FirstOrDefault();
                if (best.Directory is null) continue;
                foreach (var dll in Directory.EnumerateFiles(best.Directory, "*.dll")) found[Path.GetFileNameWithoutExtension(dll)] = dll;
                if (found.Count > 0) return found;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { /* a bad entry */ }
        return found;
    }

    /// <summary>
    /// The runtime's <b>reference</b> assemblies (<c>packs/Microsoft.NETCore.App.Ref/&lt;version&gt;/ref/net&lt;major&gt;.0</c>) when a pack of this major version is on the machine (an SDK install has it, a runtime-only install does not): compiling against them
    /// makes an assembly that binds to <c>System.Runtime</c> and runs on later versions too. Null otherwise, and the build falls back to <see cref="FrameworkAssemblies"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? ReferencePack()
    {
        foreach (var root in DotnetRoots())
            try
            {
                var packs = Path.Combine(root, "packs", "Microsoft.NETCore.App.Ref");
                if (!Directory.Exists(packs)) continue;
                var best = Directory.EnumerateDirectories(packs).Select(d => (Directory: d, Version: Version.TryParse(Path.GetFileName(d).Split('-')[0], out var v) ? v : null))
                    .Where(d => d.Version is { } v && v.Major == Environment.Version.Major).OrderByDescending(d => d.Version).FirstOrDefault();
                var refs = best.Directory is null ? null : Path.Combine(best.Directory, "ref", $"net{Environment.Version.Major}.{Environment.Version.Minor}");
                if (refs is null || !Directory.Exists(refs)) continue;
                return Directory.EnumerateFiles(refs, "*.dll").ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => f, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { /* a bad entry */ }
        return null;
    }

    /// <summary>Where a .NET install may be: <c>DOTNET_ROOT</c> when it is set (and then only that, so a machine can say which install it means), else the folders of <c>PATH</c> and the usual places.</summary>
    private static IEnumerable<string> DotnetRoots()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } dotnetRoot) return [dotnetRoot];
        return [.. (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries), "/usr/share/dotnet", "/usr/lib/dotnet", "/usr/local/share/dotnet", @"C:\Program Files\dotnet"];
    }

    /// <summary>The kit and what its public surface stands on, as files: <c>DbDataKit.Runner.Kit</c> and every assembly outside the shared framework that it references, transitively.</summary>
#pragma warning disable IL3000 // Assembly.Location is empty in a single-file app; an assembly without a file is skipped below (Length == 0), so the empty string is handled
    public static IReadOnlyList<string> KitReferences(params Assembly[] also)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<Assembly>();
        queue.Enqueue(typeof(RunnerJobBase).Assembly);
        foreach (var assembly in also) queue.Enqueue(assembly);
        while (queue.TryDequeue(out var assembly))
        {
            var name = assembly.GetName().Name!;
            if (assembly.IsDynamic || assembly.Location.Length == 0 || found.ContainsKey(name) || assembly.Location.Contains($"{Path.DirectorySeparatorChar}shared{Path.DirectorySeparatorChar}Microsoft.", StringComparison.Ordinal)) continue;
            found[name] = assembly.Location;
            foreach (var reference in assembly.GetReferencedAssemblies())
                try { queue.Enqueue(Assembly.Load(reference)); }
                catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException) { /* not needed to compile against */ }
        }
        return [.. found.Values];
    }
#pragma warning restore IL3000

    /// <summary>The simple names of what <see cref="KitReferences"/> gives: a package of the integration never brings its own copy of these (the task host answers them, so the job and the kit agree on their types).</summary>
    private static IReadOnlySet<string> HostAssemblyNames() => KitReferences().Select(p => Path.GetFileNameWithoutExtension(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
