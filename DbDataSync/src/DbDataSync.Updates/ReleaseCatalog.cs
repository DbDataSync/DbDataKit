using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DbDataSync.Updates;

/// <summary>
/// Lists what can be installed, per channel, newest first — from the one place
/// <see cref="ReleaseSources"/> pins, and nowhere else: this repository's GitHub releases.
/// <para>
/// The repository publishes every product in the DbDataKit monorepo, so a release counts only if it is
/// DbDataSync's: its tag is <see cref="ReleaseSources.ReleaseTagPrefix"/> (stable and beta) or
/// <see cref="ReleaseSources.SnapshotTagPrefix"/> (snapshot) plus a version that parses and belongs to the channel
/// the tag says, its prerelease flag agrees with that channel, **and** it carries both assets a release is made of
/// (<see cref="ReleaseSources.PackageFileName"/> and its checksum), downloaded from under
/// <see cref="ReleaseSources.AssetDownloadPrefix"/>. Anything that fails a check is skipped, not reported — a release
/// listing is not the place to discover that another product's release, or a hand-made one, exists.
/// </para>
/// </summary>
/// <param name="gitHubToken">Optional. Raises GitHub's limit from 60 requests an hour per address; sent only
/// with the release-listing request, never with a download.</param>
public sealed class ReleaseCatalog(HttpClient http, string? gitHubToken = null, string userAgent = "DbDataSync-update")
{
    private const int PageSize = 100;
    private const int MaxPages = 3;

    // Every channel is read from the same release listing, so a page is fetched once per catalog and shared: listing all
    // three channels costs one request for a typical repository, not three — it matters against GitHub's anonymous limit.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<ReleaseInfo> _read = [];
    private int _pagesRead;
    private bool _exhausted;

    public async Task<IReadOnlyList<ReleaseInfo>> ListAsync(
        ReleaseChannel channel, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            while (_read.Count(r => r.Channel == channel) < limit && !_exhausted && _pagesRead < MaxPages)
                await ReadNextPageAsync(cancellationToken);

            return _read.Where(r => r.Channel == channel).OrderByDescending(r => r.Version).Take(limit).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ReadNextPageAsync(CancellationToken cancellationToken)
    {
        var page = _pagesRead + 1;
        using var request = NewRequest($"{ReleaseSources.GitHubReleasesUrl}?per_page={PageSize}&page={page}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (!string.IsNullOrEmpty(gitHubToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gitHubToken);

        using var response = await SendAsync(request, "GitHub", cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw GitHubFailure(response);

        using var document = await ReadJsonAsync(response, "GitHub", cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new ReleaseSourceException("GitHub's release list was not in the expected shape.");

        var count = 0;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            count++;
            if (TryReadRelease(release, out var info))
                _read.Add(info);
        }

        _pagesRead = page;
        if (count < PageSize)
            _exhausted = true;
    }

    private static bool TryReadRelease(JsonElement release, out ReleaseInfo info)
    {
        info = null!;
        if (release.ValueKind != JsonValueKind.Object)
            return false;

        if (GetBool(release, "draft"))
            return false;

        var tag = GetString(release, "tag_name");
        if (tag is null)
            return false;

        // The tag says which kind of release this is; the version has to agree with it.
        bool snapshotTag;
        string text;
        if (tag.StartsWith(ReleaseSources.ReleaseTagPrefix, StringComparison.Ordinal))
        {
            snapshotTag = false;
            text = tag[ReleaseSources.ReleaseTagPrefix.Length..];
        }
        else if (tag.StartsWith(ReleaseSources.SnapshotTagPrefix, StringComparison.Ordinal))
        {
            snapshotTag = true;
            text = tag[ReleaseSources.SnapshotTagPrefix.Length..];
        }
        else
        {
            return false;
        }

        if (!ReleaseVersion.TryParse(text, out var version) || version.Text != text || version.Channel is not { } channel)
            return false;

        if (snapshotTag != (channel == ReleaseChannel.Snapshot))
            return false;

        // Stable is the only channel that is not a prerelease; a mismatch means the release was made by hand.
        if (GetBool(release, "prerelease") != (channel != ReleaseChannel.Stable))
            return false;

        Uri? packageUrl = null, checksumUrl = null;
        if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = GetString(asset, "name");
                var url = GetString(asset, "browser_download_url");
                if (name is null || url is null || !url.StartsWith(ReleaseSources.AssetDownloadPrefix, StringComparison.Ordinal)
                    || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    continue;

                if (name == ReleaseSources.PackageFileName(version))
                    packageUrl = uri;
                else if (name == ReleaseSources.ChecksumFileName(version))
                    checksumUrl = uri;
            }
        }

        if (packageUrl is null || checksumUrl is null)
            return false;

        Uri? page = Uri.TryCreate(GetString(release, "html_url"), UriKind.Absolute, out var html) ? html : null;
        info = new ReleaseInfo(version, channel, packageUrl, checksumUrl, page);
        return true;
    }

    private HttpRequestMessage NewRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(userAgent);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string source, CancellationToken cancellationToken)
    {
        try
        {
            return await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new ReleaseSourceException($"Could not reach {source}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ReleaseSourceException($"{source} did not answer in time.", ex);
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, string source, CancellationToken cancellationToken)
    {
        try
        {
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new ReleaseSourceException($"{source} answered with something that was not JSON.", ex);
        }
    }

    private ReleaseSourceException GitHubFailure(HttpResponseMessage response)
    {
        var status = response.StatusCode;
        if (status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            if (Header(response, "x-ratelimit-remaining") == "0"
                && long.TryParse(Header(response, "x-ratelimit-reset"), NumberStyles.None, CultureInfo.InvariantCulture, out var reset))
            {
                var when = DateTimeOffset.FromUnixTimeSeconds(reset).UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
                return new ReleaseSourceException(string.IsNullOrEmpty(gitHubToken)
                    ? $"GitHub's anonymous API limit (60 requests an hour) is used up until {when} UTC. " +
                      "Set GITHUB_TOKEN (or GH_TOKEN) to raise it, or try again then."
                    : $"GitHub's API rate limit for that token is used up until {when} UTC.");
            }

            if (int.TryParse(Header(response, "retry-after"), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                return new ReleaseSourceException($"GitHub asked for a pause of {seconds} seconds before the next request.");
        }

        return new ReleaseSourceException($"GitHub answered {(int)status} when asked for {ReleaseSources.Repository}'s releases.");
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
