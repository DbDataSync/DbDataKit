using System.Globalization;
using System.Text;
using System.Text.Json;
using DbDataKit.Update;

namespace DbDataKit.Upgrade;

/// <summary>The platform name a release's zip carries (<c>win-x64</c>, <c>linux-x64</c>, ...), as <c>dist/README.md</c> names them.</summary>
public static class PlatformRid
{
    public static string Current { get; } =
        (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux") + "-" + (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64");
}

/// <summary>A file of a release (a zip, the MSI, <c>SHA256SUMS</c>).</summary>
public sealed record FeedAsset(string Name, Uri Url, long Size);

/// <summary>One release of one product as the feed lists it: the tag <c>&lt;product&gt;/v&lt;version&gt;</c> of the suite's repository.</summary>
public sealed record FeedRelease(string Product, string Version, ReleaseVersion Parsed, DateTimeOffset? Published, Uri? NotesUrl, IReadOnlyList<FeedAsset> Assets, Uri? Base)
{
    public Channel Channel => Parsed.Channel;

    public FeedAsset? Asset(string name) => Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The zip of a platform: <c>&lt;product&gt;-v&lt;version&gt;-&lt;rid&gt;.zip</c> (dist/README.md).</summary>
    public string ZipName(string rid) => $"{Product}-v{Version}-{rid}.zip";
}

/// <summary>What a release says beyond its listing: the checksums of its files and, when its <c>release.json</c> is there, the compatibility generation it was built with.</summary>
public sealed record FeedReleaseDetails(FeedRelease Release, IReadOnlyDictionary<string, string> Sha256, int? Generation, string Source);

