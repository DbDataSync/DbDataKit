using System.Security.Cryptography;
using System.Text;
using DbDataKit.Connections;
using DbDataKit.Connections.Files;
using DbDataKit.Runner.FileConnections;
using DbDataKit.Monitoring;
using DbDataKit.Runner.Execution;
using DbDataKit.Runner.Scheduler;
using DbDataKit.Runner.Triggers;
using DbDataKit.Scheduling;
using DbDataKit.Secrets;
using DbDataKit.State;
using DbDataKit.State.Tests;

namespace DbDataKit.Runner.Tests;

/// <summary>
/// Plan 27a IX07: a file connection feeds the runner's file trigger. On the real engine of each state database (sqlite, and PostgreSQL and SQL Server when their variables are set): a file seen twice on an SFTP source runs once (the SFTP server is the container of
/// <c>testenv/testenv.sh up sftp</c>; skipped without it); a partly written file is not taken (a producer's <c>.part</c> is not listed, and a file modified within the settle time waits for the next look); the source of a slot follows the connection the slot is bound to in each
/// release channel, and a slot bound to nothing falls back to a local folder.
/// </summary>
public sealed class FileConnectionTriggerTests
{
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static PackageTask Load(string path = "in/*.csv", int pollSeconds = 10) =>
        new("load", new TaskEntry("T"), new Schedule(), [TaskTrigger.OnFile("inbox", path, TimeSpan.FromSeconds(pollSeconds))], [], [new ConnectionSlot("inbox", "file")], ProgressDeclaration.Empty, TaskLimits.None, [], []);

    private static PackageManifest Manifest(params PackageTask[] tasks) => SchedulerHarness.Manifest("pkg", tasks);

    /// <summary>A store over a folder that tells how many times it was opened and let go: a trigger holds no connection between looks.</summary>
    private sealed class Counted(string folder)
    {
        public int Opened, Disposed;

        public Func<CancellationToken, Task<IFileStore>> Open => _ => { Opened++; return Task.FromResult<IFileStore>(new Tracked(new PathFileStore(folder), this)); };

