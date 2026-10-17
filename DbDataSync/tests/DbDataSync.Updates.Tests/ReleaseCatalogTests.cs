using System.Globalization;
using System.Net;

namespace DbDataSync.Updates.Tests;

/// <summary>
/// The GitHub fixture is **synthetic**, shaped exactly like the release objects GitHub returns for the DbDataKit
/// monorepo (trimmed to the fields the catalog reads): valid DbDataSync snapshot, stable and beta releases, plus
/// releases the catalog must skip — other products' (<c>dbdataproxy/…</c>, <c>dbdatabuild/…</c>), a DbDataSync tag carrying
/// another product's assets, a release whose prerelease flag disagrees with its version, the retired <c>snapshot-</c> tag form, a draft,
/// one with no checksum asset, an asset hosted somewhere else, a wrong asset name, a scratch tag, and a tag with
/// no product prefix at all.
/// </summary>
public class ReleaseCatalogTests
{
    private const string GitHubPage = ReleaseSources.GitHubReleasesUrl + "?per_page=100&page=";

    private static ReleaseCatalog Catalog(FakeHttpHandler handler, string? token = null) => new(handler.Client(), token);

    private static FakeHttpHandler Serving(string github) => new(request =>
        request.RequestUri!.AbsoluteUri switch
        {
            var url when url.StartsWith(ReleaseSources.GitHubReleasesUrl, StringComparison.Ordinal) => Http.Ok(github),
            _ => Http.Status(HttpStatusCode.NotFound),
        });

    private static FakeHttpHandler Fixtures() => Serving(Fixture.Read("github-releases.json"));

    [Fact]
    public async Task Stable_ListsTheStableReleases_NewestFirst_WithTheirDownloadUrls()
    {
        var releases = await Catalog(Fixtures()).ListAsync(ReleaseChannel.Stable, 10);

        Assert.Equal(
            ["2026.9.18.1918", "2026.9.16.1005", "2026.9.16.506", "2026.9.12.547", "2026.9.11.532"],
            releases.Select(r => r.Version.Text));
        Assert.All(releases, r =>
        {
            Assert.Equal(ReleaseChannel.Stable, r.Channel);
            Assert.Equal(
                $"https://github.com/DbDataSync/DbDataKit/releases/download/dbdatasync/v{r.Version.Text}/DbDataSync.{r.Version.Text}.nupkg",
                r.PackageUrl!.AbsoluteUri);
            Assert.EndsWith(".nupkg.sha512", r.ChecksumUrl!.AbsoluteUri);
        });
    }

    [Fact]
    public async Task Beta_ListsOnlyBetaVersions()
    {
        var releases = await Catalog(Fixtures()).ListAsync(ReleaseChannel.Beta, 10);

        Assert.Equal(
            ["2026.9.12.721-beta", "2026.9.11.450-beta", "2026.9.11.425-beta"],
            releases.Select(r => r.Version.Text));
    }

    [Fact]
    public async Task Limit_KeepsTheNewest()
    {
        var releases = await Catalog(Fixtures()).ListAsync(ReleaseChannel.Stable, 2);

        Assert.Equal(["2026.9.18.1918", "2026.9.16.1005"], releases.Select(r => r.Version.Text));
    }

