using DbDataBuild.Execution;
using DbDataBuild.State;
using Xunit;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// DESIGN.md 12 and 15.5 on real engines: the init script runs and is idempotent, the catalog reader reports shapes whose hashes track real changes,
/// and the tracking tables round-trip through the gate (real parameter binding into char/uuid/timestamp columns).
/// </summary>
public class TrackingConformanceTests
{
    public static TheoryData<string> Engines => new() { "sqlserver", "postgres" };

    private static LoginSettings Login(Engine e, Login which) =>
        LoginSettings.FromEnvironment(e.Name, which, _ => e.ConnectionString).Settings!;

    private static async Task<MutationGate> GateAsync(Engine e, StatementKind permitted, MemoryStatementLog? log = null) =>
        await MutationGate.OpenAsync(Login(e, DbDataBuild.Execution.Login.Write), "test", permitted, log ?? new MemoryStatementLog(), Guid.NewGuid());

    private const string SchemaName = "dbdatabuild";

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Init_creates_the_tracking_tables_and_is_idempotent(string name)
    {
        await using var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        await using var read = await ReadSession.OpenAsync(Login(engine, DbDataBuild.Execution.Login.Read));

        Assert.Equal(TrackingState.Missing, (await TrackingStore.StatusAsync(read, name, SchemaName)).State);
        await using (var gate = await GateAsync(engine, StatementKind.Tracking))
        {
            await TrackingStore.InitAsync(gate, name, SchemaName);
            await TrackingStore.InitAsync(gate, name, SchemaName); // twice: nothing changes, nothing fails
        }
        var status = await TrackingStore.StatusAsync(read, name, SchemaName);
        Assert.Equal(new TrackingStatus(TrackingState.Ready, TrackingSchema.Version), status);
        Assert.Null(status.AsDiagnostic(SchemaName));

        var shapes = await CatalogReader.ReadObjectsAsync(read, name, SchemaName);
        Assert.Equal(TrackingSchema.Tables.Select(t => $"{SchemaName}.{t.Name}").Order(), shapes.Where(x => x.Value.Kind == ObjectKind.Table).Select(x => x.Key).Order());
        Assert.Contains(shapes, x => x.Value.Kind == ObjectKind.View && x.Key == $"{SchemaName}.metadata_current");
        Assert.Single(await engine.RowsAsync($"SELECT version FROM {engine.QuoteIdent(SchemaName)}.{engine.QuoteIdent("tracking_version")}"));
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task An_unknown_layout_version_is_reported_not_assumed(string name)
    {
        await using var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        await using (var gate = await GateAsync(engine, StatementKind.Tracking)) await TrackingStore.InitAsync(gate, name, SchemaName);
        await engine.ExecAsync($"UPDATE {engine.QuoteIdent(SchemaName)}.{engine.QuoteIdent("tracking_version")} SET {engine.QuoteIdent("version")} = 99");
        await using var read = await ReadSession.OpenAsync(Login(engine, DbDataBuild.Execution.Login.Read));
        var status = await TrackingStore.StatusAsync(read, name, SchemaName);
        Assert.Equal(new TrackingStatus(TrackingState.UnknownLayout, 99), status);
        Assert.Equal("DDB-505", status.AsDiagnostic(SchemaName)!.Code);
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task The_catalog_reader_reports_shapes_whose_hashes_track_real_changes(string name)
    {
        await using var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        var q = engine.QuoteIdent;
        await engine.ExecAsync(name == "postgres" ? "CREATE SCHEMA marts" : "EXEC('CREATE SCHEMA marts')");
        var money = engine.ColumnType("DECIMAL(18,2)");
        await engine.ExecAsync($"CREATE TABLE marts.{q("fct")} ({q("id")} BIGINT NOT NULL, {q("label")} {engine.ColumnType("VARCHAR(20)")} NULL, {q("amount")} {money} NULL, {q("at")} {engine.ColumnType("TIMESTAMP")} NOT NULL)");
        await engine.ExecAsync($"CREATE INDEX ix_fct_id ON marts.{q("fct")} ({q("id")})");
        await engine.ExecAsync($"CREATE VIEW marts.{q("v_fct")} AS SELECT {q("id")} FROM marts.{q("fct")}");
        await using var read = await ReadSession.OpenAsync(Login(engine, DbDataBuild.Execution.Login.Read));

        var first = await CatalogReader.ReadObjectsAsync(read, name, "marts");
        var fct = first["marts.fct"];
        Assert.Equal(ObjectKind.Table, fct.Kind);
        Assert.Equal(["id", "label", "amount", "at"], fct.Columns.Select(c => c.Name));
        Assert.Equal(ObjectKind.View, first["marts.v_fct"].Kind);
        var label = fct.Columns.Single(c => c.Name == "label");
        Assert.Equal(20, label.Length);                                   // nvarchar(20) is 40 bytes on SQL Server: reported in characters
        Assert.True(label.Nullable);
        Assert.False(fct.Columns.Single(c => c.Name == "id").Nullable);
        var amount = fct.Columns.Single(c => c.Name == "amount");
        Assert.Equal((18, 2), (amount.Precision, amount.Scale));
        Assert.Contains(fct.Physical, p => p.Kind == "index" && p.Name == "ix_fct_id");

        // reading twice gives the same hashes
        var again = (await CatalogReader.ReadObjectsAsync(read, name, "marts"))["marts.fct"];
        Assert.Equal(fct.ShapeHash, again.ShapeHash);
        Assert.Equal(fct.PhysicalHash, again.PhysicalHash);

        // a new column changes the shape hash and leaves the physical hash alone
        await engine.ExecAsync($"ALTER TABLE marts.{q("fct")} ADD {q("note")} {engine.ColumnType("VARCHAR(5)")} NULL");
        var added = (await CatalogReader.ReadObjectsAsync(read, name, "marts"))["marts.fct"];
        Assert.NotEqual(fct.ShapeHash, added.ShapeHash);
        Assert.Equal(fct.PhysicalHash, added.PhysicalHash);

        // widening a column changes the shape hash (length is covered)
        await engine.ExecAsync(name == "postgres" ? $"ALTER TABLE marts.{q("fct")} ALTER COLUMN {q("label")} TYPE VARCHAR(40)" : $"ALTER TABLE marts.{q("fct")} ALTER COLUMN {q("label")} NVARCHAR(40) NULL");
        var widened = (await CatalogReader.ReadObjectsAsync(read, name, "marts"))["marts.fct"];
        Assert.NotEqual(added.ShapeHash, widened.ShapeHash);

        // a new index changes the physical hash and leaves the shape hash alone
        await engine.ExecAsync($"CREATE INDEX ix_fct_at ON marts.{q("fct")} ({q("at")})");
        var indexed = (await CatalogReader.ReadObjectsAsync(read, name, "marts"))["marts.fct"];
        Assert.Equal(widened.ShapeHash, indexed.ShapeHash);
        Assert.NotEqual(widened.PhysicalHash, indexed.PhysicalHash);
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task Schema_versions_round_trip_and_drift_is_classified_from_them(string name)
    {
        await using var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        var q = engine.QuoteIdent;
        await engine.ExecAsync(name == "postgres" ? "CREATE SCHEMA marts" : "EXEC('CREATE SCHEMA marts')");
        await engine.ExecAsync($"CREATE TABLE marts.{q("fct")} ({q("id")} BIGINT NOT NULL)");
        await using var read = await ReadSession.OpenAsync(Login(engine, DbDataBuild.Execution.Login.Read));
        await using (var g = await GateAsync(engine, StatementKind.Tracking)) await TrackingStore.InitAsync(g, name, SchemaName);

        ObjectShape? Live(IReadOnlyDictionary<string, ObjectShape> all) => all.GetValueOrDefault("marts.fct");
        var recorded = await TrackingStore.LatestShapeHashesAsync(read, new TrackingScope(name, SchemaName, "data"));
        var shapes = await CatalogReader.ReadObjectsAsync(read, name, "marts");
        Assert.Equal(ObjectState.Untracked, Drift.Classify(Live(shapes), recorded.GetValueOrDefault("marts.fct")));
        Assert.Equal(ObjectState.Missing, Drift.Classify(null, null));

        var log = new MemoryStatementLog();
        await using (var gate = await GateAsync(engine, StatementKind.Tracking, log))
        {
            await TrackingStore.RecordSchemaVersionAsync(gate, new TrackingScope(name, SchemaName, "data"), "rec-1", "marts.fct", Live(shapes)!.ShapeHash, Live(shapes)!.PhysicalHash, "tool", "plan-1", "abc123");
        }
        recorded = await TrackingStore.LatestShapeHashesAsync(read, new TrackingScope(name, SchemaName, "data"));
        Assert.Equal(ObjectState.InSync, Drift.Classify(Live(shapes), recorded["marts.fct"]));
        Assert.Equal(Live(shapes)!.ShapeHash, recorded["marts.fct"]);

        // an out-of-band change is seen as drift
        await engine.ExecAsync($"ALTER TABLE marts.{q("fct")} ADD {q("sneaky")} INT NULL");
        shapes = await CatalogReader.ReadObjectsAsync(read, name, "marts");
        Assert.Equal(ObjectState.OutOfBand, Drift.Classify(Live(shapes), recorded["marts.fct"]));

        // recording the new state as out_of_band makes it the newest record, even when written immediately after the first
        await using (var gate = await GateAsync(engine, StatementKind.Tracking))
            await TrackingStore.RecordSchemaVersionAsync(gate, new TrackingScope(name, SchemaName, "data"), "rec-2", "marts.fct", Live(shapes)!.ShapeHash, null, "out_of_band", null, null);
        recorded = await TrackingStore.LatestShapeHashesAsync(read, new TrackingScope(name, SchemaName, "data"));
        Assert.Equal(ObjectState.InSync, Drift.Classify(Live(shapes), recorded["marts.fct"]));
        var sources = await engine.RowsAsync($"SELECT source FROM {q(SchemaName)}.{q("schema_version")}");
        Assert.Equal(["out_of_band", "tool"], sources);

        // the statement log saw the parameters (as strings) and the outcome, and nothing from the driver
        Assert.Contains(log.Entries, e => e.Phase == "begin" && e.StepId == "rec-1" && e.Parameters!.Any(p => p.Name == "plan_id" && p.Value == "plan-1"));
        Assert.Contains(log.Entries, e => e.Phase == "end" && e.Outcome == "ok rows=1");
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_failing_statement_is_reported_without_the_drivers_message_in_the_log(string name)
    {
        await using var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        var log = new MemoryStatementLog();
        await using var gate = await GateAsync(engine, StatementKind.Data, log);
        await Assert.ThrowsAnyAsync<Exception>(() => gate.ExecuteAsync(GateStatement.FromPlanStep("bad", StatementKind.Data, "INSERT INTO no_such_table VALUES ('secret-value')")));
        var end = log.Entries.Single(e => e.Phase == "end");
        Assert.StartsWith("failed ", end.Outcome);
        Assert.DoesNotContain("no_such_table", end.Outcome);
        Assert.DoesNotContain("secret", end.Outcome);
    }

    [SkippableFact]
    public async Task A_postgres_read_session_is_read_only_at_the_session_level_too()
    {
        await using var engine = EngineEnv.Require("postgres");
        await engine.StartAsync();
        await using var read = await ReadSession.OpenAsync(Login(engine, DbDataBuild.Execution.Login.Read));
        var rows = await read.QueryAsync("SELECT current_setting('default_transaction_read_only')");
        Assert.Equal("on", rows.Single().Single());
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task The_init_command_applies_through_the_gate_and_leaves_a_statement_log(string name)
    {
        await using var engine = EngineEnv.Require(name);
        await engine.StartAsync();
        var dir = Path.Combine(Path.GetTempPath(), "ddb-init-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), $"defaults: {{connections: [{name}]}}\ntracking: {{ connection: {name}, schema: ddb_cli }}\n");
            Func<string, string?> env = v => v == LoginSettings.VariableName(name, DbDataBuild.Execution.Login.Write) ? engine.ConnectionString : null;
            for (var run = 1; run <= 2; run++) // the second run is a no-op, not an error
            {
                var o = new StringWriter();
                var e = new StringWriter();
                Assert.Equal(0, DbDataBuild.Cli.CliApp.Run(["connection", "init", "--project", dir, "--apply"], o, e, environment: env));
                Assert.Equal("", e.ToString());
                Assert.Contains("The tracking tables are ready.", o.ToString());
            }
            await using var read = await ReadSession.OpenAsync(Login(engine, DbDataBuild.Execution.Login.Read));
            Assert.Equal(new TrackingStatus(TrackingState.Ready, TrackingSchema.Version), await TrackingStore.StatusAsync(read, name, "ddb_cli"));

            var logs = Directory.GetFiles(Path.Combine(dir, ".dbdatabuild", "statement-log"), "*.jsonl");
            Assert.Equal(2, logs.Length);
            var lines = File.ReadAllLines(logs[0]);
            var statements = TrackingDdl.For(name).InitScript("ddb_cli", "x").Count;
            Assert.Equal(statements * 2, lines.Length);                       // a begin and an end per statement
            Assert.Equal(statements, lines.Count(l => l.Contains("\"phase\":\"begin\"")));
            Assert.DoesNotContain(lines, l => l.Contains("Password"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
