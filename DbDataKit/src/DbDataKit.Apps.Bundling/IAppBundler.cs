namespace DbDataKit.Apps.Bundling;

/// <summary>What to build: an app's source folder and the file in it the bundle starts from.</summary>
/// <param name="SourceDirectory">The app's source. Nothing outside it is read except the packages it names, which are fetched into it.</param>
/// <param name="EntryPoint">Path of the entry file, relative to <paramref name="SourceDirectory"/> (for example <c>src/main.tsx</c>).</param>
/// <param name="OutputDirectory">Where the bundle is written; made if missing and emptied of an earlier build's files.</param>
/// <param name="Minify">Minify the bundle (the default for a deployed version).</param>
/// <param name="MaxBundleBytes">The most the bundle's files may add up to; a larger build fails rather than being served.</param>
/// <param name="Timeout">How long the build may take before it is stopped.</param>
/// <param name="Offline">Reach no package registry: nothing is installed, and a source with dependencies builds only when its <c>node_modules</c> folder is already there (a vendored or earlier install). <paramref name="PackageStore"/> is the complete form (plan 22a GP33).</param>
/// <param name="PackageStore">Build from a vendored package store (QG12): the source's own <c>node_modules</c> and registry settings are ignored, the build gets a copy of the store checked against its manifest, nothing is installed, and a source that imports a package that is not in the store's library or in <paramref name="AllowedPackages"/> is refused by name. Implies <paramref name="Offline"/>.</param>
/// <param name="AllowedPackages">The administrator's allow-list (<c>apps.allowedPackages</c>): further packages, present in the store, an app may import.</param>
/// <param name="Isolation">How hard the build is kept off the network when it is built from a store (see <see cref="NetworkIsolation"/>).</param>
public sealed record AppBundleRequest(string SourceDirectory, string EntryPoint, string OutputDirectory, bool Minify = true, long MaxBundleBytes = 20L * 1024 * 1024, TimeSpan? Timeout = null, bool Offline = false,
    VendoredPackageStore? PackageStore = null, IReadOnlyList<string>? AllowedPackages = null, NetworkIsolation Isolation = NetworkIsolation.IfAvailable);

/// <summary>One line the bundler said about the source, in the words it used (a file, a line, a reason). Never carries the environment.</summary>
public sealed record AppBundleDiagnostic(string Message);

/// <summary>The outcome: the files written (relative to the output directory) when it worked, the diagnostics when it did not. <paramref name="Notes"/> are things worth saying about a build that worked (for example that the network was not isolated).</summary>
public sealed record AppBundleResult(bool Succeeded, IReadOnlyList<string> Files, long TotalBytes, IReadOnlyList<AppBundleDiagnostic> Diagnostics, TimeSpan Duration, IReadOnlyList<string>? Notes = null)
{
    public static AppBundleResult Failed(TimeSpan duration, params string[] messages) =>
        new(false, [], 0, messages.Select(m => new AppBundleDiagnostic(m)).ToList(), duration);
}

/// <summary>Builds an app. An implementation never throws for a bad source: it answers a failed result with the reasons.</summary>
public interface IAppBundler
{
    Task<AppBundleResult> BundleAsync(AppBundleRequest request, CancellationToken cancellationToken = default);
}