        private sealed class Tracked(IFileStore inner, Counted owner) : IFileStore
        {
            public Task<IReadOnlyList<DbDataKit.Connections.Files.FileEntry>> ListAsync(string folder = "/", bool includeTemporary = false, CancellationToken ct = default) => inner.ListAsync(folder, includeTemporary, ct);
            public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => inner.OpenReadAsync(path, ct);
            public Task WriteAsync(string path, Stream content, CancellationToken ct = default) => inner.WriteAsync(path, content, ct);
            public Task DeleteAsync(string path, CancellationToken ct = default) => inner.DeleteAsync(path, ct);
            public ValueTask DisposeAsync() { owner.Disposed++; return inner.DisposeAsync(); }
        }
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_partly_written_file_is_not_taken_and_the_connection_is_let_go_after_each_look(string engine)
    {
        using var h = new TriggerHarness(engine);
        var folder = Path.Combine(h.Root, "drop");
        Directory.CreateDirectory(Path.Combine(folder, "in"));
        h.Activate(Manifest(Load(pollSeconds: 1)));
        var runner = new FakeRunner(h.Database);
        var counted = new Counted(folder);
        var scheduler = h.NewScheduler(runner, triggers: new TriggerOptions { FileSources = new ConnectionFileSources((_, _) => counted.Open), FileSettle = TimeSpan.FromSeconds(5) });
        string Csv(string name) => Path.Combine(folder, "in", name);
        void Written(string name, string text, TimeSpan ago) { File.WriteAllText(Csv(name), text); File.SetLastWriteTimeUtc(Csv(name), h.Clock.GetUtcNow().UtcDateTime - ago); }

        // a producer writes feed.csv.part and renames it when whole: the part is never listed, so never taken, however old it is
        Written("feed.csv.part", "id,1\n", TimeSpan.FromMinutes(10));
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        await h.Tick(scheduler);
        Assert.Empty(runner.Requests);

        // a file under its own name that was changed a moment ago may still be being written: it waits, and is taken at the look after the settle time
        Written("late.csv", "id,2\n", TimeSpan.Zero);
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        await h.Tick(scheduler);
        Assert.Empty(runner.Requests);
        h.Clock.Advance(TimeSpan.FromSeconds(6));
        await h.Tick(scheduler);
        Assert.Single(runner.Requests);

        // the producer renames the part when it is done: the file appears under its name, and again waits out the settle time
        File.Move(Csv("feed.csv.part"), Csv("feed.csv"));
        File.SetLastWriteTimeUtc(Csv("feed.csv"), h.Clock.GetUtcNow().UtcDateTime);
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        await h.Tick(scheduler);
        Assert.Single(runner.Requests);
        h.Clock.Advance(TimeSpan.FromSeconds(6));
        await h.Tick(scheduler);
        Assert.Equal(2, runner.Requests.Count);

        Assert.True(counted.Opened >= 4);
        Assert.Equal(counted.Opened, counted.Disposed);                   // every look let its connection go
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task The_source_of_a_slot_follows_the_connection_it_is_bound_to_in_each_release_channel_and_an_unbound_slot_falls_back_to_a_local_folder(string engine)
    {
        using var h = new TriggerHarness(engine);
        var prod = Path.Combine(h.Root, "prod-drop");
        var test = Path.Combine(h.Root, "test-drop");
        var local = Path.Combine(h.Root, "dev-folder");
        foreach (var f in new[] { prod, test, local }) Directory.CreateDirectory(Path.Combine(f, "in"));
        var manifest = Manifest(Load());
        h.Activate(manifest, channel: "prod");
        h.Activate(manifest, channel: "test");
        h.Activate(manifest, channel: "dev");
        await File.WriteAllTextAsync(Path.Combine(prod, "in", "prod-only.csv"), "p\n");
        await File.WriteAllTextAsync(Path.Combine(test, "in", "test-only.csv"), "t\n");
        await File.WriteAllTextAsync(Path.Combine(local, "in", "dev-only.csv"), "d\n");
        var bound = new Dictionary<string, Counted> { ["pkg/prod/inbox"] = new(prod), ["pkg/test/inbox"] = new(test) };
        var sources = new ConnectionFileSources((task, slot) => bound.TryGetValue($"{task.Package}/{task.Channel}/{slot}", out var c) ? c.Open : null,
            fallback: new LocalFileSources(new Dictionary<string, string> { ["pkg/dev/inbox"] = local }));
        var runner = new FakeRunner(h.Database);
        var scheduler = h.NewScheduler(runner, triggers: new TriggerOptions { FileSources = sources, FileSettle = TimeSpan.Zero }, channels: ["prod", "test", "dev"]);
        var ledgers = new[] { "prod", "test", "dev" }.ToDictionary(c => c, c => new ProgressStore(h.Database, new ProgressKey("pkg", c, "load")));

        h.Clock.Advance(TimeSpan.FromSeconds(11));
        await h.Tick(scheduler);
        Assert.Equal(3, runner.Requests.Count);
        // each channel read its own source and only that one: production's folder was never read by the test channel
        Assert.NotNull(ledgers["prod"].FindFile("inbox", Hash("p\n")));
        Assert.Null(ledgers["prod"].FindFile("inbox", Hash("t\n")));
        Assert.NotNull(ledgers["test"].FindFile("inbox", Hash("t\n")));
        Assert.Null(ledgers["test"].FindFile("inbox", Hash("p\n")));
        Assert.NotNull(ledgers["dev"].FindFile("inbox", Hash("d\n")));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_slot_bound_to_nothing_says_so_in_the_status_and_queues_nothing(string engine)
    {
        using var h = new TriggerHarness(engine);
        h.Activate(Manifest(Load()));
        var runner = new FakeRunner(h.Database);
        var scheduler = h.NewScheduler(runner, triggers: new TriggerOptions { FileSources = new ConnectionFileSources((_, _) => null), FileSettle = TimeSpan.Zero });
        h.Clock.Advance(TimeSpan.FromSeconds(11));
        await h.Tick(scheduler);
        Assert.Empty(runner.Requests);
        Assert.Contains("no folder", scheduler.Triggers.Statuses().Single(s => s.Kind == "file").Problem, StringComparison.Ordinal);
    }

    public static TheoryData<string> Engines => StateEngines.Engines;

    [Fact]
    public async Task A_connection_source_lists_the_files_that_match_oldest_first_with_paths_under_the_folder_reads_them_and_a_missing_folder_has_no_files()
    {
        var folder = Directory.CreateTempSubdirectory("dbdatakit-fcs-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "in", "sub"));
            foreach (var (name, age) in new[] { ("new.csv", 1), ("old.csv", 9), ("note.txt", 5), ("old2.CSV", 7), ("x.csv.part", 3) })
            {
                var path = Path.Combine(folder, "in", name);
                File.WriteAllText(path, name);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-age));
            }

            await using var source = new ConnectionFileSource(_ => Task.FromResult<IFileStore>(new PathFileStore(folder)));
            var found = new List<DbDataKit.Runner.Triggers.FileEntry>();
            await foreach (var f in source.ListAsync("in", "*.csv", default)) found.Add(f);
            Assert.Equal(["in/old.csv", "in/new.csv"], found.Select(f => f.Path));   // oldest first; the pattern is on the whole name and case-sensitive; a folder and a .part are not files
            Assert.Equal(7, found[0].Size);
            using (var read = new StreamReader(await source.OpenReadAsync("in/new.csv", default))) Assert.Equal("new.csv", await read.ReadToEndAsync());

            var nothing = new List<DbDataKit.Runner.Triggers.FileEntry>();
            await foreach (var f in source.ListAsync("not-there", "*", default)) nothing.Add(f);
            Assert.Empty(nothing);
            var root = new List<DbDataKit.Runner.Triggers.FileEntry>();
            await foreach (var f in source.ListAsync("", "n*.???", default)) root.Add(f);   // the folder itself: nothing there but "in"
            Assert.Empty(root);
        }
        finally { Directory.Delete(folder, true); }
    }

