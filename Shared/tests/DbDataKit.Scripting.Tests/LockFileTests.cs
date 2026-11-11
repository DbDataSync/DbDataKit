using DbDataKit.Scripting;

namespace DbDataKit.Scripting.Tests;

public class LockFileTests
{
    [Fact]
    public void A_lock_file_round_trips_sorted_and_writes_nothing_when_nothing_changed()
    {
        using var feed = new Feed();
        var path = Path.Combine(feed.Root, "library.lock");
        var written = new LockFile();
        written.Pin("Npgsql", "10.0.3", "BBB");
        written.Pin("Humanizer.Core", "2.14.1", "AAA");
        written.Write(path);

        var read = LockFile.Read(path);
        Assert.Equal(["humanizer.core/2.14.1", "npgsql/10.0.3"], read.Pins.Keys);
        Assert.Equal("BBB", read.Pins["npgsql/10.0.3"]);

        var stamp = File.GetLastWriteTimeUtc(path);
        Thread.Sleep(20);
        read.Write(path);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    [Theory]
    [InlineData("not json", "not valid JSON")]
    [InlineData("{\"schema\":\"other/1\"}", "not a scripting-lock/1 file")]
    [InlineData("[]", "not a scripting-lock/1 file")]
    [InlineData("{\"schema\":\"scripting-lock/1\",\"packages\":{\"a/1.0.0\":5}}", "not text")]
    public void A_damaged_or_foreign_lock_file_is_refused_naming_the_file(string text, string said)
    {
        using var feed = new Feed();
        var path = Path.Combine(feed.Root, "library.lock");
        File.WriteAllText(path, text);

        var error = Assert.Throws<InvalidDataException>(() => LockFile.Read(path, "config/library.lock"));

        Assert.Contains("config/library.lock", error.Message);
        Assert.Contains(said, error.Message);
    }

    [Theory]
    [InlineData("{\"schema\":\"scripting-lock/1\",\"extra\":1}", "unknown key `extra` at $.extra")]
    [InlineData("{\"schema\":\"scripting-lock/1\",\"packages\":[]}", "`packages` at $.packages is not an object")]
    [InlineData("{\"schema\":\"scripting-lock/1\",\"packages\":{\"a/1.0.0\":5}}", "$.packages.a/1.0.0")]
    public void An_unknown_key_or_a_wrong_shape_is_an_error_with_its_path(string text, string said)
    {
        using var feed = new Feed();
        var path = Path.Combine(feed.Root, "x.lock");
        File.WriteAllText(path, text);

        var error = Assert.Throws<InvalidDataException>(() => LockFile.Read(path, "scripts/x.lock"));

        Assert.Contains("scripts/x.lock", error.Message);
        Assert.Contains(said, error.Message);
    }

    [Fact]
    public void Replace_keeps_only_the_packages_it_is_given()
    {
        var pins = new LockFile();
        pins.Pin("Old", "1.0.0", "A");
        pins.Replace([new ResolvedPackage("New", NuGet.Versioning.NuGetVersion.Parse("2.0.0"), "dir", "B")]);
        Assert.Equal(["new/2.0.0"], pins.Pins.Keys);
    }

    [Fact]
    public void A_missing_lock_file_is_an_empty_one()
    {
        using var feed = new Feed();
        Assert.Empty(LockFile.Read(Path.Combine(feed.Root, "none.lock")).Pins);
    }
}
