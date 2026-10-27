using DbDataKit.Scripting;
using NuGet.Versioning;
using static DbDataKit.Scripting.Tests.Feed;

namespace DbDataKit.Scripting.Tests;

public class ResolverTests
{
    private static PackageRequest Exact(string id, string version) => PackageRequest.Exact(id, version);

    private static string[] Names(Closure c) => [.. c.Packages.Select(p => $"{p.Id} {p.Version}")];

    [Fact]
    public async Task A_package_and_the_ones_it_depends_on_are_taken_with_their_assets_from_the_nearest_framework()
    {
        using var feed = new Feed();
        feed.Add("Lib.A", "1.0.0", ["lib/netstandard2.0/Lib.A.dll", "lib/net10.0/Lib.A.dll", "lib/net6.0/Lib.A.dll"], [new Dep("Lib.B", "[2.0.0]")]);
        feed.Add("Lib.B", "2.0.0", ["lib/netstandard2.0/Lib.B.dll"]);

        var closure = await new PackageResolver(feed.Options()).ResolveAsync([Exact("Lib.A", "1.0.0")]);

        Assert.Equal(["Lib.A 1.0.0", "Lib.B 2.0.0"], Names(closure));
        Assert.Contains("net10.0", closure.Managed["Lib.A"]);                    // the nearest of netstandard2.0, net6.0 and net10.0
        Assert.Contains("netstandard2.0", closure.Managed["Lib.B"]);
        Assert.All(closure.Packages, p => Assert.True(Directory.Exists(p.Directory)));
        Assert.All(closure.Packages, p => Assert.NotEmpty(p.Sha512));
    }

    [Fact]
    public async Task Compile_references_come_from_ref_when_a_package_has_them_and_from_lib_when_it_does_not()
    {
        using var feed = new Feed();
        feed.Add("With.Ref", "1.0.0", ["ref/net10.0/With.Ref.dll", "lib/net10.0/With.Ref.dll"]);
        feed.Add("No.Ref", "1.0.0", ["lib/net10.0/No.Ref.dll"]);

        var closure = await new PackageResolver(feed.Options()).ResolveAsync([Exact("With.Ref", "1.0.0"), Exact("No.Ref", "1.0.0")]);

        Assert.Contains($"ref{Path.DirectorySeparatorChar}net10.0", closure.Compile["With.Ref"]);
        Assert.Contains($"lib{Path.DirectorySeparatorChar}net10.0", closure.Managed["With.Ref"]);
        Assert.Contains($"lib{Path.DirectorySeparatorChar}net10.0", closure.Compile["No.Ref"]);
    }

    [Fact]
    public async Task Native_files_and_platform_assemblies_come_from_the_best_runtime_identifier_in_the_chain()
    {
        using var feed = new Feed();
        feed.Add("Has.Native", "1.0.0",
        [
            "lib/net10.0/Has.Native.dll",
            "runtimes/linux-x64/native/libthing.so", "runtimes/linux/native/libthing.so", "runtimes/linux/native/libonly-linux.so",
            "runtimes/win-x64/native/thing.dll", "runtimes/linux-musl-x64/native/libthing.so",
            "runtimes/linux-x64/lib/net10.0/Has.Native.Platform.dll", "runtimes/win-x64/lib/net10.0/Has.Native.Platform.dll",
        ]);

        var linux = await new PackageResolver(feed.Options(rids: ["linux-x64", "linux", "unix", "any"])).ResolveAsync([Exact("Has.Native", "1.0.0")]);
        Assert.Contains($"linux-x64{Path.DirectorySeparatorChar}native", linux.Native["libthing.so"]);       // the most specific rid that ships the name
        Assert.Contains($"{Path.DirectorySeparatorChar}linux{Path.DirectorySeparatorChar}native", linux.Native["libonly-linux.so"]);
        Assert.DoesNotContain("thing.dll", linux.Native.Keys);
        Assert.Contains("linux-x64", linux.Managed["Has.Native.Platform"]);

        var musl = await new PackageResolver(feed.Options(rids: ["linux-musl-x64", "linux-x64", "linux", "unix", "any"])).ResolveAsync([Exact("Has.Native", "1.0.0")]);
        Assert.Contains("linux-musl-x64", musl.Native["libthing.so"]);

        var windows = await new PackageResolver(feed.Options(rids: ["win-x64", "win", "any"])).ResolveAsync([Exact("Has.Native", "1.0.0")]);
        Assert.Contains("thing.dll", windows.Native.Keys);
        Assert.Contains("win-x64", windows.Managed["Has.Native.Platform"]);
    }