    // ---- a real SFTP server ----

    [Theory, MemberData(nameof(Engines))]
    public async Task A_file_seen_twice_on_an_sftp_source_runs_once(string engine)
    {
        var host = Environment.GetEnvironmentVariable("DBDATAKIT_TEST_SFTP_HOST");
        Assert.SkipUnless(!string.IsNullOrEmpty(host), "Start the SFTP server (testenv/testenv.sh up sftp) and set the DBDATAKIT_TEST_SFTP_* variables");
        var secrets = new SecretResolverChain([new EnvSecretProvider(name => name == "PW" ? Environment.GetEnvironmentVariable("DBDATAKIT_TEST_SFTP_PASSWORD") : null)]);
        var folder = "/" + Guid.NewGuid().ToString("N")[..8];
        ConnectionDefinition Connection(string hostKeys) => Load($"schema: connection/2\nkind: sftp\nname: drop\nfile_server:\n  host: {host}\n  port: {Environment.GetEnvironmentVariable("DBDATAKIT_TEST_SFTP_PORT")}\n  root: {Environment.GetEnvironmentVariable("DBDATAKIT_TEST_SFTP_ROOT")}{folder}\n  host_keys: [{hostKeys}]\nsign_in: {{ method: basic, user: {Environment.GetEnvironmentVariable("DBDATAKIT_TEST_SFTP_USER")}, password_secret: env:PW }}\n{(hostKeys.Length == 0 ? "tls: { verify: false }\n" : "")}");
        var shown = await FileStores.ProbeAsync(Connection(""));
        var drop = Connection($"\"{shown}\"");

        using var h = new TriggerHarness(engine);
        h.Activate(Manifest(Load()));
        var runner = new FakeRunner(h.Database);
        var scheduler = h.NewScheduler(runner, triggers: new TriggerOptions { FileSources = new ConnectionFileSources((_, slot) => slot == "inbox" ? drop : null, secrets), FileSettle = TimeSpan.Zero });
        var ledger = new ProgressStore(h.Database, new ProgressKey("pkg", "prod", "load"));
        await using var producer = await FileStores.OpenAsync(drop, secrets);

        h.Clock.Advance(TimeSpan.FromSeconds(11));
        await h.Tick(scheduler);
        Assert.Empty(runner.Requests);                                    // the folder is not even there yet: no file, no failure

        await producer.WriteAsync("/in/a.csv", Stream(new MemoryStream(Encoding.UTF8.GetBytes("id,1\n"))));
        await producer.WriteAsync("/in/skip.txt", Stream(new MemoryStream("not matched"u8.ToArray())));
        await producer.WriteAsync("/in/feed.csv.part", Stream(new MemoryStream("id,9\n"u8.ToArray())));   // a producer still writing
        h.Clock.Advance(TimeSpan.FromSeconds(11));
        await h.Tick(scheduler);
        Assert.Single(runner.Requests);
        Assert.NotNull(ledger.FindFile("inbox", Hash("id,1\n")));
        Assert.Null(ledger.FindFile("inbox", Hash("id,9\n")));

        // the same file again (the partner sends it twice, and once more under another name): one run
        h.Clock.Advance(TimeSpan.FromSeconds(11));
        await h.Tick(scheduler);
        await producer.WriteAsync("/in/a-again.csv", Stream(new MemoryStream("id,1\n"u8.ToArray())));
        h.Clock.Advance(TimeSpan.FromSeconds(11));
        await h.Tick(scheduler);
        Assert.Single(runner.Requests);

        await producer.WriteAsync("/in/b.csv", Stream(new MemoryStream("id,2\n"u8.ToArray())));
        h.Clock.Advance(TimeSpan.FromSeconds(11));
        await h.Tick(scheduler);
        Assert.Equal(2, runner.Requests.Count);
    }

    private static Stream Stream(Stream s) => s;

    private static ConnectionDefinition Load(string yaml)
    {
        var result = ConnectionDefinitionLoader.LoadConnectionOfAnyKind(yaml, "connections/drop.yml");
        Assert.True(result.Ok, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        return result.Definition!;
    }
}
