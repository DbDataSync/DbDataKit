using System.Text;
using DbDataKit.Identity;
using DbDataKit.State;
using Microsoft.AspNetCore.Http;

namespace DbDataKit.Apps.Tests;

/// <summary>App storage (plan 26, AP09): per person and per instance, isolated by instance, with the manifest's limits.</summary>
public sealed class AppStorageTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "app-storage-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly StateDatabase database;
    private readonly AppStorage storage;
    private readonly AppInstanceStore instances;
    private static readonly AppStorageLimits Limits = new();

    public AppStorageTests()
    {
        Directory.CreateDirectory(folder);
        database = StateDatabase.OpenModules(Path.Combine(folder, "state.db"), [DefinitionsModule.Module, AppStorageModule.Module]);
        storage = new AppStorage(database);
        instances = new AppInstanceStore(new DefinitionStore(database));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    [Fact]
    public void A_person_stores_and_reads_a_value_and_it_counts_up_its_version()
    {
        Assert.Equal(1, storage.Set("acme", "user", "ada", "theme", "\"dark\"", Limits));
        Assert.Equal(2, storage.Set("acme", "user", "ada", "theme", "\"light\"", Limits));

        Assert.Equal(new StoredValue("theme", "\"light\"", 2), storage.Get("acme", "user", "ada", "theme"));
        Assert.True(storage.Delete("acme", "user", "ada", "theme"));
        Assert.Null(storage.Get("acme", "user", "ada", "theme"));
    }

    [Fact]
    public void One_person_cannot_read_anothers_user_storage_but_instance_storage_is_shared_by_the_instance()
    {
        storage.Set("acme", "user", "ada", "draft", "1", Limits);
        storage.Set("acme", "instance", "ada", "motd", "\"hi\"", Limits);

        Assert.Null(storage.Get("acme", "user", "grace", "draft"));
        Assert.Empty(storage.List("acme", "user", "grace"));
        Assert.Equal("\"hi\"", storage.Get("acme", "instance", "grace", "motd")!.Json);
    }

    [Fact]
    public void Two_instances_do_not_share()
    {
        storage.Set("acme", "instance", "ada", "k", "1", Limits);
        storage.Set("acme", "user", "ada", "k", "1", Limits);

        Assert.Null(storage.Get("globex", "instance", "ada", "k"));
        Assert.Null(storage.Get("globex", "user", "ada", "k"));
    }

    [Fact]
    public void List_returns_the_keys_with_a_prefix_in_order()
    {
        foreach (var key in new[] { "filter/b", "filter/a", "other" }) storage.Set("acme", "user", "ada", key, "true", Limits);

        Assert.Equal(["filter/a", "filter/b"], storage.List("acme", "user", "ada", "filter/").Select(i => i.Key));
    }

    [Fact]
    public void A_value_over_the_limit_a_long_key_and_text_that_is_not_json_are_refused()
    {
        var big = "\"" + new string('x', Limits.MaximumValueBytes) + "\"";
        Assert.Equal("value", Assert.Throws<AppStorageException>(() => storage.Set("acme", "user", "ada", "k", big, Limits)).Kind);
        Assert.Equal("key", Assert.Throws<AppStorageException>(() => storage.Set("acme", "user", "ada", new string('k', Limits.MaximumKeyLength + 1), "1", Limits)).Kind);
        Assert.Equal("key", Assert.Throws<AppStorageException>(() => storage.Set("acme", "user", "ada", "", "1", Limits)).Kind);
        Assert.Equal("value", Assert.Throws<AppStorageException>(() => storage.Set("acme", "user", "ada", "k", "not json", Limits)).Kind);
    }

    [Fact]
    public void The_instances_total_is_limited_across_both_scopes_and_an_overwrite_counts_only_the_difference()
    {
        var small = new AppStorageLimits(QuotaBytes: 100);
        storage.Set("acme", "user", "ada", "a", "\"" + new string('x', 58) + "\"", small);          // 60 bytes
        Assert.Equal("quota", Assert.Throws<AppStorageException>(() => storage.Set("acme", "instance", "ada", "b", "\"" + new string('x', 58) + "\"", small)).Kind);
        storage.Set("acme", "user", "ada", "a", "\"" + new string('y', 88) + "\"", small);          // replaces the 60: 90 bytes
        storage.Set("globex", "user", "ada", "a", "\"" + new string('x', 58) + "\"", small);        // another instance has its own total
    }

    [Fact]
    public void An_if_version_check_refuses_a_stale_write_and_zero_means_only_if_new()
    {
        storage.Set("acme", "instance", "ada", "n", "1", Limits, ifVersion: 0);
        Assert.Equal("conflict", Assert.Throws<AppStorageException>(() => storage.Set("acme", "instance", "ada", "n", "2", Limits, ifVersion: 0)).Kind);
        Assert.Equal(2, storage.Set("acme", "instance", "grace", "n", "2", Limits, ifVersion: 1));
        Assert.Equal("conflict", Assert.Throws<AppStorageException>(() => storage.Set("acme", "instance", "ada", "n", "3", Limits, ifVersion: 1)).Kind);
    }

    // ---- the manifest and the endpoint ----

    private ResolvedInstance Publish(string storageYaml)
    {
        instances.PublishApp(new AppPackage($"""
            schemaVersion: 1
            name: notes
            version: 1.0.0
            entry: index.html
            roles: [viewer]
            {storageYaml}
            """, new Dictionary<string, byte[]> { ["index.html"] = Encoding.UTF8.GetBytes("<!doctype html>") }), "ada");
        instances.Save(new AppInstance("acme", "notes"), "ada");
        return instances.Resolve("acme")!;
    }

    private static AppSession Person(string id) => new("s", new HomeIdentity(id, id, null, [], new Dictionary<string, IReadOnlyList<string>>()), DateTimeOffset.MaxValue);

    private async Task<(int Status, string Body)> Call(ResolvedInstance resolved, string person, string json)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        context.Response.Body = new MemoryStream();
        await AppStorageEndpoint.HandleAsync(context, storage, resolved, Person(person));
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, new StreamReader(context.Response.Body).ReadToEnd());
    }

    [Fact]
    public void The_manifest_declares_storage_and_an_app_that_declares_nothing_gets_none()
    {
        Assert.Equal(new AppStorageUse(true, true, 5), Publish("storage: {user: true, instance: true, quota_mb: 5}").Manifest.StorageUse);
        Assert.Equal(AppStorageUse.None, Publish("").Manifest.StorageUse);
    }

    [Theory]
    [InlineData("storage: {user: yes}", "true")]
    [InlineData("storage: {quota_mb: 0}", "quota_mb")]
    [InlineData("storage: {quota_mb: 5000}", "quota_mb")]
    [InlineData("storage: {bogus: true}", "bogus")]
    [InlineData("storage: nope", "mapping")]
    public void A_bad_storage_section_is_refused_with_its_reason(string yaml, string mention)
    {
        var load = AppManifestLoader.Load($"schemaVersion: 1\nname: notes\nversion: 1.0.0\nentry: index.html\n{yaml}\n");

        Assert.Null(load.Manifest);
        Assert.Contains(mention, string.Join("\n", load.Diagnostics.Select(d => d.Message)));
    }

    [Fact]
    public async Task The_endpoint_stores_for_the_signed_in_person_only_and_refuses_what_the_manifest_does_not_declare()
    {
        var resolved = Publish("storage: {user: true}");

        Assert.Equal(200, (await Call(resolved, "ada", """{"op":"set","scope":"user","key":"theme","value":{"dark":true}}""")).Status);
        Assert.Contains("\"dark\":true", (await Call(resolved, "ada", """{"op":"get","scope":"user","key":"theme"}""")).Body);
        Assert.Contains("\"found\":false", (await Call(resolved, "grace", """{"op":"get","scope":"user","key":"theme"}""")).Body);
        Assert.Equal(403, (await Call(resolved, "ada", """{"op":"set","scope":"instance","key":"x","value":1}""")).Status);
        Assert.Equal(400, (await Call(resolved, "ada", """{"op":"set","scope":"elsewhere","key":"x","value":1}""")).Status);
        Assert.Equal(400, (await Call(resolved, "ada", "not json")).Status);
        Assert.Equal(409, (await Call(resolved, "ada", """{"op":"set","scope":"user","key":"theme","value":1,"ifVersion":7}""")).Status);
    }

    [Fact]
    public async Task An_app_with_no_storage_declared_is_refused_and_the_quota_comes_from_the_manifest()
    {
        Assert.Equal(403, (await Call(Publish(""), "ada", """{"op":"get","scope":"user","key":"k"}""")).Status);

        var resolved = Publish("storage: {instance: true, quota_mb: 1}");
        var big = new string('x', 200 * 1024);
        for (var i = 0; i < 5; i++) Assert.Equal(200, (await Call(resolved, "ada", $$"""{"op":"set","scope":"instance","key":"k{{i}}","value":"{{big}}"}""")).Status);
        Assert.Equal(413, (await Call(resolved, "ada", $$"""{"op":"set","scope":"instance","key":"k5","value":"{{big}}"}""")).Status);
    }
}