    [Theory]
    [InlineData("linux", "x64", false, "linux-x64,linux,unix,any")]
    [InlineData("linux", "arm64", true, "linux-musl-arm64,linux-arm64,linux,unix,any")]
    [InlineData("windows", "x64", false, "win-x64,win,any")]
    [InlineData("macos", "arm64", false, "osx-arm64,osx,unix,any")]
    public void The_runtime_identifier_chain_is_most_specific_first(string os, string arch, bool musl, string expected) =>
        Assert.Equal(expected.Split(','), Platform.RidChain(os, arch, musl));

    [Fact]
    public async Task What_the_host_provides_is_not_taken_from_a_package_and_a_warning_says_so()
    {
        using var feed = new Feed();
        feed.Add("Carries.Framework", "1.0.0", ["lib/net10.0/System.Text.Json.dll", "lib/net10.0/Own.dll"]);

        var closure = await new PackageResolver(feed.Options(hostProvides: n => n == "System.Text.Json")).ResolveAsync([Exact("Carries.Framework", "1.0.0")]);

        Assert.DoesNotContain("System.Text.Json", closure.Managed.Keys);
        Assert.Contains("Own", closure.Managed.Keys);
        Assert.Contains(closure.Warnings, w => w.Code == "scripting.host.provides" && w.Message.Contains("System.Text.Json"));
    }

    [Fact]
    public async Task Packages_that_only_stand_for_the_framework_are_skipped_not_downloaded()
    {
        using var feed = new Feed();
        feed.Add("Uses.Standard", "1.0.0", ["lib/netstandard2.0/Uses.Standard.dll"], [new Dep("NETStandard.Library", "2.0.3", "netstandard2.0"), new Dep("Microsoft.NETCore.Platforms", "1.1.0", "netstandard2.0")]);

        var closure = await new PackageResolver(feed.Options()).ResolveAsync([Exact("Uses.Standard", "1.0.0")]);

        Assert.Equal(["Uses.Standard 1.0.0"], Names(closure));
        Assert.Equal(["Microsoft.NETCore.Platforms", "NETStandard.Library"], closure.Skipped.Order());
        Assert.False(Directory.Exists(Path.Combine(feed.Cache, "netstandard.library")));
    }

    [Fact]
    public async Task A_package_the_script_names_beats_a_dependencys_wish_and_a_warning_says_so()
    {
        using var feed = new Feed();
        feed.Add("Top", "1.0.0", ["lib/net10.0/Top.dll"], [new Dep("Shared", "[2.0.0]")]);
        feed.Add("Shared", "1.0.0", ["lib/net10.0/Shared.dll"]);
        feed.Add("Shared", "2.0.0", ["lib/net10.0/Shared.dll"]);

        var closure = await new PackageResolver(feed.Options()).ResolveAsync([Exact("Top", "1.0.0"), Exact("Shared", "1.0.0")]);

        Assert.Contains("Shared 1.0.0", Names(closure));
        Assert.Contains(closure.Warnings, w => w.Code == "scripting.version.kept" && w.Message.Contains("Shared 1.0.0"));
    }

