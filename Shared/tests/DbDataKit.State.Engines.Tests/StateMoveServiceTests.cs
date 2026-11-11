using DbDataKit.State.Engines.Snapshots;
using DbDataKit.State.Snapshots;

namespace DbDataKit.State.Tests;

/// <summary>Plan 29b M1/M5 and slice C04's core: <see cref="StateMoveService"/> between every pair of SQLite, PostgreSQL and SQL Server.</summary>
public sealed class StateMoveServiceTests : IDisposable
{
    private const string SettingsText = "# the product's settings\nname: demo\nlisten: 127.0.0.1:5000\nstate:\n  engine: sqlite\n# trailing comment\n";

    private readonly ServiceScratch _scratch = new();
    private readonly StateMoveService _service;
    private static readonly StateModule[] Modules = ServiceData.Modules;
    private static readonly StateScope Scope = StateScope.Standalone;

    public StateMoveServiceTests() => _service = new StateMoveService(_scratch.Resolver);

    public void Dispose() => _scratch.Dispose();

    private string SettingsFile()
    {
        var path = Path.Combine(_scratch.Directory, "settings.yaml");
        File.WriteAllText(path, SettingsText);
        return path;
    }

    private (StateEndpoint Endpoint, Guid Payload, byte[] Bytes) Seed(string engine, params StateModule[] extra)
    {
        var endpoint = _scratch.Provision(engine);
        var database = StateOpener.Open(endpoint.Settings, endpoint.SqliteFile!, [.. Modules, .. extra], Scope, _scratch.Resolver);
        var (payload, bytes) = ServiceData.Fill(database);
        return (endpoint, payload, bytes);
    }

    private Task<MoveResult> Move(StateEndpoint from, StateEndpoint to, string settings, MoveOptions? options = null, IProgress<MoveProgress>? progress = null, params StateModule[] extra) =>
        _service.MoveAsync(from, to, settings, [.. Modules, .. extra], Scope, options, progress, CancellationToken.None);

    /// <summary>Everything a person could be shown or find after a move: the result's texts, every file beside the settings and the kept snapshot.</summary>
    private string Everything(MoveResult result, params string[] directories) =>
        string.Join("\n", result.Summary, result.RestartInstruction, result.WayBack, result.Export.Directory, result.VerifiedAgainstSnapshot.Summary, result.VerifiedAgainstSource.Summary,
            string.Join("\n", result.Import.Warnings), ServiceScratch.EverythingUnder(directories));

    [Theory]
    [MemberData(nameof(ServiceScratch.Pairs), MemberType = typeof(ServiceScratch))]
    public async Task A_move_writes_the_new_settings_only_after_the_target_is_verified_and_the_product_then_opens_it(string from, string to)
    {
        var (source, payload, bytes) = Seed(from);
        var target = _scratch.Provision(to);
        var settings = SettingsFile();
        var steps = new List<MoveStep>();

        var result = await Move(source, target, settings, new MoveOptions { DeleteSnapshotOnSuccess = true }, new InlineProgress<MoveProgress>(p => { if (steps.Count == 0 || steps[^1] != p.Step) steps.Add(p.Step); }));

        Assert.Equal([MoveStep.Preflight, MoveStep.Export, MoveStep.Import, MoveStep.VerifySnapshot, MoveStep.VerifyDatabases, MoveStep.WriteSettings, MoveStep.Cleanup, MoveStep.Done], steps);
        Assert.True(result.VerifiedAgainstSnapshot.Equal, result.VerifiedAgainstSnapshot.Summary);
        Assert.True(result.VerifiedAgainstSource.Equal, result.VerifiedAgainstSource.Summary);
        Assert.Contains("Restart the service", result.RestartInstruction);
        Assert.Contains(".before-state-move", result.WayBack);

        // the settings file names the target and nothing else of it changed; the previous file is kept
        var written = StateSettingsWriter.Load(settings);
        Assert.Equal(target.Engine, written.EngineId);
        var text = File.ReadAllText(settings);
        Assert.Contains("# the product's settings\nname: demo\nlisten: 127.0.0.1:5000\n", text);
        Assert.Equal(SettingsText, File.ReadAllText(result.BackupFile!));
        Assert.Equal(settings + ".before-state-move", result.BackupFile);

        // the temporary snapshot is gone
        Assert.Null(result.SnapshotDirectory);
        Assert.False(Directory.Exists(result.Export.Directory));

        // the new settings open the target and the stores read what the old database held
        var database = StateOpener.Open(written with { ConnectionString = target.Settings.ConnectionString }, target.SqliteFile ?? "", Modules, Scope, _scratch.Resolver);
        Assert.Equal(2, new IdentityUserStore(database).List().Count);
        Assert.Single(new RunStore(database, "dbdatasync").Read(new RunQuery(JobId: "orders-sync")));
        using var stream = new PayloadStore(database).OpenRead(payload);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        Assert.Equal(bytes, copy.ToArray());

        // the old database is as it was: it still verifies against the new one
        var again = await new StateSnapshotService(_scratch.Resolver).VerifyAsync(source, target, Modules, Scope, new StateVerifyOptions { RoundedTables = result.Import.RoundedTables }, null, CancellationToken.None);
        Assert.True(again.Equal, again.Summary);
    }

