using System.Net;
using System.Text;

namespace DbDataKit.Update.Tests;

public class CheckerPolicyTests
{
    private static ReleaseManifest M(string v = "1.5.0", Action<ReleaseManifest, Dictionary<string, object>>? _ = null) => new() { Product = "p", Version = v, Channel = ReleaseVersion.Parse(v).Channel.ToString().ToLowerInvariant() };
    private static UpdateDecision Eval(ReleaseManifest m, string installed, UpdateCheckOptions? o = null)
        => UpdateChecker.Evaluate(m, ReleaseVersion.Parse(installed), o ?? new(), "p");

    [Fact] public void NewerIsAvailable() => Assert.Equal(UpdateDecisionKind.UpdateAvailable, Eval(M("1.5.0"), "1.4.0").Kind);
    [Fact] public void SameIsUpToDate() => Assert.Equal(UpdateDecisionKind.UpToDate, Eval(M("1.4.0"), "1.4.0").Kind);

    [Fact]
    public void DowngradeBlockedByDefault()
    {
        var d = Eval(M("1.3.0"), "1.4.0");
        Assert.Equal(UpdateDecisionKind.Blocked, d.Kind);
        Assert.Equal(BlockReason.Downgrade, d.Reason);
    }

    [Fact]
    public void DowngradeAllowedExplicitly()
    {
        var d = Eval(M("1.3.0"), "1.4.0", new() { AllowDowngrade = true });
        Assert.True(d.CanApply);
        Assert.True(d.IsDowngrade);
    }

    [Fact]
    public void RevokedBlockedEvenIfNewer()
    {
        var d = Eval(M("1.5.0") with { Revoked = true }, "1.4.0");
        Assert.Equal(BlockReason.Revoked, d.Reason);
        Assert.False(d.CanApply);
    }

    [Fact]
    public void RevokedBlockedEvenWithAllowDowngrade()
        => Assert.Equal(BlockReason.Revoked, Eval(M("1.3.0") with { Revoked = true }, "1.4.0", new() { AllowDowngrade = true }).Reason);

    [Fact]
    public void BelowMinimumSafeIsMandatory()
    {
        var d = Eval(M("1.5.0") with { MinimumSafeVersion = "1.4.5" }, "1.4.0");
        Assert.Equal(UpdateDecisionKind.Mandatory, d.Kind);
    }

    [Fact]
    public void SupersededIsMandatory()
        => Assert.Equal(UpdateDecisionKind.Mandatory, Eval(M("1.5.0") with { Supersedes = ["1.4.0"] }, "1.4.0").Kind);

    [Fact]
    public void KnownMinimumSafeBlocksReplayOfOldManifest()
    {
        var d = Eval(M("1.4.2"), "1.4.0", new() { KnownMinimumSafeVersion = "1.4.5" });
        Assert.Equal(BlockReason.BelowMinimumSafe, d.Reason);
    }

    [Fact]
    public void StableInstallNeverSeesBetaButBetaSeesStable()
    {
        Assert.Equal(BlockReason.ChannelNotAllowed, Eval(M("2.0.0-beta.1"), "1.0.0").Reason);
        Assert.True(Eval(M("2.0.0"), "2.0.0-beta.1", new() { Channel = Channel.Beta }).CanApply);
        Assert.Equal(BlockReason.ChannelNotAllowed, Eval(M("2.0.0-snapshot.1"), "1.0.0", new() { Channel = Channel.Beta }).Reason);
        Assert.True(Eval(M("2.0.0-snapshot.1"), "1.0.0", new() { Channel = Channel.Snapshot }).CanApply);
    }

    [Fact]
    public void WrongProductBlocked() => Assert.Equal(BlockReason.WrongProduct, Eval(M() with { Product = "other" }, "1.0.0").Reason);

    [Fact]
    public void UpdaterTooOldBlocked()
        => Assert.Equal(BlockReason.UpdaterTooOld, Eval(M() with { MinUpdater = "2.0" }, "1.0.0", new() { UpdaterVersion = "1.9.0" }).Reason);

    [Fact]
    public void ManifestRejectsChannelVersionMismatch()
        => Assert.Throws<FormatException>(() => (M("1.0.0") with { Channel = "beta" }).Validate());