    [Fact]
    public async Task Between_two_dependencies_a_later_range_the_first_choice_does_not_meet_raises_the_version_and_drops_what_the_old_one_needed()
    {
        using var feed = new Feed();
        feed.Add("Top", "1.0.0", ["lib/net10.0/Top.dll"], [new Dep("Low", "1.0.0"), new Dep("High", "1.0.0")]);
        feed.Add("Low", "1.0.0", ["lib/net10.0/Low.dll"], [new Dep("Shared", "[1.0.0]")]);
        feed.Add("High", "1.0.0", ["lib/net10.0/High.dll"], [new Dep("Shared", "[2.0.0]")]);
        feed.Add("Shared", "1.0.0", ["lib/net10.0/Shared.dll"], [new Dep("OnlyForOld", "1.0.0")]);
        feed.Add("Shared", "2.0.0", ["lib/net10.0/Shared.dll"]);
        feed.Add("OnlyForOld", "1.0.0", ["lib/net10.0/OnlyForOld.dll"]);

        var closure = await new PackageResolver(feed.Options()).ResolveAsync([Exact("Top", "1.0.0")]);

        Assert.Contains("Shared 2.0.0", Names(closure));
        Assert.DoesNotContain(closure.Packages, p => p.Id == "OnlyForOld");              // nothing reaches it now
        Assert.DoesNotContain("OnlyForOld", closure.Managed.Keys);
        Assert.Contains(closure.Warnings, w => w.Code == "scripting.version.raised" && w.Message.Contains("Shared"));
    }

    [Fact]
    public async Task A_fixed_range_takes_the_lowest_version_that_fits_and_a_floating_one_the_highest()
    {
        using var feed = new Feed();
        foreach (var v in new[] { "1.0.0", "1.2.0", "1.9.0", "2.0.0" }) feed.Add("Ranged", v, [$"lib/net10.0/Ranged.dll"]);

        var fixedRange = await new PackageResolver(feed.Options()).ResolveAsync([new PackageRequest("Ranged", VersionRange.Parse("[1.1.0,2.0.0)"))]);
        Assert.Equal(["Ranged 1.2.0"], Names(fixedRange));

        using var other = new Feed();
        foreach (var v in new[] { "1.0.0", "1.2.0", "1.9.0", "2.0.0" }) other.Add("Ranged", v, ["lib/net10.0/Ranged.dll"]);
        var floating = await new PackageResolver(other.Options()).ResolveAsync([new PackageRequest("Ranged", VersionRange.Parse("1.*"))]);
        Assert.Equal(["Ranged 1.9.0"], Names(floating));
    }

    [Fact]
    public async Task Once_cached_a_fixed_version_needs_no_source_at_all()
    {
        using var feed = new Feed();
        feed.Add("Offline.Me", "1.0.0", ["lib/net10.0/Offline.Me.dll"]);
        await new PackageResolver(feed.Options()).ResolveAsync([Exact("Offline.Me", "1.0.0")]);

        System.IO.Directory.Delete(feed.Directory, true);                                // the feed is gone
        var again = await new PackageResolver(feed.Options()).ResolveAsync([Exact("Offline.Me", "1.0.0")]);
        Assert.Equal(["Offline.Me 1.0.0"], Names(again));

        var offline = await new PackageResolver(feed.Options(offline: true, sources: [])).ResolveAsync([Exact("Offline.Me", "1.0.0")]);
        Assert.Equal(["Offline.Me 1.0.0"], Names(offline));
        await Assert.ThrowsAsync<PackageResolutionException>(() => new PackageResolver(feed.Options(offline: true, sources: [])).ResolveAsync([Exact("Not.There", "1.0.0")]));
    }

    [Fact]
    public async Task A_source_that_does_not_answer_is_a_warning_when_another_serves_the_package_and_an_error_when_none_does()
    {
        using var feed = new Feed();
        feed.Add("Found.Later", "1.0.0", ["lib/net10.0/Found.Later.dll"]);
        var dead = new PackageSource(Path.Combine(feed.Root, "no-such-feed"));
        var sources = new[] { dead, new PackageSource(feed.Directory) };

        var closure = await new PackageResolver(feed.Options(sources: sources)).ResolveAsync([Exact("Found.Later", "1.0.0")]);
        Assert.Equal(["Found.Later 1.0.0"], Names(closure));

        var missing = await Assert.ThrowsAsync<PackageResolutionException>(() => new PackageResolver(feed.Options(sources: sources)).ResolveAsync([Exact("Nowhere", "1.0.0")]));
        Assert.Contains("Nowhere", missing.Message);
    }