/// <summary>The feed could not be read or is not what it should be; the message names the source (the address, the folder or the file).</summary>
public sealed class FeedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The lines of a <c>SHA256SUMS</c> (<c>sha256sum</c>'s output: a hash, a space, <c>*</c> or a space, a name).</summary>
public static class ReleaseSums
{
    public static IReadOnlyDictionary<string, string> Parse(string text, string source)
    {
        var sums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var number = 0;
        foreach (var raw in text.Split('\n'))
        {
            number++;
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var space = line.IndexOf(' ');
            if (space != 64 || !line[..64].All(Uri.IsHexDigit)) throw new FeedException($"{source}, line {number}: not a checksum line (a 64-digit SHA-256, a space, the file's name).");
            var name = line[(space + 1)..].TrimStart(' ', '*');
            if (name.Length == 0) throw new FeedException($"{source}, line {number}: the checksum names no file.");
            sums[name] = line[..64].ToLowerInvariant();
        }

        return sums;
    }
}

/// <summary>
/// The release feed reader (plan 24a UC08, QU2): the releases of the suite as GitHub lists them, or a mirror's. It lists what is there and reads the checksums of one release when asked (<see cref="DetailsAsync"/>); it
/// never downloads a program. A listing that is not what it should be is refused with the source's name (<see cref="FeedException"/>), never read as "no releases".
/// </summary>
public sealed class ReleaseFeed(UpgradeFeedSettings settings, IContentFetcher fetcher)
{
    public const string ReleasesFile = "releases.json";

    public UpgradeFeedSettings Settings => settings;

    public IContentFetcher Fetcher => fetcher;

    /// <summary>The address or the folder the feed is read from, for the screen.</summary>
    public string Source => settings.Mirror ?? $"{settings.ApiBase.TrimEnd('/')}/repos/{settings.Repo}/releases";

    public static ReleaseFeed Create(UpgradeFeedSettings settings, HttpClient? http = null)
    {
        http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        if (http.DefaultRequestHeaders.UserAgent.Count == 0) http.DefaultRequestHeaders.UserAgent.ParseAdd("dbdatakit-upgrade");
        if (!http.DefaultRequestHeaders.Accept.Any()) http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return new ReleaseFeed(settings, new CompositeContentFetcher(new HttpContentFetcher(http), new FileContentFetcher()));
    }

    /// <summary>The prefix of the addresses a program may be downloaded from (<see cref="TrustedUrlPolicy"/>): the repository's release downloads, the mirror's folder or the mirror's address.</summary>
    public string TrustedPrefix => settings.Mirror is null
        ? $"https://github.com/{settings.Repo}/releases/download/"
        : MirrorIsAddress ? MirrorAddress().AbsoluteUri : new Uri(Path.GetFullPath(settings.Mirror).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).AbsoluteUri;

    private bool MirrorIsAddress => settings.Mirror is { } m && (m.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || m.StartsWith("http://", StringComparison.OrdinalIgnoreCase));

    private Uri MirrorAddress() => new(settings.Mirror!.TrimEnd('/') + "/");

    /// <summary>Every release of the feed at or above the configured channel, newest first; with <paramref name="product"/> only that product's.</summary>
    public async Task<IReadOnlyList<FeedRelease>> ListAsync(string? product = null, CancellationToken ct = default)
    {
        var all = settings.Mirror is { } mirror && !MirrorIsAddress && !File.Exists(Path.Combine(mirror, ReleasesFile))
            ? ListFolder(mirror)
            : await ListJsonAsync(ct);
        return [.. all.Where(r => (product is null || string.Equals(r.Product, product, StringComparison.OrdinalIgnoreCase)) && r.Channel <= settings.ParsedChannel).OrderByDescending(r => r.Parsed)];
    }

    /// <summary>The newest release of <paramref name="product"/> in the feed, or null when it lists none.</summary>
    public async Task<FeedRelease?> NewestAsync(string product, CancellationToken ct = default) => (await ListAsync(product, ct)).FirstOrDefault();

    /// <summary>The checksums (the release's <c>SHA256SUMS</c>, required) and the generation (its <c>release.json</c>, when it has one) of a release.</summary>
    public async Task<FeedReleaseDetails> DetailsAsync(FeedRelease release, CancellationToken ct = default)
    {
        var sumsAsset = release.Asset("SHA256SUMS") ?? throw new FeedException($"The release {release.Product} {release.Version} in {Source} has no SHA256SUMS, so nothing it holds can be checked.");
        var sums = ReleaseSums.Parse(Encoding.UTF8.GetString(await ReadAsync(sumsAsset.Url, ct)), sumsAsset.Url.IsFile ? sumsAsset.Url.LocalPath : sumsAsset.Url.AbsoluteUri);
        int? generation = null;
        if (release.Asset("release.json") is { } manifest)
        {
            // informational: the generation the release was built with (24a D1). It is not what a program is trusted by (the checksums are), so a file that cannot be read is refused, not skipped.
            var bytes = await ReadAsync(manifest.Url, ct);
            try
            {
                var parsed = ReleaseManifest.Parse(bytes);
                if (parsed.SchemaVersions.TryGetValue("generation", out var g) && g >= 1) generation = g;
            }
            catch (FormatException ex) { throw new FeedException($"{(manifest.Url.IsFile ? manifest.Url.LocalPath : manifest.Url.AbsoluteUri)} is not a release/1 manifest: {ex.Message}", ex); }
        }

        return new FeedReleaseDetails(release, sums, generation, Source);
    }

    private async Task<byte[]> ReadAsync(Uri location, CancellationToken ct)
    {
        try { return await fetcher.ReadAllBytesAsync(location, 4 * 1024 * 1024, ct); }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            throw new FeedException($"{(location.IsFile ? location.LocalPath : location.AbsoluteUri)} could not be read: {ex.Message}", ex);
        }
    }

    // ---- the GitHub shape (the API, or a mirror's releases.json)

    private async Task<IReadOnlyList<FeedRelease>> ListJsonAsync(CancellationToken ct)
    {
        Uri location;
        Uri? baseUri = null;
        if (settings.Mirror is null) location = new Uri($"{settings.ApiBase.TrimEnd('/')}/repos/{settings.Repo}/releases?per_page=100");
        else if (MirrorIsAddress) { baseUri = MirrorAddress(); location = new Uri(baseUri, ReleasesFile); }
        else { baseUri = new Uri(Path.GetFullPath(settings.Mirror).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar); location = new Uri(baseUri, ReleasesFile); }
        var name = location.IsFile ? location.LocalPath : location.AbsoluteUri;
        var bytes = await ReadAsync(location, ct);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new FeedException($"{name} is not a list of releases (a JSON array, as GitHub's releases call answers).");
            var list = new List<FeedRelease>();
            var index = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                index++;
                if (item.ValueKind != JsonValueKind.Object) throw new FeedException($"{name}: entry {index} is not a release (an object).");
                if (item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
                if (!item.TryGetProperty("tag_name", out var tagElement) || tagElement.ValueKind != JsonValueKind.String) throw new FeedException($"{name}: entry {index} has no tag_name.");
                if (!TryTag(tagElement.GetString()!, out var product, out var parsed)) continue; // a tag that is not <product>/v<version> (the <product>/dist releases) is not a version
                var assets = new List<FeedAsset>();
                if (item.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
                    foreach (var asset in assetsElement.EnumerateArray())
                    {
                        var assetName = asset.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                        var url = asset.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                        if (assetName is null || url is null) throw new FeedException($"{name}: an asset of {tagElement.GetString()} has no name or no browser_download_url.");
                        var absolute = Uri.TryCreate(url, UriKind.Absolute, out var abs) ? abs : baseUri is not null && Uri.TryCreate(baseUri, url, out var rel) ? rel : throw new FeedException($"{name}: the address '{url}' of {assetName} is not an address.");
                        assets.Add(new FeedAsset(assetName, absolute, asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var size) ? size : 0));
                    }

                DateTimeOffset? published = item.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(p.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) ? when : null;
                Uri? notes = item.TryGetProperty("html_url", out var h) && h.ValueKind == JsonValueKind.String && Uri.TryCreate(h.GetString(), UriKind.Absolute, out var notesUrl) ? notesUrl : null;
                list.Add(new FeedRelease(product, parsed.ToString(), parsed, published, notes, assets, baseUri));
            }

            return list;
        }
        catch (JsonException ex) { throw new FeedException($"{name} is not valid JSON: {ex.Message}", ex); }
    }

    // ---- the folder layout: <mirror>/<product>/v<version>/<files>

    private IReadOnlyList<FeedRelease> ListFolder(string mirror)
    {
        if (!Directory.Exists(mirror)) throw new FeedException($"The mirror folder '{mirror}' does not exist.");
        var list = new List<FeedRelease>();
        foreach (var productDirectory in Directory.EnumerateDirectories(mirror))
            foreach (var releaseDirectory in Directory.EnumerateDirectories(productDirectory))
            {
                var product = Path.GetFileName(productDirectory);
                var folder = Path.GetFileName(releaseDirectory);
                if (!TryTag($"{product}/{folder}", out _, out var parsed))
                    throw new FeedException($"The mirror folder '{releaseDirectory}' is not named v<version> (the layout is <product>/v<version>/).");
                var assets = Directory.EnumerateFiles(releaseDirectory).Select(f => new FeedAsset(Path.GetFileName(f), new Uri(f), new FileInfo(f).Length)).ToList();
                list.Add(new FeedRelease(product, parsed.ToString(), parsed, Directory.GetLastWriteTimeUtc(releaseDirectory), null, assets, new Uri(releaseDirectory + Path.DirectorySeparatorChar)));
            }

        return list;
    }

    /// <summary><c>dbdatagateway/v2026.11.9.1430</c> is a product and a version; <c>dbdatagateway/dist</c> and a tag without a product are not.</summary>
    public static bool TryTag(string tag, out string product, out ReleaseVersion version)
    {
        product = "";
        version = null!;
        var slash = tag.IndexOf('/');
        if (slash <= 0 || slash == tag.Length - 1 || tag[slash + 1] != 'v') return false;
        if (!ReleaseVersion.TryParse(tag[(slash + 1)..], out var parsed)) return false;
        product = tag[..slash];
        version = parsed;
        return true;
    }
}
