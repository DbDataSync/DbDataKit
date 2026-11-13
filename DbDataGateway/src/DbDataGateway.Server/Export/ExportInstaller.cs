using System.IO.Compression;
using System.Security.Cryptography;
using DbDataKit.Update;
using DbDataKit.Upgrade;
using DbDataKit.Views.Export;

namespace DbDataGateway.Server.Export;

/// <summary>What one install of the export component is asked to do (plan 32 DBR16, owner decision 2026-11-12).</summary>
/// <param name="Version">The release whose component is installed; null is this program's own version (the print page and the program are one release).</param>
/// <param name="Source">Where the release's files are: a folder (an offline copy: the asset files directly in it, or in <c>dbdatagateway/v&lt;version&gt;/</c> as a mirror lays them out), a <c>file://</c> address of one, or an https address that serves them; null reads the product's GitHub release (<c>DBDATAKIT_EXPORT_RELEASE_BASE</c> or the upgrade mirror when set).</param>
/// <param name="Directory">Where the component is put; null is <c>export-component</c> in the data directory (an upgrade of the program leaves it).</param>
/// <param name="Rid">The platform; null is this machine's.</param>
/// <param name="Force">Install again although the same version is installed and whole.</param>
public sealed record ExportInstallRequest(string? Version, string? Source, string? Directory, string? Rid, bool Force);

/// <summary>What an install did: where the component is, which Chromium and fonts it holds, whether it was already there, and whether it carries the browser and fonts this program was tested with (<paramref name="Pins"/>: a verification line or a warning, never a refusal).</summary>
public sealed record ExportInstallResult(string Directory, string Version, string Rid, string Chromium, string Fonts, bool AlreadyInstalled, long DownloadedBytes, ExportPinCheck Pins);

/// <summary>An install that did not happen, in words: nothing was installed or changed unless the message says so.</summary>
public sealed class ExportInstallException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Where an install is: <c>state</c> is idle, running, done or failed; <c>step</c> names the part; the bytes are the archive's.</summary>
public sealed record ExportInstallProgress(string State, string Step, long Bytes, long Total, string? Message);

/// <summary>An <see cref="IProgress{T}"/> that runs on the caller's thread, in order: <see cref="Progress{T}"/> posts to the thread pool, so a late report could land after the install finished (or the writer it prints to was closed).</summary>
public sealed class ExportProgressSink(Action<ExportInstallProgress> report) : IProgress<ExportInstallProgress>
{
    public void Report(ExportInstallProgress value) => report(value);
}

