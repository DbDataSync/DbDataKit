using DbDataKit.Access;
using DbDataKit.Apps;
using DbDataKit.DataModel;
using DbDataKit.DataModel.Storage;
using DbDataKit.Secrets;
using DbDataKit.Serving;
using DbDataKit.State;
using DbDataKit.Store;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Auth;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Data;
using DbDataGateway.Server.Management;
using DbDataGateway.Server.Secrets;
using DbDataGateway.Server.Serving;
using DbDataGateway.Server.State;
using DbDataGateway.Tests.Config;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Data.Common;

namespace DbDataGateway.Tests.Serving;

/// <summary>
/// Plan 22a GP11 (QG2, QR1 to QR3, QR6), the Store entity half, end to end on SQL Server and PostgreSQL: the group's store API reads an entity of a published model through the caller's row rules (the home groups the caller carries, or the home's when
/// the call came without them, as the apps host's does). A person in a group reads exactly the rows the rules table gives, two callers get two answers, a new subject in the rules table shows after the producer's <c>data.changed</c>, the count of a list is
/// over the filter, a get, an as-of read or a history of a row the caller may not read answers not found as for a row that is not there, a rules table past half its age is served and alerted and past its age refuses, and a write is not filtered.
/// </summary>
public sealed class EntityRowRulesEngineTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dbgateway-entity-access-").FullName;
    private ScratchStateDatabase? _scratch;
    private StateDatabase? _state;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        NpgsqlConnection.ClearAllPools();
        SqlConnection.ClearAllPools();
        _scratch?.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    public static IEnumerable<object[]> Engines => [["mssql"], ["postgres"]];

    private sealed class Monitor(GatewayOptions value) : IOptionsMonitor<GatewayOptions>
    {
        public GatewayOptions CurrentValue => value;
        public GatewayOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<GatewayOptions, string?> listener) => null;
    }

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Audit(List<string> alerts) : IAuditLog
    {
        public void Append(AuditEntry entry) => alerts.Add(entry.Command + ": " + entry.Target);

        public IReadOnlyList<AuditEntry> Read(int limit = 100) => [];
    }

    private const string Model = """
        schemaVersion: 2
        name: sales
        storage: {connection: mdm_sql}
        access:
          max_age: 1h
          row:
            - dataset: territory
              groups:
                - id: by_region
                  rules:
                    - { id: region, effect: include, table: rules.region_access, subject: who, columns: { region: region_code } }
                    - { id: not_closed, effect: exclude, table: rules.closed_codes, subject: who, columns: { code: code } }
            - dataset: price
              groups:
                - id: price_by_region
                  rules:
                    - { id: price_region, effect: include, table: rules.region_access, subject: who, columns: { region: region_code } }
        entities:
          territory:
            key: [code]
            attributes:
              code: {type: VARCHAR(10), required: true}
              region: {type: VARCHAR(20)}
          price:
            key: [id]
            history: effective_dated
            attributes:
              id: {type: INTEGER, required: true}
              region: {type: VARCHAR(20)}
          note:
            key: [id]
            attributes:
              id: {type: INTEGER, required: true}
              text: {type: VARCHAR(40)}
        """;

    private sealed class Fixture
    {
        public required string Engine { get; init; }
        public required string Connection { get; init; }
        public required GatewayOptions Options { get; init; }
        public required Clock Time { get; init; }
        public required GroupStoreApi Api { get; init; }
        public required GatewayAccess Access { get; init; }
        public required List<string> Alerts { get; init; }
        public required Dictionary<string, string[]> Groups { get; init; }

        public void Execute(string sql)
        {
            DbConnection c = Engine == "mssql" ? new SqlConnection(Connection) : new NpgsqlConnection(Connection);
            using (c)
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>The producer says a rules table was refreshed: what the management call does (the table's age starts again and the names it holds are read again).</summary>
        public void Refreshed(string table) =>
            Access.Changed(new DataChangedNotice(GatewayLineageGraph.DatasetIdentity(Options.DataSources, (_, text) => text, "mdm_sql", table)!, "build", Time.Now));
    }

    private Fixture Make(string engine)
    {
        _scratch = ScratchStateDatabase.For(engine);
        var connection = _scratch.Connection.Replace("env:" + _scratch.PasswordVariable, _scratch.Environment(_scratch.PasswordVariable));
        var options = new GatewayOptions { DataDirectory = _dir };   // the key ring of the secrets runtime opens its folder under it: never the default /var/lib/dbdatakit
        options.DataSources["mdm_sql"] = new DataSourceOptions { Provider = engine == "mssql" ? "sqlserver" : "postgres", ConnectionString = engine == "mssql" ? connection : connection + ";SSL Mode=Disable", Everyone = true };
        var clock = new Clock(DateTimeOffset.Parse("2026-11-12T10:00:00Z"));
        var monitor = new Monitor(options);
        _state = GatewayState.Open(_dir);
        var models = new PublishedEntityModels(ServingKinds.StoreOf(_state));
        models.Publish("sales", Model, "mdm_sql", null, "ada", format: 2);
        var served = models.Find("sales") ?? throw new InvalidOperationException("The model was not served.");
        var dbEngine = engine == "mssql" ? "sqlserver" : "postgres";
        var fixture = new Fixture
        {
            Engine = engine, Connection = connection, Options = options, Time = clock, Alerts = [], Groups = new(StringComparer.Ordinal),
            Access = null!, Api = null!,
        };
        // the data: the model's tables (made as a DbDataStore server makes them), the rules tables a Build model would produce, and the rows
        DbConnection data = engine == "mssql" ? new SqlConnection(connection) : new NpgsqlConnection(connection);
        data.Open();
        using (data)
        {
            ModelStorage.CreateAsync(data, ModelStorage.Plan(served.Model, dbEngine)).GetAwaiter().GetResult();
            foreach (var statement in new[]
            {
                "CREATE SCHEMA rules",
                "CREATE TABLE rules.region_access (who VARCHAR(40), region_code VARCHAR(20))",
                "CREATE TABLE rules.closed_codes (who VARCHAR(40), code VARCHAR(10))",
                // east: East (twice in the table, once in the answer) without the closed E3; emea: both regions; nullers: the rows with no region
                "INSERT INTO rules.region_access VALUES ('east', 'East'), ('east', 'East'), ('emea', 'East'), ('emea', 'West'), ('nullers', NULL)",
                "INSERT INTO rules.closed_codes VALUES ('east', 'E3')",
            })
            {
                using var command = data.CreateCommand();
                command.CommandText = statement;
                command.ExecuteNonQuery();
            }

            var writer = new ModelWriter(served.Model, data, dbEngine);
            var ada = new WriteActor("u-ada", "Ada");
            foreach (var (code, region) in new[] { ("E1", "East"), ("E2", "East"), ("E3", "East"), ("W1", "West"), ("N1", "North"), ("X1", null) })
                writer.InsertAsync("territory", new Dictionary<string, object?> { ["code"] = code, ["region"] = region }, ada).GetAwaiter().GetResult();
            var now = clock.Now;
            writer.ApplyAsync([RowChange.Insert("price", new Dictionary<string, object?> { ["id"] = 1, ["region"] = "East" }, now.AddDays(-10))], ada).GetAwaiter().GetResult();
            writer.ApplyAsync([RowChange.Update("price", new Dictionary<string, object?> { ["id"] = 1 }, 1, new Dictionary<string, object?> { ["region"] = "West" }, now.AddDays(-5))], ada).GetAwaiter().GetResult();
            writer.InsertAsync("note", new Dictionary<string, object?> { ["id"] = 1, ["text"] = "everyone" }, ada).GetAwaiter().GetResult();
        }

        var ledger = RulesTableLedger.For(_state, clock);
        var access = new StoreEntityAccess(ledger, userId => fixture.Groups.GetValueOrDefault(userId) ?? [], new Alerts(fixture.Alerts), clock);
        var sources = new DataSourceCatalog(monitor, GatewaySources.Providers());
        var rulesChanged = new GatewayAccess(sources, () => options, () => new DbDataKit.Serving.Semantic.SemanticModels(), ledger,
            (source, physical) => GatewayLineageGraph.DatasetIdentity(options.DataSources, (_, text) => text, source, physical), new Audit(fixture.Alerts), NullLogger.Instance, clock, () => models.All);
        var secrets = new SecretsRuntime(new Redactor(), monitor, NullLoggerFactory.Instance);
        return new Fixture
        {
            Engine = engine, Connection = connection, Options = options, Time = clock, Alerts = fixture.Alerts, Groups = fixture.Groups,
            Access = rulesChanged, Api = new GroupStoreApi(() => options, secrets, models, null!, access),
        };
    }

    private sealed class Alerts(List<string> sink) : IAccessAlerts
    {
        public void RulesTableStale(RulesTableStatus status) => sink.Add(status.Table);
    }

    private static StoreCaller Person(string id, params string[] groups) => new(id, id, false, ServerRole.Viewer, Groups: groups);

    private static readonly IReadOnlyDictionary<string, string> NoQuery = new Dictionary<string, string>();

    private static async Task<string[]> Codes(GroupStoreApi api, StoreCaller caller, string entity = "territory")
    {
        var answer = await api.RowsAsync(caller, "sales", entity, NoQuery, CancellationToken.None);
        Assert.Equal(200, answer.Status);
        return [.. ((System.Text.Json.Nodes.JsonArray)answer.Body["rows"]!).Select(r => (string)r!["values"]!["code"]!)];
    }

    private static Dictionary<string, string> Key(string value) => new() { ["key.code"] = value };

    [Theory, MemberData(nameof(Engines))]
    public async Task A_person_in_a_group_reads_exactly_the_rows_the_rules_table_gives_and_two_callers_get_two_answers(string engine)
    {
        var f = Make(engine);
        Assert.Equal(["E1", "E2"], await Codes(f.Api, Person("u1", "east")));
        Assert.Equal(["E1", "E2", "E3", "W1"], await Codes(f.Api, Person("u2", "emea")));
        Assert.Equal(["X1"], await Codes(f.Api, Person("u3", "nullers")));
        // a person no rule names reads none, and no refusal says so (D2); an administrator is a caller like any other (QR3)
        Assert.Empty(await Codes(f.Api, Person("u4", "nobody")));
        Assert.Empty(await Codes(f.Api, new StoreCaller("root", "Root", false, ServerRole.Admin, Groups: ["administrators"])));
        // an entity of the model with no rule is read in full
        var notes = await f.Api.RowsAsync(Person("u4", "nobody"), "sales", "note", NoQuery, CancellationToken.None);
        Assert.Single((System.Text.Json.Nodes.JsonArray)notes.Body["rows"]!);
        Assert.Null(notes.Body["total"]);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task The_total_of_a_list_is_over_the_filter_and_a_get_of_a_row_the_caller_may_not_read_says_not_found(string engine)
    {
        var f = Make(engine);
        var east = Person("u1", "east");
        var page = await f.Api.RowsAsync(east, "sales", "territory", new Dictionary<string, string> { ["limit"] = "1", ["offset"] = "1" }, CancellationToken.None);
        Assert.Equal(2L, (long)page.Body["total"]!);
        Assert.Single((System.Text.Json.Nodes.JsonArray)page.Body["rows"]!);
        Assert.Equal(4L, (long)(await f.Api.RowsAsync(Person("u2", "emea"), "sales", "territory", NoQuery, CancellationToken.None)).Body["total"]!);

        // E1 is east's; E3 is excluded for east; W1 is another region's; NOPE is not there: the three that are not east's answer the same, so existence is not revealed
        Assert.Equal(200, (await f.Api.RowAsync(east, "sales", "territory", Key("E1"), CancellationToken.None)).Status);
        var hidden = new[] { "E3", "W1", "NOPE" };
        var answers = new List<(int, string?)>();
        foreach (var code in hidden)
        {
            var answer = await f.Api.RowAsync(east, "sales", "territory", Key(code), CancellationToken.None);
            answers.Add((answer.Status, ((string?)answer.Body["error"])?.Replace(code, "*")));
        }

        Assert.All(answers, a => Assert.Equal((404, "`territory` has no such row."), a));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task The_as_of_read_and_the_history_of_a_hidden_row_are_not_found_too(string engine)
    {
        var f = Make(engine);
        var east = Person("u1", "east");
        var key = new Dictionary<string, string> { ["key.id"] = "1" };
        var now = f.Time.Now;
        // the row was East ten days ago and is West now: east reads the East version of its history, not the open one
        Assert.Equal(404, (await f.Api.RowAsync(east, "sales", "price", key, CancellationToken.None)).Status);
        Assert.Equal(200, (await f.Api.RowAsync(east, "sales", "price", new Dictionary<string, string>(key) { ["as_of"] = now.AddDays(-7).ToString("o") }, CancellationToken.None)).Status);
        var history = await f.Api.RowAsync(east, "sales", "price", new Dictionary<string, string>(key) { ["history"] = "true" }, CancellationToken.None);
        Assert.Single((System.Text.Json.Nodes.JsonArray)history.Body["versions"]!);
        // a person who may read none of its versions is told what they are told of a row that was never there
        var none = await f.Api.RowAsync(Person("u4", "nobody"), "sales", "price", new Dictionary<string, string>(key) { ["history"] = "true" }, CancellationToken.None);
        Assert.Equal(404, none.Status);
        Assert.Equal(404, (await f.Api.RowAsync(Person("u4", "nobody"), "sales", "price", new Dictionary<string, string> { ["key.id"] = "99", ["history"] = "true" }, CancellationToken.None)).Status);
        Assert.Equal(2, ((System.Text.Json.Nodes.JsonArray)(await f.Api.RowAsync(Person("u2", "emea"), "sales", "price", new Dictionary<string, string>(key) { ["history"] = "true" }, CancellationToken.None)).Body["versions"]!).Count);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_new_subject_in_the_rules_table_shows_after_the_producers_data_changed_and_a_new_row_of_a_known_subject_at_once(string engine)
    {
        var f = Make(engine);
        var newcomer = Person("u5", "newcomers");
        Assert.Empty(await Codes(f.Api, newcomer));

        f.Execute("INSERT INTO rules.region_access VALUES ('newcomers', 'North')");
        // the rows of the table are read live, but the names it holds are read once per generation: until the producer says it refreshed, a subject the table had no row for is not known
        Assert.Empty(await Codes(f.Api, newcomer));
        f.Time.Now += TimeSpan.FromMinutes(5);
        f.Refreshed("rules.region_access");
        Assert.Equal(["N1"], await Codes(f.Api, newcomer));

        // east already had a rule row: its new row is read at once
        f.Execute("INSERT INTO rules.region_access VALUES ('east', 'West')");
        Assert.Equal(["E1", "E2", "W1"], await Codes(f.Api, Person("u1", "east")));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_rules_table_past_half_its_age_is_served_and_alerted_once_and_past_its_age_refuses(string engine)
    {
        var f = Make(engine);
        var east = Person("u1", "east");
        Assert.Equal(["E1", "E2"], await Codes(f.Api, east));
        f.Time.Now += TimeSpan.FromMinutes(40);   // past half of an hour
        for (var i = 0; i < 3; i++) Assert.Equal(["E1", "E2"], await Codes(f.Api, east));
        Assert.Equal(["rules.closed_codes", "rules.region_access"], f.Alerts.Distinct().Order().ToArray());
        Assert.Equal(2, f.Alerts.Count);   // once per table, however many reads follow

        f.Time.Now += TimeSpan.FromMinutes(30);   // past the hour
        var refused = await f.Api.RowsAsync(east, "sales", "territory", NoQuery, CancellationToken.None);
        Assert.Equal(403, refused.Status);
        Assert.Contains("is not served", (string?)refused.Body["error"]);
        // a person the table has no row for sees nothing anyway, with no refusal
        Assert.Equal(200, (await f.Api.RowsAsync(Person("u4", "nobody"), "sales", "territory", NoQuery, CancellationToken.None)).Status);
        f.Refreshed("rules.region_access");
        f.Refreshed("rules.closed_codes");
        Assert.Equal(["E1", "E2"], await Codes(f.Api, east));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_write_is_not_filtered_and_the_apps_host_reads_the_same_filtered_rows_with_the_groups_the_home_gives(string engine)
    {
        var f = Make(engine);
        // QR6: a person who cannot read W1 may still change it (the Store's roles decide); the row stays hidden from them
        var nobody = new StoreCaller("u4", "u4", false, ServerRole.Author, Groups: ["nobody"]);
        var body = System.Text.Encoding.UTF8.GetBytes("""{"changes":[{"action":"update","entity":"territory","key":{"code":"W1"},"expected_version":1,"values":{"region":"East"}}]}""");
        Assert.Equal(200, (await f.Api.ChangesAsync(nobody, "sales", body, null, CancellationToken.None)).Status);
        Assert.Empty(await Codes(f.Api, nobody));
        Assert.Equal(["E1", "E2", "W1"], await Codes(f.Api, Person("u1", "east")));   // the moved row is east's now

        // the apps host's call carries the person and the roles, not the groups: the group's directory gives them, so the same rows come back
        f.Groups["u1"] = ["east"];
        var host = new GroupAppData(f.Api);
        var call = new AppDataCall("sales-app", "u1", "u1", ServerRole.Viewer, [TokenScope.Entry("sales", "territory", write: false)], AppDataRoute.Rows, "sales", "territory", null, NoQuery, []);
        var answer = await host.SendAsync(call, CancellationToken.None);
        Assert.Equal(200, answer.Status);
        var rows = System.Text.Json.Nodes.JsonNode.Parse(answer.Body)!["rows"]!.AsArray().Select(r => (string)r!["values"]!["code"]!).ToArray();
        Assert.Equal(["E1", "E2", "W1"], rows);
        // a person the home has no groups for reads nothing
        var stranger = await host.SendAsync(call with { SubjectId = "u9", SubjectName = "u9" }, CancellationToken.None);
        Assert.Empty(System.Text.Json.Nodes.JsonNode.Parse(stranger.Body)!["rows"]!.AsArray());
    }
}