    [Fact]
    public void PolicyModes()
    {
        var avail = Eval(M("1.5.0"), "1.4.0");
        var now = new DateTimeOffset(2026, 1, 1, 3, 0, 0, TimeSpan.Zero);
        Assert.Equal(PolicyAction.None, UpdatePolicy.Decide(UpdateMode.Disabled, avail, now));
        Assert.Equal(PolicyAction.Notify, UpdatePolicy.Decide(UpdateMode.Notify, avail, now));
        Assert.Equal(PolicyAction.AwaitApproval, UpdatePolicy.Decide(UpdateMode.Approve, avail, now));
        var win = new UpdateWindow(new TimeOnly(2, 0), new TimeOnly(4, 0));
        Assert.Equal(PolicyAction.ApplyNow, UpdatePolicy.Decide(UpdateMode.Auto, avail, now, win));
        Assert.Equal(PolicyAction.Notify, UpdatePolicy.Decide(UpdateMode.Auto, avail, now.AddHours(5), win));
        Assert.Equal(PolicyAction.None, UpdatePolicy.Decide(UpdateMode.Auto, Eval(M("1.4.0"), "1.4.0"), now, win));
    }

    [Fact]
    public void WindowWrapsMidnightAndHonoursDays()
    {
        var w = new UpdateWindow(new TimeOnly(22, 0), new TimeOnly(2, 0));
        Assert.True(w.Contains(new DateTimeOffset(2026, 1, 1, 23, 0, 0, TimeSpan.Zero)));
        Assert.True(w.Contains(new DateTimeOffset(2026, 1, 2, 1, 0, 0, TimeSpan.Zero)));
        Assert.False(w.Contains(new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero)));
        var sun = new UpdateWindow(new TimeOnly(0, 0), new TimeOnly(0, 0), [DayOfWeek.Sunday]);
        Assert.False(sun.Contains(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero))); // Thursday
        Assert.True(sun.Contains(new DateTimeOffset(2026, 1, 4, 12, 0, 0, TimeSpan.Zero)));
    }
}

public class SourceAndCheckTests
{
    private static UpdateChecker Checker(KeyPair k) => new("dbdataproxy", new CompositeContentFetcher(null, new FileContentFetcher()), k.Trust());

    [Fact]
    public async Task FolderSource_PicksHighestAllowedVersion()
    {
        using var t = new TempDir();
        var k = new KeyPair("k1");
        var zip = TestKit.MakeZip(("a.txt", "x"));
        TestKit.WriteRelease(t.Path, k, "1.0.0", zip);
        TestKit.WriteRelease(t.Path, k, "1.2.0", zip);
        TestKit.WriteRelease(t.Path, k, "2.0.0-beta.1", zip);
        var d = await Checker(k).CheckAsync(new FolderReleaseSource(t.Path), ReleaseVersion.Parse("1.0.0"), new());
        Assert.Equal("1.2.0", d.Manifest!.Version);
        Assert.Equal(UpdateDecisionKind.UpdateAvailable, d.Kind);
        var beta = await Checker(k).CheckAsync(new FolderReleaseSource(t.Path), ReleaseVersion.Parse("1.0.0"), new() { Channel = Channel.Beta });
        Assert.Equal("2.0.0-beta.1", beta.Manifest!.Version);
    }

    [Fact]
    public async Task FolderSource_TamperedManifestFailsClosed()
    {
        using var t = new TempDir();
        var k = new KeyPair("k1");
        var (_, dir) = TestKit.WriteRelease(t.Path, k, "1.2.0", TestKit.MakeZip(("a", "x")));
        var p = Path.Combine(dir, "release.json");
        File.WriteAllText(p, File.ReadAllText(p).Replace("\"revoked\": false", "\"revoked\": false ") + " ");
        var ex = await Assert.ThrowsAsync<UpdateVerificationException>(() => Checker(k).CheckAsync(new FolderReleaseSource(t.Path), ReleaseVersion.Parse("1.0.0"), new()));
        Assert.Equal(VerificationFailure.BadSignature, ex.Failure);
    }

    [Fact]
    public async Task FolderSource_VersionSwapDetected()
    {
        using var t = new TempDir();
        var k = new KeyPair("k1");
        // A validly signed old release copied into the 9.0.0 folder (replay/rollback attempt).
        var (_, dir) = TestKit.WriteRelease(t.Path, k, "1.0.0", TestKit.MakeZip(("a", "x")));
        Directory.Move(dir, Path.Combine(t.Path, "9.0.0"));
        var ex = await Assert.ThrowsAsync<UpdateVerificationException>(() => Checker(k).CheckAsync(new FolderReleaseSource(t.Path), ReleaseVersion.Parse("1.0.0"), new()));
        Assert.Equal(VerificationFailure.VersionMismatch, ex.Failure);
    }