    [Fact]
    public async Task Source_mapping_serves_a_package_only_from_the_source_mapped_to_its_id()
    {
        using var open = new Feed();
        using var privateFeed = new Feed();
        open.Add("Acme.Secret", "1.0.0", ["lib/net10.0/Impostor.dll"]);                // a public package squatting on a private id
        privateFeed.Add("Acme.Secret", "1.0.0", ["lib/net10.0/Real.dll"]);
        open.Add("Public.Thing", "1.0.0", ["lib/net10.0/Public.Thing.dll"]);
        var sources = new[] { new PackageSource(open.Directory, ["*"]), new PackageSource(privateFeed.Directory, ["Acme.*"]) };

        var closure = await new PackageResolver(open.Options(sources: sources)).ResolveAsync([Exact("Acme.Secret", "1.0.0"), Exact("Public.Thing", "1.0.0")]);

        Assert.Contains("Real", closure.Managed.Keys);
        Assert.DoesNotContain("Impostor", closure.Managed.Keys);
        Assert.Contains("Public.Thing", closure.Managed.Keys);

        // an id that no source is mapped for is refused (a fresh cache: a package already cached needs no source)
        using var fresh = new Feed();
        fresh.Add("Acme.Secret", "1.0.0", ["lib/net10.0/Impostor.dll"]);
        var unmapped = new[] { new PackageSource(fresh.Directory, ["Public.*"]) };
        var refused = await Assert.ThrowsAsync<PackageResolutionException>(() => new PackageResolver(fresh.Options(sources: unmapped)).ResolveAsync([Exact("Acme.Secret", "1.0.0")]));
        Assert.Contains("No package source is mapped", refused.Message);
    }

    [Fact]
    public void Source_patterns_match_an_id_or_a_prefix_and_the_longest_pattern_wins()
    {
        var all = new PackageSource("all", ["*"]);
        var acme = new PackageSource("acme", ["Acme.*"]);
        var secret = new PackageSource("secret", ["Acme.Secret"]);
        Assert.Equal(["all"], PackageSource.For([all, acme], "Other").Select(s => s.Location));
        Assert.Equal(["acme"], PackageSource.For([all, acme], "Acme.Tools").Select(s => s.Location));
        Assert.Equal(["secret"], PackageSource.For([all, acme, secret], "acme.secret").Select(s => s.Location));
        Assert.Equal(["a", "b"], PackageSource.For([new PackageSource("a"), new PackageSource("b")], "X").Select(s => s.Location));
    }

    [Fact]
    public async Task The_allow_list_is_asked_before_anything_is_stored()
    {
        using var feed = new Feed();
        feed.Add("Not.Allowed", "1.0.0", ["lib/net10.0/Not.Allowed.dll"]);

        var error = await Assert.ThrowsAsync<PackageResolutionException>(() => new PackageResolver(feed.Options(allow: id => id.Id != "Not.Allowed")).ResolveAsync([Exact("Not.Allowed", "1.0.0")]));

        Assert.Contains("allow-list", error.Message);
        Assert.False(System.IO.Directory.Exists(feed.Cache) && System.IO.Directory.EnumerateFileSystemEntries(feed.Cache).Any());
    }

    [Fact]
    public async Task The_first_download_is_recorded_in_the_lock_file_and_a_changed_package_is_refused_afterwards()
    {
        using var feed = new Feed();
        feed.Add("Pinned", "1.0.0", ["lib/net10.0/Pinned.dll"]);
        var lockFile = new LockFile();
        await new PackageResolver(feed.Options(lockFile: lockFile)).ResolveAsync([Exact("Pinned", "1.0.0")]);
        Assert.Equal(["pinned/1.0.0"], lockFile.Pins.Keys);

        // someone replaces the package on the feed (same id and version, other bytes) and a fresh cache downloads it
        feed.Add("Pinned", "1.0.0", ["lib/net10.0/Pinned.dll", "lib/net10.0/Extra.dll"]);
        System.IO.Directory.Delete(feed.Cache, true);

        var error = await Assert.ThrowsAsync<PackageIntegrityException>(() => new PackageResolver(feed.Options(lockFile: lockFile)).ResolveAsync([Exact("Pinned", "1.0.0")]));
        Assert.Contains("pinned", error.Message);
    }

