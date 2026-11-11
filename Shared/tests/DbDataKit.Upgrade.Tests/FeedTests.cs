namespace DbDataKit.Upgrade.Tests;

/// <summary>The release feed reader (24a UC08), over a mirror folder (what a site without internet uses) and the GitHub shape.</summary>
public sealed class FeedTests : IDisposable
{
    private readonly MirrorFolder _machine = new();

    private ReleaseFeed Feed(string? channel = null, string? mirror = null) => ReleaseFeed.Create(new UpgradeFeedSettings { Mirror = mirror ?? _machine.Mirror, Channel = channel ?? "stable" });

    [Fact]
    public async Task A_mirror_folder_lists_the_releases_of_a_product_newest_first_and_works_offline()
    {
        _machine.Release("2026.11.1.1000");
        _machine.Release("2026.11.9.1430");
        _machine.Release("2026.11.5.900");
        var releases = await Feed().ListAsync(MirrorFolder.Product);
        Assert.Equal(["2026.11.9.1430", "2026.11.5.900", "2026.11.1.1000"], releases.Select(r => r.Version));
        Assert.Equal("2026.11.9.1430", (await Feed().NewestAsync(MirrorFolder.Product))!.Version);
        Assert.Empty(await Feed().ListAsync("dbdataother"));
    }

    [Fact]
    public async Task The_channel_setting_decides_which_prereleases_are_offered()
    {
        _machine.Release("2026.11.1.1000");
        _machine.Release("2026.11.9.1430", channelSuffix: "-beta");
        Assert.Equal("2026.11.1.1000", (await Feed().NewestAsync(MirrorFolder.Product))!.Version);
        Assert.Equal("2026.11.9.1430-beta", (await Feed("beta").NewestAsync(MirrorFolder.Product))!.Version);
    }

    [Fact]
    public async Task The_checksums_and_the_generation_of_a_release_are_read()
    {
        var zip = _machine.Release(MirrorFolder.New, generation: 3);
        var feed = Feed();
        var details = await feed.DetailsAsync((await feed.NewestAsync(MirrorFolder.Product))!);
        Assert.Equal(3, details.Generation);
        Assert.Equal(64, details.Sha256[Path.GetFileName(zip)].Length);
    }

    [Fact]
    public async Task A_folder_that_is_not_named_for_a_version_is_refused_naming_it()
    {
        Directory.CreateDirectory(Path.Combine(_machine.Mirror, MirrorFolder.Product, "latest"));
        var ex = await Assert.ThrowsAsync<FeedException>(() => Feed().ListAsync(MirrorFolder.Product));
        Assert.Contains(Path.Combine(_machine.Mirror, MirrorFolder.Product, "latest"), ex.Message);
    }

    [Fact]
    public async Task A_malformed_checksum_file_is_refused_naming_the_file_and_the_line()
    {
        _machine.Release(MirrorFolder.New);
        var sums = Path.Combine(_machine.Mirror, MirrorFolder.Product, "v" + MirrorFolder.New, "SHA256SUMS");
        File.WriteAllText(sums, "nothing like a checksum\n");
        var feed = Feed();
        var ex = await Assert.ThrowsAsync<FeedException>(async () => await feed.DetailsAsync((await feed.NewestAsync(MirrorFolder.Product))!));
        Assert.Contains(sums, ex.Message);
        Assert.Contains("line 1", ex.Message);
    }

    [Fact]
    public async Task A_release_without_checksums_is_refused()
    {
        _machine.Release(MirrorFolder.New);
        File.Delete(Path.Combine(_machine.Mirror, MirrorFolder.Product, "v" + MirrorFolder.New, "SHA256SUMS"));
        var feed = Feed();
        var ex = await Assert.ThrowsAsync<FeedException>(async () => await feed.DetailsAsync((await feed.NewestAsync(MirrorFolder.Product))!));
        Assert.Contains("no SHA256SUMS", ex.Message);
    }

    [Fact]
    public async Task A_releases_json_in_the_shape_of_the_github_answer_is_read_and_a_malformed_one_is_refused_naming_it()
    {
        var folder = Path.Combine(_machine.Root, "api");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "releases.json");
        File.WriteAllText(file, """
            [
              {"tag_name":"dbdatatest/v2026.11.9.1430","draft":false,"published_at":"2026-11-09T14:30:00Z","html_url":"https://example.invalid/notes","assets":[{"name":"SHA256SUMS","browser_download_url":"SHA256SUMS","size":10}]},
              {"tag_name":"dbdatatest/dist","assets":[]},
              {"tag_name":"dbdatatest/v2026.11.10.0900","draft":true,"assets":[]}
            ]
            """);
        var releases = await Feed(mirror: folder).ListAsync(MirrorFolder.Product);
        var release = Assert.Single(releases);
        Assert.Equal("2026.11.9.1430", release.Version);
        Assert.Equal("https://example.invalid/notes", release.NotesUrl!.AbsoluteUri);
        Assert.StartsWith("file:", release.Asset("SHA256SUMS")!.Url.AbsoluteUri);

        File.WriteAllText(file, "{\"not\": \"a list\"}");
        var ex = await Assert.ThrowsAsync<FeedException>(() => Feed(mirror: folder).ListAsync(MirrorFolder.Product));
        Assert.Contains(file, ex.Message);
        File.WriteAllText(file, "[ {\"draft\": false} ]");
        Assert.Contains("no tag_name", (await Assert.ThrowsAsync<FeedException>(() => Feed(mirror: folder).ListAsync(MirrorFolder.Product))).Message);
        File.WriteAllText(file, "[ nope");
        Assert.Contains("not valid JSON", (await Assert.ThrowsAsync<FeedException>(() => Feed(mirror: folder).ListAsync(MirrorFolder.Product))).Message);
    }

    [Fact]
    public async Task A_missing_mirror_is_refused_not_read_as_no_releases()
    {
        var ex = await Assert.ThrowsAsync<FeedException>(() => Feed(mirror: Path.Combine(_machine.Root, "nowhere")).ListAsync());
        Assert.Contains("nowhere", ex.Message);
    }

    [Fact]
    public void The_feed_setting_is_read_from_settings_yaml_and_a_key_that_is_not_a_setting_is_refused_with_the_file_and_line()
    {
        var file = Path.Combine(_machine.Root, "settings.yaml");
        Directory.CreateDirectory(_machine.Root);
        Assert.Equal(UpgradeFeedSettings.Default, UpgradeFeedSettings.Load(file));
        File.WriteAllText(file, "state:\n  engine: sqlite\nupgrade:\n  feed:\n    mirror: /srv/releases\n    channel: beta\n");
        var settings = UpgradeFeedSettings.Load(file);
        Assert.Equal("/srv/releases", settings.Mirror);
        Assert.Equal("beta", settings.Channel);
        File.WriteAllText(file, "upgrade:\n  feed:\n    mirrr: /srv/releases\n");
        var ex = Assert.Throws<InvalidOperationException>(() => UpgradeFeedSettings.Load(file));
        Assert.Contains(file, ex.Message);
        Assert.Contains("line 3", ex.Message);
        File.WriteAllText(file, "upgrade:\n  feed:\n    channel: nightly\n");
        Assert.Contains("channel", Assert.Throws<InvalidOperationException>(() => UpgradeFeedSettings.Load(file)).Message);
    }

    public void Dispose() => _machine.Dispose();
}
