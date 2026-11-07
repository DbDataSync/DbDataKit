using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbDataKit.State.Snapshots;
using DbDataKit.Monitoring;

namespace DbDataKit.State.Tests;

/// <summary>
/// A snapshot read back by the test, independently of the writer: it parses <c>manifest.json</c> and the files with <see cref="JsonDocument"/> and hashes with <see cref="SHA256"/> itself, so what a test
/// asserts is what is on disk and not what the writer believed it wrote.
/// </summary>
internal sealed class SnapshotFiles : IDisposable
{
    private readonly JsonDocument _manifest;

    public SnapshotFiles(string directory)
    {
        Directory = directory;
        ManifestText = File.ReadAllText(Path.Combine(directory, "manifest.json"));
        _manifest = JsonDocument.Parse(ManifestText);
    }

    public string Directory { get; }

    public string ManifestText { get; }

    public JsonElement Root => _manifest.RootElement;

    public IEnumerable<JsonElement> Modules => Root.GetProperty("modules").EnumerateArray();

    public IEnumerable<(string Module, JsonElement Table)> Tables => Modules.SelectMany(m => m.GetProperty("tables").EnumerateArray().Select(t => (m.GetProperty("name").GetString()!, t)));

    public JsonElement Table(string module, string table) =>
        Tables.Single(t => t.Module == module && t.Table.GetProperty("name").GetString() == table).Table;

    /// <summary>The lines of a table's file, uncompressed.</summary>
    public List<string> Lines(string module, string table)
    {
        using var file = File.OpenRead(Path.Combine(Directory, Table(module, table).GetProperty("file").GetString()!));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }

    /// <summary>The rows of a table, each an array of values in column order.</summary>
    public List<JsonElement[]> Rows(string module, string table) =>
        [.. Lines(module, table).Select(l =>
        {
            using var doc = JsonDocument.Parse(l);
            return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
        })];

    public static string Sha256Of(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }

    /// <summary>The sum of the SHA-256 of each line, modulo 2^256, as the format defines a table's <c>rows_digest</c>.</summary>
    public static string RowsDigestOf(IEnumerable<string> lines)
    {
        var sum = System.Numerics.BigInteger.Zero;
        foreach (var line in lines)
            sum += new System.Numerics.BigInteger(SHA256.HashData(Encoding.UTF8.GetBytes(line)), isUnsigned: true, isBigEndian: true);
        sum %= System.Numerics.BigInteger.Pow(2, 256);
        var bytes = sum.ToByteArray(isUnsigned: true, isBigEndian: true);
        return Convert.ToHexStringLower(new byte[32 - bytes.Length].Concat(bytes).ToArray());
    }

    /// <summary>Every file of the snapshot as text (table files uncompressed, payload files as Latin-1), for a search for something that must not be there.</summary>
    public string EverythingAsText()
    {
        var text = new StringBuilder(ManifestText);
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*", SearchOption.AllDirectories))
        {
            if (path.EndsWith(".gz", StringComparison.Ordinal))
            {
                using var file = File.OpenRead(path);
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                using var reader = new StreamReader(gzip, Encoding.UTF8);
                text.Append(reader.ReadToEnd());
            }
            else if (!path.EndsWith("manifest.json", StringComparison.Ordinal))
                text.Append(Encoding.Latin1.GetString(File.ReadAllBytes(path)));
        }

        return text.ToString();
    }

    public void Dispose() => _manifest.Dispose();
}

/// <summary>The modules the snapshot tests use besides the real ones.</summary>
internal static class SnapshotTestModules
{
    /// <summary>One table of every logical type, a composite key, an older identity key and an added column.</summary>
    public static StateModule Types { get; } = new(
        "snapshot_types",
        StateScopeKind.Install,
        [
            """
            CREATE TABLE {table:sample} (
                id         {{uuid}} PRIMARY KEY,
                label      {{key}} NOT NULL,
                note       {{text}} NULL,
                created_at {{time}} NOT NULL,
                seen_at    {{time}} NULL,
                enabled    {{bool}} NOT NULL,
                amount     {{decimal}} NULL,
                doc        {{json}} NULL,
                data       {{blob}} NULL,
                size       {{int}} NOT NULL
            );
            CREATE TABLE {table:pair} (
                a     {{int}} NOT NULL,
                b     {{int}} NOT NULL,
                note  {{key}} NOT NULL,
                PRIMARY KEY (a, b)
            );
            CREATE TABLE {table:counter} (
                {{identity:id}},
                name {{key}} NOT NULL
            );
            """,
            "ALTER TABLE {table:sample} {{addcolumn}} extra {{key}} NULL;",
        ]);

    /// <summary>What the Proxy's serving module has that a snapshot cares about: the instance rows.</summary>
    public static StateModule Serving { get; } = new(
        "serving",
        StateScopeKind.Group,
        [
            """
            CREATE TABLE {table:serving_instance} (
                instance_key  {{key}} PRIMARY KEY,
                host          {{key}} NOT NULL,
                process_id    {{int}} NOT NULL,
                started_at    {{time}} NOT NULL,
                heartbeat_at  {{time}} NOT NULL
            );
            """,
        ]);