    [Fact]
    public async Task A_cached_package_that_was_changed_on_disk_is_refused_every_time_it_is_used()
    {
        using var feed = new Feed();
        feed.Add("Tamper", "1.0.0", ["lib/net10.0/Tamper.dll"]);
        await new PackageResolver(feed.Options()).ResolveAsync([Exact("Tamper", "1.0.0")]);
        var nupkg = System.IO.Directory.EnumerateFiles(feed.Cache, "*.nupkg", SearchOption.AllDirectories).Single();
        File.AppendAllText(nupkg, "x");

        await Assert.ThrowsAsync<PackageIntegrityException>(() => new PackageResolver(feed.Options()).ResolveAsync([Exact("Tamper", "1.0.0")]));
    }

    [Fact]
    public async Task A_stored_script_may_only_use_pinned_packages()
    {
        using var feed = new Feed();
        feed.Add("Unpinned", "1.0.0", ["lib/net10.0/Unpinned.dll"]);

        var error = await Assert.ThrowsAsync<PackageResolutionException>(() => new PackageResolver(feed.Options(requirePinned: true)).ResolveAsync([Exact("Unpinned", "1.0.0")]));
        Assert.Contains("lock file", error.Message);
        Assert.False(System.IO.Directory.Exists(feed.Cache) && System.IO.Directory.EnumerateFiles(feed.Cache, "*", SearchOption.AllDirectories).Any());

        var pinned = new LockFile();
        await new PackageResolver(feed.Options(lockFile: pinned)).ResolveAsync([Exact("Unpinned", "1.0.0")]);
        var closure = await new PackageResolver(feed.Options(lockFile: pinned, requirePinned: true)).ResolveAsync([Exact("Unpinned", "1.0.0")]);
        Assert.Equal(["Unpinned 1.0.0"], Names(closure));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("lib/net10.0/../../../escape.txt")]
    [InlineData("/etc/escape.txt")]
    public async Task A_package_built_to_write_outside_its_own_folder_is_refused_and_leaves_nothing_behind(string entry)
    {
        using var feed = new Feed();
        feed.Add("Hostile", "1.0.0", raw: [(entry, [1, 2, 3])]);

        var error = await Assert.ThrowsAsync<PackageIntegrityException>(() => new PackageResolver(feed.Options()).ResolveAsync([Exact("Hostile", "1.0.0")]));

        Assert.Contains("outside the package's own folder", error.Message);
        Assert.False(File.Exists(Path.Combine(feed.Root, "escape.txt")));
        Assert.False(File.Exists(Path.Combine(feed.Cache, "escape.txt")));
        Assert.Empty(System.IO.Directory.Exists(feed.Cache) ? System.IO.Directory.EnumerateDirectories(feed.Cache, "*", SearchOption.AllDirectories).Where(d => d.Contains(".tmp-")) : []);
    }

    [Fact]
    public async Task Two_resolutions_racing_for_one_package_both_succeed_and_leave_one_good_copy()
    {
        using var feed = new Feed();
        feed.Add("Raced", "1.0.0", ["lib/net10.0/Raced.dll"]);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => new PackageResolver(feed.Options()).ResolveAsync([Exact("Raced", "1.0.0")])));

        Assert.All(results, r => Assert.Equal(["Raced 1.0.0"], Names(r)));
        Assert.Single(System.IO.Directory.EnumerateFiles(feed.Cache, "*.nupkg", SearchOption.AllDirectories));
        Assert.DoesNotContain(System.IO.Directory.EnumerateDirectories(feed.Cache, "*", SearchOption.AllDirectories), d => d.Contains(".tmp-"));
    }
}