    [Theory]
    [InlineData("sqlite", "sqlite")]
    [InlineData("sqlite", "postgres")]
    [InlineData("mssql", "sqlite")]
    public async Task A_target_that_cannot_decrypt_a_seeded_secret_fails_before_the_settings_are_written(string from, string to)
    {
        var (source, _, _) = Seed(from);
        var target = _scratch.Provision(to);
        var settings = SettingsFile();
        var before = File.ReadAllBytes(settings);
        var asked = new List<string>();

        var failure = await Assert.ThrowsAsync<StateMoveException>(() => Move(source, target, settings, new MoveOptions
        {
            SecretSamples = [new StateSecretSample("svc_types", "sample", "label", value => { asked.Add(value); return false; })],
        }));

        Assert.Equal(MoveStep.CheckSecrets, failure.Step);
        Assert.NotEmpty(asked);
        Assert.Contains("svc_types.sample.label", failure.Message);
        Assert.Contains("secret store", failure.Message);
        Assert.DoesNotContain(asked[0], failure.Message.Replace("svc_types.sample.label", ""));   // the value is never in the message
        Assert.Equal(before, File.ReadAllBytes(settings));
        Assert.Empty(Directory.GetFiles(_scratch.Directory, "settings.yaml.before-state-move*"));
    }

    [Theory]
    [InlineData("sqlite", "sqlite")]
    public async Task A_target_that_decrypts_the_sample_is_moved_and_the_restart_instruction_names_the_command(string from, string to)
    {
        var (source, _, _) = Seed(from);
        var target = _scratch.Provision(to);
        var steps = new List<MoveStep>();

        var result = await Move(source, target, SettingsFile(), new MoveOptions
        {
            SecretSamples = [new StateSecretSample("svc_types", "sample", "label", _ => true, Sample: 2), new StateSecretSample("not_in_this_layout", "x", "y", _ => false)],
            RestartCommand = "demo service restart",
        }, new InlineProgress<MoveProgress>(p => { if (steps.Count == 0 || steps[^1] != p.Step) steps.Add(p.Step); }));

        Assert.Contains(MoveStep.CheckSecrets, steps);
        Assert.True(steps.IndexOf(MoveStep.CheckSecrets) < steps.IndexOf(MoveStep.WriteSettings));
        Assert.Contains("('demo service restart')", result.RestartInstruction);
    }

