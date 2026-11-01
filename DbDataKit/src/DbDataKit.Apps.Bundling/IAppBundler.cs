namespace DbDataKit.Apps.Bundling;

/// <summary>What to build: an app's source folder and the file in it the bundle starts from.</summary>
/// <param name="SourceDirectory">The app's source. Nothing outside it is read except the packages it names, which are fetched into it.</param>
/// <param name="EntryPoint">Path of the entry file, relative to <paramref name="SourceDirectory"/> (for example <c>src/main.tsx</c>).</param>
/// <param name="OutputDirectory">Where the bundle is written; made if missing and emptied of an earlier build's files.</param>
/// <param name="Minify">Minify the bundle (the default for a deployed version).</param>
/// <param name="MaxBundleBytes">The most the bundle's files may add up to; a larger build fails rather than being served.</param>
/// <param name="Timeout">How long the build may take before it is stopped.</param>
public sealed record AppBundleRequest(string SourceDirectory, string EntryPoint, string OutputDirectory, bool Minify = true, long MaxBundleBytes = 20L * 1024 * 1024, TimeSpan? Timeout = null);

/// <summary>One line the bundler said about the source, in the words it used (a file, a line, a reason). Never carries the environment.</summary>
public sealed record AppBundleDiagnostic(string Message);

/// <summary>The outcome: the files written (relative to the output directory) when it worked, the diagnostics when it did not.</summary>
public sealed record AppBundleResult(bool Succeeded, IReadOnlyList<string> Files, long TotalBytes, IReadOnlyList<AppBundleDiagnostic> Diagnostics, TimeSpan Duration)
{
    public static AppBundleResult Failed(TimeSpan duration, params string[] messages) =>
        new(false, [], 0, messages.Select(m => new AppBundleDiagnostic(m)).ToList(), duration);
}

/// <summary>Builds an app. An implementation never throws for a bad source: it answers a failed result with the reasons.</summary>
public interface IAppBundler
{
    Task<AppBundleResult> BundleAsync(AppBundleRequest request, CancellationToken cancellationToken = default);
}