    [Fact]
    public async Task FolderSource_DowngradeAttemptBlocked()
    {
        using var t = new TempDir();
        var k = new KeyPair("k1");
        TestKit.WriteRelease(t.Path, k, "1.0.0", TestKit.MakeZip(("a", "x")));
        var d = await Checker(k).CheckAsync(new FolderReleaseSource(t.Path), ReleaseVersion.Parse("1.4.0"), new());
        Assert.Equal(BlockReason.Downgrade, d.Reason);
    }

    [Fact]
    public async Task GitHubSource_ParsesReleasesAndSkipsDraftsAndUnsigned()
    {
        const string json = """
        [
          {"tag_name":"v1.4.0","draft":false,"assets":[
             {"name":"release.json","browser_download_url":"https://github.com/o/r/releases/download/v1.4.0/release.json"},
             {"name":"release.json.sig","browser_download_url":"https://github.com/o/r/releases/download/v1.4.0/release.json.sig"}]},
          {"tag_name":"v1.5.0","draft":true,"assets":[
             {"name":"release.json","browser_download_url":"https://x/release.json"},
             {"name":"release.json.sig","browser_download_url":"https://x/release.json.sig"}]},
          {"tag_name":"v1.3.0","draft":false,"assets":[{"name":"app.zip","browser_download_url":"https://x/app.zip"}]},
          {"tag_name":"nightly","draft":false,"assets":[]}
        ]
        """;
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        var src = new GitHubReleaseSource("o", "r", new HttpClient(h));
        var refs = await src.ListAsync(default);
        var r = Assert.Single(refs);
        Assert.Equal("1.4.0", r.Version);
        Assert.EndsWith("/v1.4.0/release.json", r.ManifestLocation!.AbsoluteUri);
        Assert.Contains("/repos/o/r/releases", h.Requests[0].AbsoluteUri);
    }

    [Fact]
    public async Task GitHubSource_EndToEndThroughHttpFetcher()
    {
        var k = new KeyPair("k1");
        var m = new ReleaseManifest { Product = "dbdataproxy", Version = "1.4.0", Channel = "stable" };
        var bytes = ManifestBuilder.ToBytes(m);
        var sig = ManifestSigner.Sign(bytes, k.Seed, k.Id);
        var h = new StubHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/repos/o/r/releases" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
              [{"tag_name":"v1.4.0","assets":[
                {"name":"release.json","browser_download_url":"https://github.com/o/r/releases/download/v1.4.0/release.json"},
                {"name":"release.json.sig","browser_download_url":"https://github.com/o/r/releases/download/v1.4.0/release.json.sig"}]}]
              """) },
            "/o/r/releases/download/v1.4.0/release.json" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) },
            "/o/r/releases/download/v1.4.0/release.json.sig" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sig) },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var http = new HttpClient(h);
        var checker = new UpdateChecker("dbdataproxy", new HttpContentFetcher(http), k.Trust());
        var d = await checker.CheckAsync(new GitHubReleaseSource("o", "r", http), ReleaseVersion.Parse("1.3.0"), new());
        Assert.Equal(UpdateDecisionKind.UpdateAvailable, d.Kind);
    }

    [Fact]
    public async Task NuGetSource_ListsVersionsAsUnsignedHints()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"versions":["1.0.0","1.1.0-beta.1","junk"]}""") });
        var refs = await new NuGetFlatContainerSource("DbDataSync", new HttpClient(h)).ListAsync(default);
        Assert.Equal(2, refs.Count);
        Assert.All(refs, r => Assert.True(r.IsUnsignedHint));
        Assert.Equal("https://api.nuget.org/v3-flatcontainer/dbdatasync/index.json", h.Requests[0].AbsoluteUri);
    }

    [Fact]
    public async Task UrlSource_FetchesManifestAndSignature()
    {
        using var t = new TempDir();
        var k = new KeyPair("k1");
        var (_, dir) = TestKit.WriteRelease(t.Path, k, "1.2.0", TestKit.MakeZip(("a", "x")));
        var d = await Checker(k).CheckAsync(new UrlReleaseSource(new Uri(Path.Combine(dir, "release.json"))), ReleaseVersion.Parse("1.0.0"), new());
        Assert.Equal("1.2.0", d.Manifest!.Version);
    }

    [Fact]
    public async Task HttpFetcher_ServerIgnoringRangeStillResumesCorrectly()
    {
        var data = Encoding.UTF8.GetBytes("0123456789");
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
        await using var s = await new HttpContentFetcher(new HttpClient(h)).OpenReadAsync(new Uri("https://x/y"), 4, default);
        using var r = new StreamReader(s);
        Assert.Equal("456789", await r.ReadToEndAsync());
    }
}