    [Theory]
    [InlineData("sqlite", "postgres")]
    [InlineData("postgres", "mssql")]
    [InlineData("mssql", "sqlite")]
    public async Task A_failed_verification_leaves_the_settings_unchanged_and_the_snapshot_for_inspection(string from, string to)
    {
        var (source, _, _) = Seed(from);
        var target = _scratch.Provision(to);
        var settings = SettingsFile();
        var before = File.ReadAllBytes(settings);

        var failure = await Assert.ThrowsAsync<StateMoveException>(() => Move(source, target, settings, new MoveOptions
        {
            AfterImport = (database, _) =>
            {
                using var connection = database.OpenConnection();
                using var damage = database.Command("svc_types", connection, "UPDATE {table:sample} SET label = 'tampered' WHERE size = 1;");
                damage.ExecuteNonQuery();
                return Task.CompletedTask;
            },
        }));

        // against the snapshot a table whose times the engine rounded can only be checked loosely; against the source database it is exact
        Assert.True(failure.Step is MoveStep.VerifySnapshot or MoveStep.VerifyDatabases, failure.Step.ToString());
        Assert.False(failure.SettingsChanged);
        Assert.Contains("svc_types.sample", failure.Message);
        Assert.Contains("was not changed", failure.Message);
        Assert.Equal(before, File.ReadAllBytes(settings));
        Assert.Empty(Directory.GetFiles(_scratch.Directory, "settings.yaml.before-state-move*"));

        // the snapshot is kept, complete and intact
        Assert.NotNull(failure.SnapshotDirectory);
        Assert.True(File.Exists(Path.Combine(failure.SnapshotDirectory!, "manifest.json")));
        Assert.True((await new StateSnapshotService().VerifyAsync(failure.SnapshotDirectory!, null, CancellationToken.None)).Equal);
        Assert.StartsWith(_scratch.Directory, failure.SnapshotDirectory!, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ServiceScratch.Engines), MemberType = typeof(ServiceScratch))]
    public async Task A_source_a_live_instance_uses_is_refused_and_nothing_changes(string engine)
    {
        var (source, _, _) = Seed(engine, ServiceData.Runner);
        var live = StateOpener.Open(source.Settings, source.SqliteFile!, [.. Modules, ServiceData.Runner], Scope, _scratch.Resolver);
        using (var connection = live.OpenConnection())
        {
            using var insert = live.Command("runner", connection, "INSERT INTO {table:instance} (id, host, lease_expires_at) VALUES ($id, $host, $lease);");
            insert.Bind(live, "id", "worker-1").Bind(live, "host", "host-a").Bind(live, "lease", DateTimeOffset.UtcNow.AddMinutes(10));
            insert.ExecuteNonQuery();
        }

        var target = _scratch.Provision("sqlite");
        var settings = SettingsFile();
        var before = File.ReadAllBytes(settings);

        var failure = await Assert.ThrowsAsync<StateMoveException>(() => Move(source, target, settings, extra: ServiceData.Runner));

        Assert.Equal(MoveStep.Export, failure.Step);
        var refusal = Assert.IsType<SnapshotRefusedException>(failure.InnerException);
        Assert.Equal(SnapshotRefusal.InstanceInUse, refusal.Reason);
        Assert.Contains("worker-1", failure.Message);
        Assert.Equal(before, File.ReadAllBytes(settings));
        Assert.Null(failure.SnapshotDirectory);
        Assert.Empty(Directory.GetDirectories(_scratch.Directory, "state-move-*"));
        Assert.Empty(StateOpener.OpenWithoutMigrating(target.Settings, target.SqliteFile, Modules, Scope).RegisteredModules());
    }

    [Fact]
    public async Task A_failed_preflight_changes_nothing_and_says_why()
    {
        var (source, _, _) = Seed("sqlite");
        var settings = SettingsFile();
        var before = File.ReadAllBytes(settings);

        var same = await Assert.ThrowsAsync<StateMoveException>(() => Move(source, source, settings));
        Assert.Equal(MoveStep.Preflight, same.Step);
        Assert.Equal(StateServiceRefusal.SameDatabase, Assert.IsType<StateServiceException>(same.InnerException).Reason);

        var (occupied, _, _) = Seed("sqlite");
        var notEmpty = await Assert.ThrowsAsync<StateMoveException>(() => Move(source, occupied, settings));
        Assert.Equal(MoveStep.Preflight, notEmpty.Step);
        Assert.Equal(StateServiceRefusal.TargetNotEmpty, Assert.IsType<StateServiceException>(notEmpty.InnerException).Reason);

        var invalid = await Assert.ThrowsAsync<StateMoveException>(() => Move(source, new StateEndpoint(new StateSettings("sqlite") { Retention = new RetentionSettings { Days = 0 } }, Path.Combine(_scratch.Directory, "new.db")), settings));
        Assert.Equal(MoveStep.Preflight, invalid.Step);
        Assert.Contains("settings.yaml", invalid.Message);
        Assert.False(File.Exists(Path.Combine(_scratch.Directory, "new.db")), "the target was not created: the settings were checked first");

        Assert.Equal(before, File.ReadAllBytes(settings));
        Assert.Empty(Directory.GetDirectories(_scratch.Directory, "state-move-*"));
        Assert.Empty(Directory.GetFiles(_scratch.Directory, ".state-move-check-*"));
    }

    [Theory]
    [InlineData("postgres", "mssql")]
    [InlineData("mssql", "postgres")]
    public async Task A_secret_appears_in_no_result_message_or_file_but_stays_a_reference_in_the_settings(string from, string to)
    {
        var (source, _, _) = Seed(from);
        var target = _scratch.Provision(to);
        var settings = SettingsFile();

        var result = await Move(source, target, settings, new MoveOptions { DeleteSnapshotOnSuccess = false });

        Assert.NotNull(result.SnapshotDirectory);
        var everything = Everything(result, _scratch.Directory);
        foreach (var password in _scratch.Passwords) Assert.DoesNotContain(password, everything, StringComparison.Ordinal);
        Assert.Contains("Password=secret:", File.ReadAllText(settings));
        Assert.Equal(target.Settings.ConnectionString, StateSettingsWriter.Load(settings).ConnectionString);
        Assert.NotNull(StateSettingsWriter.Load(settings).ConnectionString);
        // a failed move says the same: a damaged target, and the text still holds no secret
        var (other, _, _) = Seed(from);
        var broken = _scratch.Provision(to);
        var failure = await Assert.ThrowsAsync<StateMoveException>(() => Move(other, broken, SettingsFile(), new MoveOptions
        {
            AfterImport = (database, _) =>
            {
                using var connection = database.OpenConnection();
                using var damage = database.Command("svc_types", connection, "DELETE FROM {table:counter};");
                damage.ExecuteNonQuery();
                return Task.CompletedTask;
            },
        }));
        foreach (var password in _scratch.Passwords) Assert.DoesNotContain(password, failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_snapshot_can_be_kept_where_the_operator_says_and_a_move_without_a_settings_file_creates_one()
    {
        var (source, _, _) = Seed("sqlite");
        var target = _scratch.Provision("sqlite");
        var settings = Path.Combine(_scratch.Directory, "fresh.yaml");
        var kept = Path.Combine(_scratch.Directory, "keep-me");

        var result = await Move(source, target, settings, new MoveOptions { SnapshotDirectory = kept, DeleteSnapshotOnSuccess = false });

        Assert.Equal(kept, result.SnapshotDirectory);
        Assert.True(File.Exists(Path.Combine(kept, "manifest.json")));
        Assert.Null(result.BackupFile);
        Assert.Contains("did not exist before the move", result.WayBack);
        Assert.Equal(StateEngineIds.Sqlite, StateSettingsWriter.Load(settings).EngineId);
        Assert.Contains("state:", File.ReadAllText(settings));
    }
}