/// <summary>
/// Installs the export component from a release (plan 32 DBR16): downloads <c>dbdatagateway-export-v&lt;version&gt;-&lt;rid&gt;.zip</c> and the release's <c>SHA256SUMS</c> from the release address, a folder or a mirror,
/// refuses an archive whose SHA-256 is not the one the checksums name, extracts it beside the host (never into the program folder, so an upgrade keeps it), runs <see cref="ExportComponent.Verify"/> over the result
/// (the browser, every font and licence against the pins inside it, the print files against the manifest) and only then moves it into place, replacing an older one. The command <c>dbdatagateway export install</c> and the
/// console's button both call this one class. It changes nothing in <c>server.yaml</c>: the caller does, with <see cref="ExportComponent.SettingsFor"/>.
/// </summary>
public sealed class ExportInstaller(IContentFetcher fetcher, string dataDirectory, string programVersion, Func<string?>? environment = null)
{
    public const string DefaultFolder = "export-component";
    public const string SourceVariable = "DBDATAKIT_EXPORT_RELEASE_BASE";
    private const long MaxArchiveBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>https (with redirects: a GitHub release asset is one) and files.</summary>
    public static IContentFetcher DefaultFetcher()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("dbdatagateway-export-install");
        return new CompositeContentFetcher(new HttpContentFetcher(http), new FileContentFetcher());
    }

    public static string ZipName(string version, string rid) => $"dbdatagateway-export-v{version}-{rid}.zip";

    /// <summary>The folder an install without <c>--dir</c> uses.</summary>
    public static string DefaultDirectory(string dataDirectory) => Path.Combine(Path.GetFullPath(dataDirectory), DefaultFolder);

    public async Task<ExportInstallResult> InstallAsync(ExportInstallRequest request, IProgress<ExportInstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        void Report(string step, long bytes = 0, long total = 0) => progress?.Report(new ExportInstallProgress("running", step, bytes, total, null));
        var rid = request.Rid ?? ExportComponent.CurrentRid()
            ?? throw new ExportInstallException("No browser is pinned for this platform (linux-x64 and win-x64 have one); name the platform with --rid, or set views.export to an installed browser (mode: system).");
        if (rid is not ("linux-x64" or "win-x64")) throw new ExportInstallException($"No export component is built for '{rid}' (linux-x64 and win-x64 are).");
        var version = (request.Version ?? programVersion).Trim().TrimStart('v');
        if (version.Length == 0 || version.StartsWith("0.0.0", StringComparison.Ordinal))
            throw new ExportInstallException("This program has no release version (a build from source), so it cannot tell which release's component to install: name one with --version <release version>.");
        if (version.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '-'))) throw new ExportInstallException($"'{version}' is not a release version.");
        var target = Path.GetFullPath(request.Directory ?? DefaultDirectory(dataDirectory));
        var zip = ZipName(version, rid);

        // already here and whole: nothing to download
        if (!request.Force && ExportComponent.ReadManifest(target) is { } installed && installed.Rid == rid && ExportComponent.Verify(target, rid).Count == 0 && ReadVersion(target) == version)
            return new ExportInstallResult(target, version, rid, installed.Chromium, installed.Fonts, true, 0, ExportPins.Compare(target, installed));

        var (sumsUri, zipUri) = Locate(request.Source, version, zip);
        Report("reading the checksums");
        var sums = await ReadSumsAsync(sumsUri, cancellationToken).ConfigureAwait(false);
        if (!sums.TryGetValue(zip, out var want))
            throw new ExportInstallException($"{Name(sumsUri)} does not list {zip}: that release has no export component for {rid} (it was built before the component was a release asset, or it is not a release of this product).");

        var parent = Path.GetDirectoryName(target) ?? target;
        Directory.CreateDirectory(parent);
        var work = Path.Combine(parent, ".export-install-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            var archive = Path.Combine(work, zip);
            long downloaded;
            string got;
            try
            {
                (downloaded, got) = await DownloadAsync(zipUri, archive, (b, t) => Report("downloading " + zip, b, t), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or NotSupportedException or UnauthorizedAccessException)
            {
                throw new ExportInstallException($"{Name(zipUri)} could not be read: {ex.Message}", ex);
            }
            Report("checking the archive", downloaded, downloaded);
            if (!got.Equals(want, StringComparison.OrdinalIgnoreCase))
                throw new ExportInstallException($"{zip} does not match {Name(sumsUri)}: its SHA-256 is {got} and the release says {want}. Nothing was installed.");

            Report("extracting");
            var staged = Path.Combine(work, "component");
            Extract(archive, staged, rid);
            File.Delete(archive);

            Report("verifying the component");
            var problems = ExportComponent.Verify(staged, rid);
            if (problems.Count > 0)
                throw new ExportInstallException($"{zip} is not a whole export component, so nothing was installed:{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", problems));
            File.WriteAllText(Path.Combine(staged, "release-version"), version);

            Report("installing");
            Place(staged, target, work);
            var manifest = ExportComponent.ReadManifest(target)!;
            return new ExportInstallResult(target, version, rid, manifest.Chromium, manifest.Fonts, false, downloaded, ExportPins.Compare(target, manifest));
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a leftover temporary folder is harmless */ }
        }
    }

    /// <summary>The release a folder was installed from (what <c>release-version</c> says), or null.</summary>
    public static string? ReadVersion(string folder)
    {
        try { return File.ReadAllText(Path.Combine(folder, "release-version")).Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private (Uri Sums, Uri Zip) Locate(string? source, string version, string zip)
    {
        source ??= environment?.Invoke() ?? Environment.GetEnvironmentVariable(SourceVariable);
        if (string.IsNullOrWhiteSpace(source)) source = UpgradeFeedSettings.FromEnvironment() is { Mirror: { Length: > 0 } mirror } ? mirror : null;
        if (source is null)
        {
            var repo = UpgradeFeedSettings.FromEnvironment().Repo;
            source = $"https://github.com/{repo}/releases/download/dbdatagateway/v{version}";
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var address) && address.Scheme is "http" or "https")
        {
            if (address.Scheme != "https") throw new ExportInstallException($"'{source}' is not https: the component is downloaded over https, or read from a folder.");
            var baseUri = new Uri(source.TrimEnd('/') + "/");
            return (new Uri(baseUri, "SHA256SUMS"), new Uri(baseUri, zip));
        }

        var folder = address is { IsFile: true } ? address.LocalPath : Path.GetFullPath(source);
        if (!Directory.Exists(folder)) throw new ExportInstallException($"The release folder '{folder}' does not exist.");
        var mirrored = Path.Combine(folder, "dbdatagateway", "v" + version);
        if (File.Exists(Path.Combine(mirrored, "SHA256SUMS")) || File.Exists(Path.Combine(mirrored, zip))) folder = mirrored;
        if (!File.Exists(Path.Combine(folder, "SHA256SUMS"))) throw new ExportInstallException($"{Path.Combine(folder, "SHA256SUMS")} does not exist: the folder holds the release's files (SHA256SUMS and {zip}), as the release has them.");
        return (new Uri(Path.Combine(folder, "SHA256SUMS")), new Uri(Path.Combine(folder, zip)));
    }

    private static string Name(Uri uri) => uri.IsFile ? uri.LocalPath : uri.AbsoluteUri;

    private async Task<IReadOnlyDictionary<string, string>> ReadSumsAsync(Uri location, CancellationToken cancellationToken)
    {
        byte[] bytes;
        try { bytes = await fetcher.ReadAllBytesAsync(location, 4 * 1024 * 1024, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            throw new ExportInstallException($"{Name(location)} could not be read: {ex.Message}", ex);
        }
        try { return ReleaseSums.Parse(System.Text.Encoding.UTF8.GetString(bytes), Name(location)); }
        catch (FeedException ex) { throw new ExportInstallException(ex.Message, ex); }
    }

    private async Task<(long Bytes, string Sha256)> DownloadAsync(Uri location, string file, Action<long, long> progress, CancellationToken cancellationToken)
    {
        await using var source = await fetcher.OpenReadAsync(location, 0, cancellationToken).ConfigureAwait(false);
        var total = source.CanSeek ? source.Length : location.IsFile && File.Exists(location.LocalPath) ? new FileInfo(location.LocalPath).Length : 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var destination = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920];
        long count = 0, lastReported = 0;
        int n;
        while ((n = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            count += n;
            if (count > MaxArchiveBytes) throw new ExportInstallException($"{Name(location)} is larger than {MaxArchiveBytes / (1024 * 1024)} MB; it is not an export component.");
            hash.AppendData(buffer, 0, n);
            await destination.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
            if (count - lastReported >= 4 * 1024 * 1024) { lastReported = count; progress(count, total); }
        }
        progress(count, total == 0 ? count : total);
        return (count, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static void Extract(string archive, string destination, string rid)
    {
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        try
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries)
            {
                var path = Path.GetFullPath(Path.Combine(destination, entry.FullName));
                if (!path.StartsWith(root, StringComparison.Ordinal)) throw new ExportInstallException($"The archive holds '{entry.FullName}', which is outside the folder it is extracted to; nothing was installed.");
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(path); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path, true);
                if (!OperatingSystem.IsWindows() && ((entry.ExternalAttributes >> 16) & 0x49) != 0) // an execute bit the archive carries
                    File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
        }
        catch (InvalidDataException ex) { throw new ExportInstallException($"The archive is not a zip file ({ex.Message}); nothing was installed.", ex); }
        if (!OperatingSystem.IsWindows() && rid == "linux-x64")
            foreach (var chrome in Directory.EnumerateFiles(Path.Combine(destination, ".chromium", rid), "chrome", SearchOption.AllDirectories).Take(1))
                File.SetUnixFileMode(chrome, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    /// <summary>Moves the verified folder to <paramref name="target"/>; an older component there goes aside first, so a failure leaves the one that worked.</summary>
    private static void Place(string staged, string target, string work)
    {
        string? aside = null;
        if (Directory.Exists(target))
        {
            aside = Path.Combine(work, "previous");
            try { Directory.Move(target, aside); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ExportInstallException($"The installed component in {target} could not be replaced ({ex.Message}); an export may be running from it. Stop the gateway and install again. Nothing was changed.", ex);
            }
        }
        try { Directory.Move(staged, target); }
        catch
        {
            if (aside is not null && !Directory.Exists(target)) Directory.Move(aside, target);
            throw;
        }
    }
}