    /// <summary>A runner's instance rows, with a lease of their own.</summary>
    public static StateModule Runner { get; } = new(
        "runner",
        StateScopeKind.Group,
        [
            """
            CREATE TABLE {table:instance} (
                id                {{key}} PRIMARY KEY,
                host              {{key}} NOT NULL,
                lease_expires_at  {{time}} NOT NULL
            );
            """,
        ]);

    /// <summary>The group secrets of the Proxy's trust module: a name and a value that is the base64 of the encrypted secret.</summary>
    public static StateModule Trust { get; } = new(
        "trust",
        StateScopeKind.Group,
        [
            """
            CREATE TABLE {table:group_secret} (
                name   {{key}} PRIMARY KEY,
                value  {{text}} NOT NULL
            );
            """,
        ]);
}

/// <summary>Fills a state database through the real stores of the shared modules.</summary>
internal static class SnapshotPopulate
{
    public static readonly StateModule[] Modules =
    [
        IdentityModule.Module, NotificationsModule.Module, AuditModule.Module, DefinitionsModule.Module, RunsModule.Module, ProgressModule.Module,
    ];

    /// <summary>Returns the id of the payload with chunk data.</summary>
    public static Guid Fill(StateDatabase database)
    {
        // identity: people, their credentials, a session and an invitation
        var users = new IdentityUserStore(database);
        var ada = users.CreateUser("Ada Lovelace", "ada@example.invalid", UserRole.Admin);
        users.AddCredential(ada.Id, CredentialMethods.Windows, "S-1-5-21-1", secret: null, label: "work laptop");
        users.AddCredential(ada.Id, CredentialMethods.Passkey, "cred-1", secret: "public-key");
        var grace = users.CreateUser("Grace Hopper", null, UserRole.Viewer);
        new IdentitySessionStore(database).Create(grace.Id);
        new IdentityInviteStore(database).Create(UserRole.Viewer, createdByUserId: ada.Id);

        // notifications, audit, definitions
        var feed = new NotificationFeedStore(database, "sync");
        feed.Raise(NotificationKinds.RunFailed, "Replication 'crm' failed", taskName: "crm", mappingName: "orders", runId: Guid.NewGuid());
        feed.Raise(NotificationKinds.CertificateExpiring, "certificate expires");
        feed.MarkSeen("reader-1", feed.List()[0].Id);
        var audit = new AuditLog(database, "console");
        for (var i = 1; i <= 25; i++) audit.Record(new AuditEvent("person", "ada", "definition.publish", TargetKind: "definition", TargetId: $"d{i}", Detail: "{\"b\":1,\"a\":[1,2]}"));
        var definitions = new DefinitionStore(database);
        definitions.Publish("app", "crm", "v1 text", "ada", ["model:sales@3"], "first");
        definitions.Publish("app", "crm", "v2 text", "ada");

        // runs with their logs
        var runs = new RunStore(database, "dbdatasync");
        var run = runs.Start("orders-sync");
        for (var i = 1; i <= 40; i++) runs.Log(run, i % 10 == 0 ? "Error" : "Info", $"line {i}", item: i % 10 == 0 ? "dbo.Orders" : null);
        runs.Finish(run, RunOutcome.Failed, rows: 1200, failedItem: "dbo.Orders", failure: new RunFailure("Timeout", "the source did not answer"));
        runs.Start("nightly");

        // payloads with chunk data (one of several chunks, one empty, one never completed), a dead letter that owns one
        var payloads = new PayloadStore(database, chunkSize: 1024);
        var data = new byte[5000];
        new Random(5).NextBytes(data);
        Guid body;
        using (var writer = payloads.OpenWrite(new PayloadOptions(MediaType: "application/octet-stream")))
        {
            body = writer.Id;
            writer.Write(data);
            writer.Complete();
        }

        using (var empty = payloads.OpenWrite()) empty.Complete();
        using (var unfinished = payloads.OpenWrite()) unfinished.Write(data.AsSpan(0, 1500));

        // progress: checkpoints with history, a state value, a file ledger entry, an idempotency key, a dead letter
        var progress = new ProgressStore(database, new ProgressKey("orders-pkg", "prod", "sync-orders"));
        Assert.True(progress.Advance("orders", new Dictionary<string, ProgressValue> { ["at"] = ProgressValue.OfInt(10), ["modified"] = ProgressValue.OfTime(DateTimeOffset.Parse("2026-11-04T09:15:00.1234567+02:00")) }, 0, run));
        Assert.True(progress.Advance("orders", new Dictionary<string, ProgressValue> { ["at"] = ProgressValue.OfInt(20), ["modified"] = ProgressValue.OfTime(DateTimeOffset.Parse("2026-11-05T09:15:00Z")) }, 1, run));
        Assert.True(progress.SetState("cursor", ProgressValue.OfDecimal(12.5m)));
        progress.NoteFile("sftp", "/in/a.csv", 1234, DateTimeOffset.Parse("2026-11-04T09:00:00Z"), "abc123");
        progress.TryBeginIdempotent("key-1", TimeSpan.FromHours(1));
        progress.AddDeadLetter("crm", "400 Bad Request", bodyPayload: body);

        return body;
    }
}