    [Fact]
    public async Task Limit_BelowOne_IsRefused()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Catalog(Fixtures()).ListAsync(ReleaseChannel.Stable, 0));
    }

    [Fact]
    public async Task AReleaseIsSkippedUnlessItIsThisProductsAndWellFormed()
    {
        // Stable: only the five real ones survive. Not here: another product's releases, our tag with another product's
        // assets, a stable version flagged prerelease, a draft, a missing checksum and an unprefixed tag.
        var stable = await Catalog(Fixtures()).ListAsync(ReleaseChannel.Stable, 100);
        Assert.Equal(5, stable.Count);
        Assert.All(stable, r => Assert.StartsWith("2026.9.", r.Version.Text, StringComparison.Ordinal));

        // Beta: a beta version that is not flagged prerelease is skipped, and a release tag carrying a snapshot version
        // is neither a beta nor a snapshot.
        var beta = await Catalog(Fixtures()).ListAsync(ReleaseChannel.Beta, 100);
        Assert.Equal(3, beta.Count);
        Assert.DoesNotContain(await Catalog(Fixtures()).ListAsync(ReleaseChannel.Snapshot, 100), r => r.Version.Text.Contains("cafe123", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("2026.10.1.1000")]
    [InlineData("2026.10.2.0900")]
    [InlineData("2026.10.1.0800-snapshot.g1111111")]
    public async Task AnotherProductsReleases_NeverAppearInAnyChannel(string version)
    {
        foreach (var channel in Enum.GetValues<ReleaseChannel>())
        {
            var releases = await Catalog(Fixtures()).ListAsync(channel, 100);
            Assert.DoesNotContain(releases, r => r.Version.Text == version);
        }
    }

    [Fact]
    public async Task Snapshot_ListsTheValidOnes_NewestFirst_WithTheirDownloadUrls()
    {
        var releases = await Catalog(Fixtures()).ListAsync(ReleaseChannel.Snapshot, 10);

        Assert.Equal(
            ["2026.9.20.0930-snapshot.g0123456", "2026.9.19.1615-snapshot.gabcdef1", "2026.9.19.1432-snapshot.g65615e7"],
            releases.Select(r => r.Version.Text));

        var oldest = releases[^1];
        const string baseUrl = "https://github.com/DbDataSync/DbDataKit/releases/download/dbdatasync/v2026.9.19.1432-snapshot.g65615e7/";
        Assert.Equal(baseUrl + "DbDataSync.2026.9.19.1432-snapshot.g65615e7.nupkg", oldest.PackageUrl!.AbsoluteUri);
        Assert.Equal(baseUrl + "DbDataSync.2026.9.19.1432-snapshot.g65615e7.nupkg.sha512", oldest.ChecksumUrl!.AbsoluteUri);
        Assert.Equal("https://github.com/DbDataSync/DbDataKit/releases/tag/dbdatasync/v2026.9.19.1432-snapshot.g65615e7", oldest.ReleaseUrl!.AbsoluteUri);
    }

    [Fact]
    public async Task Snapshot_SkipsEveryReleaseThatIsNotAWellFormedSnapshot()
    {
        var releases = await Catalog(Fixtures()).ListAsync(ReleaseChannel.Snapshot, 100);

        // Only the three real ones survive: draft, non-prerelease, missing checksum, foreign host, wrong
        // asset name, scratch tag and a non-snapshot tag are all dropped, as are the stable/beta releases.
        Assert.Equal(3, releases.Count);
        Assert.DoesNotContain(releases, r => r.Version.Text.Contains("deadbee", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Snapshot_SendsAUserAgent_AndAToken_OnlyWhenOneWasGiven()
    {
        var anonymous = Fixtures();
        await Catalog(anonymous).ListAsync(ReleaseChannel.Snapshot, 5);

        var sent = Assert.Single(anonymous.Sent);
        Assert.Equal(GitHubPage + "1", sent.Uri.AbsoluteUri);
        Assert.Equal("DbDataSync-update", sent.UserAgent);
        Assert.Contains("application/vnd.github+json", sent.Accept);
        Assert.Null(sent.Authorization);

        var authenticated = Fixtures();
        await Catalog(authenticated, token: "ghp_example").ListAsync(ReleaseChannel.Snapshot, 5);

        Assert.Equal("Bearer ghp_example", Assert.Single(authenticated.Sent).Authorization);
    }

    [Fact]
    public async Task Snapshot_StopsAtAShortPage_WithoutAskingForMore()
    {
        var handler = Fixtures();
        await Catalog(handler).ListAsync(ReleaseChannel.Snapshot, 50);

        Assert.Single(handler.Sent);
    }

    [Fact]
    public async Task Snapshot_ReadsFurtherPagesUntilItHasEnough_ButNeverMoreThanThree()
    {
        // Every page is full (100 entries) of releases that are not snapshots, so the catalog can never
        // be satisfied and has to give up at its page ceiling rather than walk the whole repository.
        var fullPage = "[" + string.Join(",", Enumerable.Range(0, 100).Select(i =>
            $$"""{"tag_name":"2026.1.1.{{i + 1}}","draft":false,"prerelease":false,"assets":[]}""")) + "]";
        var handler = new FakeHttpHandler(_ => Http.Ok(fullPage));

        var releases = await Catalog(handler).ListAsync(ReleaseChannel.Snapshot, 5);

        Assert.Empty(releases);
        Assert.Equal(
            [GitHubPage + "1", GitHubPage + "2", GitHubPage + "3"],
            handler.Sent.Select(s => s.Uri.AbsoluteUri));
    }

    [Fact]
    public async Task Snapshot_FollowsToASecondPage_WhenTheFirstIsFullButHasTooFew()
    {
        var pageOne = "[" + string.Join(",", Enumerable.Range(0, 100).Select(i =>
            $$"""{"tag_name":"2026.1.1.{{i + 1}}","draft":false,"prerelease":false,"assets":[]}""")) + "]";
        var handler = new FakeHttpHandler(request =>
            request.RequestUri!.AbsoluteUri.EndsWith("page=1", StringComparison.Ordinal) ? Http.Ok(pageOne) : Http.Ok(Fixture.Read("github-releases.json")));

        var releases = await Catalog(handler).ListAsync(ReleaseChannel.Snapshot, 10);

        Assert.Equal(3, releases.Count);
        Assert.Equal(2, handler.Sent.Count);
    }

    [Fact]
    public async Task GitHub_AnUnexpectedBody_IsAnError()
    {
        var handler = new FakeHttpHandler(_ => Http.Ok("""{"message":"Not Found"}"""));

        await Assert.ThrowsAsync<ReleaseSourceException>(() => Catalog(handler).ListAsync(ReleaseChannel.Snapshot, 5));
    }

    [Fact]
    public async Task GitHub_RateLimited_SaysWhenItResets_AndNamesTheTokenVariable()
    {
        var reset = new DateTimeOffset(2026, 9, 19, 15, 42, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var handler = new FakeHttpHandler(_ =>
        {
            var response = Http.Status(HttpStatusCode.Forbidden);
            response.Headers.Add("x-ratelimit-remaining", "0");
            response.Headers.Add("x-ratelimit-reset", reset.ToString(CultureInfo.InvariantCulture));
            return response;
        });

        var ex = await Assert.ThrowsAsync<ReleaseSourceException>(() => Catalog(handler).ListAsync(ReleaseChannel.Snapshot, 5));

        Assert.Contains("60 requests an hour", ex.Message);
        Assert.Contains("15:42 UTC", ex.Message);
        Assert.Contains("GITHUB_TOKEN", ex.Message);
    }

    [Fact]
    public async Task GitHub_RateLimited_WithAToken_DoesNotSuggestSettingOne()
    {
        var reset = new DateTimeOffset(2026, 9, 19, 15, 42, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var handler = new FakeHttpHandler(_ =>
        {
            var response = Http.Status(HttpStatusCode.Forbidden);
            response.Headers.Add("x-ratelimit-remaining", "0");
            response.Headers.Add("x-ratelimit-reset", reset.ToString(CultureInfo.InvariantCulture));
            return response;
        });

        var ex = await Assert.ThrowsAsync<ReleaseSourceException>(() => Catalog(handler, token: "ghp_example").ListAsync(ReleaseChannel.Snapshot, 5));

        Assert.Contains("15:42 UTC", ex.Message);
        Assert.DoesNotContain("GITHUB_TOKEN", ex.Message);
    }

    [Fact]
    public async Task GitHub_ASecondaryLimit_ReportsTheRequestedPause()
    {
        var handler = new FakeHttpHandler(_ =>
        {
            var response = Http.Status(HttpStatusCode.TooManyRequests);
            response.Headers.Add("Retry-After", "45");
            return response;
        });

        var ex = await Assert.ThrowsAsync<ReleaseSourceException>(() => Catalog(handler).ListAsync(ReleaseChannel.Snapshot, 5));

        Assert.Contains("45 seconds", ex.Message);
    }

    [Fact]
    public async Task GitHub_AForbiddenThatIsNotARateLimit_IsJustReportedAsAStatus()
    {
        var handler = new FakeHttpHandler(_ => Http.Status(HttpStatusCode.Forbidden));

        var ex = await Assert.ThrowsAsync<ReleaseSourceException>(() => Catalog(handler).ListAsync(ReleaseChannel.Snapshot, 5));

        Assert.Contains("GitHub answered 403", ex.Message);
    }

    [Fact]
    public async Task ANetworkFailure_NamesTheSourceThatCouldNotBeReached()
    {
        var handler = new FakeHttpHandler(_ => throw new HttpRequestException("Name or service not known"));

        var ex = await Assert.ThrowsAsync<ReleaseSourceException>(() => Catalog(handler).ListAsync(ReleaseChannel.Stable, 5));

        Assert.Contains("Could not reach GitHub", ex.Message);
        Assert.Contains("Name or service not known", ex.Message);
    }

    [Fact]
    public async Task ATimeout_IsAnError_ButACancellationIsNot()
    {
        var timedOut = new FakeHttpHandler(_ => throw new TaskCanceledException("timed out"));
        var ex = await Assert.ThrowsAsync<ReleaseSourceException>(() => Catalog(timedOut).ListAsync(ReleaseChannel.Stable, 5));
        Assert.Contains("did not answer in time", ex.Message);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Catalog(Fixtures()).ListAsync(ReleaseChannel.Stable, 5, cancelled.Token));
    }

    [Fact]
    public void TheSourcesArePinnedConstants()
    {
        Assert.Equal("https://api.github.com/repos/DbDataSync/DbDataKit/releases", ReleaseSources.GitHubReleasesUrl);
        Assert.Equal("dbdatasync/v", ReleaseSources.ReleaseTagPrefix);
        Assert.Equal("DbDataSync.2026.9.19.1432-snapshot.g65615e7.nupkg", ReleaseSources.PackageFileName(ReleaseVersion.Parse("2026.9.19.1432-snapshot.g65615e7")));
        Assert.EndsWith(".nupkg.sha512", ReleaseSources.ChecksumFileName(ReleaseVersion.Parse("2026.9.19.1432-snapshot.g65615e7")));
    }
}
