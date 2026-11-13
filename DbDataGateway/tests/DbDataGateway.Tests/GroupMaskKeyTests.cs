using DbDataKit.Serving.Semantic;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Data;
using DbDataGateway.Server.Management;
using DbDataGateway.Server.Serving;
using DbDataGateway.Server.State;
using DbDataGateway.Tests.Hosting;
using Microsoft.Extensions.Options;

namespace DbDataGateway.Tests;

/// <summary>
/// Plan 22a GP13 (owner decision 2026-11-12): the key of the hash mask is the group's. Made once on first use, the same for every instance of the group, the same after a restart, never in the state in the clear; its rotation is an explicit
/// administrator's call that changes every hash and is audited. On SQLite, and on PostgreSQL and SQL Server when the shared engines are up.
/// </summary>
public sealed class GroupMaskKeyTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "dbgateway-maskkey-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly StateVerbDatabases databases = new();

    public void Dispose()
    {
        databases.Dispose();
        Environment.SetEnvironmentVariable(StateVerbDatabases.PasswordVariable, null);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        DbDataGateway.Tests.Config.TestFolder.Delete(directory);
    }

    private sealed class StaticMonitor(GatewayOptions value) : IOptionsMonitor<GatewayOptions>
    {
        public GatewayOptions CurrentValue => value;
        public GatewayOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<GatewayOptions, string?> listener) => null;
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ListAudit : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];
        public void Append(AuditEntry entry) => Entries.Add(entry);
        public IReadOnlyList<AuditEntry> Read(int limit = 100) => Entries;
    }

    /// <summary>One instance of the group: its own state connection and key object (the same secret store and state database, as a group has).</summary>
    private (GroupMaskKey Key, StateDatabase State) Instance(string engine, string? connection, Clock clock)
    {
        var options = new GatewayOptions { DataDirectory = directory };
        var state = GatewayState.Open(directory, engine == "sqlite" ? null : new StateSettings(engine, connection!), text => text.Replace($"env:{StateVerbDatabases.PasswordVariable}", databases.Password, StringComparison.Ordinal));
        return (new GroupMaskKey(state, new GroupSessionKey(state, new StaticMonitor(options)), clock), state);
    }

    private string? Connect(string engine)
    {
        if (engine == "sqlite") return null;
        var connection = databases.Provision(engine);
        Environment.SetEnvironmentVariable(StateVerbDatabases.PasswordVariable, databases.Password);
        return connection;
    }

    [Theory]
    [MemberData(nameof(StateVerbDatabases.Engines), MemberType = typeof(StateVerbDatabases))]
    public void The_key_is_made_once_on_first_use_and_is_the_same_for_every_instance_and_after_a_restart(string engine)
    {
        var connection = Connect(engine);
        var clock = new Clock();
        var (a, state) = Instance(engine, connection, clock);
        var (b, _) = Instance(engine, connection, clock);
        Assert.False(a.Exists());
        var first = a.Current();
        Assert.Equal(32, first.Length);
        Assert.True(b.Exists());
        Assert.Equal(first, b.Current());          // another instance of the group has the same
        Assert.Equal(first, a.Current());
        var (restarted, _) = Instance(engine, connection, clock);
        Assert.Equal(first, restarted.Current());  // and a new process of the same group

        // the state holds it only encrypted: the raw key is not in the row
        var stored = state.Retry(() =>
        {
            using var open = state.OpenConnection();
            using var read = state.Command("trust", open, "SELECT value FROM {table:group_secret} WHERE name = $name;");
            read.Bind(state, "name", GroupMaskKey.SecretName);
            return (string)read.ExecuteScalar()!;
        });
        Assert.DoesNotContain(Convert.ToBase64String(first), stored, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(first), stored, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(StateVerbDatabases.Engines), MemberType = typeof(StateVerbDatabases))]
    public void A_rotation_changes_the_key_at_once_here_and_for_another_instance_after_the_refresh_time(string engine)
    {
        var connection = Connect(engine);
        var clock = new Clock();
        var (a, _) = Instance(engine, connection, clock);
        var (b, _) = Instance(engine, connection, clock);
        var before = a.Current();
        Assert.Equal(before, b.Current());
        a.Rotate();
        Assert.NotEqual(before, a.Current());
        Assert.Equal(before, b.Current());                                         // b still holds what it read
        clock.Now += GroupMaskKey.Refresh + TimeSpan.FromSeconds(1);
        Assert.Equal(a.Current(), b.Current());
        Assert.NotEqual(before, b.Current());
        var (restarted, _) = Instance(engine, connection, clock);
        Assert.Equal(a.Current(), restarted.Current());                            // the new key is the one that stays
    }

    [Fact]
    public void The_hash_of_a_value_is_stable_with_the_group_key_and_changes_when_the_key_is_rotated()
    {
        var clock = new Clock();
        var (key, _) = Instance("sqlite", null, clock);
        var one = DbDataKit.Access.ColumnMasks.Hash(key.Current(), "sales", "customers.customer", "Acme");
        Assert.Equal(one, DbDataKit.Access.ColumnMasks.Hash(key.Current(), "sales", "customers.customer", "Acme"));
        key.Rotate();
        Assert.NotEqual(one, DbDataKit.Access.ColumnMasks.Hash(key.Current(), "sales", "customers.customer", "Acme"));
    }

    [Fact]
    public void Rotation_through_the_management_api_needs_the_admin_role_is_audited_and_never_shows_the_value()
    {
        var clock = new Clock();
        var (key, _) = Instance("sqlite", null, clock);
        var options = new GatewayOptions();
        var cache = new GatewayCache(new DataSourceCatalog(new StaticMonitor(options), GatewaySources.Providers()), () => options, () => new SemanticModels());
        var audit = new ListAudit();
        var api = new GatewayAccessManagement(key, cache, audit, clock);
        ManagementCaller Holding(ServerRole roles)
        {
            var client = new PairedClient("console-1", "Console", [], ServerRole.Admin);
            return new ManagementCaller(new ValidatedToken(client, "ada", "Ada", false, roles, new Dictionary<string, ServerRole>(), "token-1", clock.Now, clock.Now.AddMinutes(5)));
        }

        Assert.Equal(403, api.Handle("POST", GatewayAccessManagement.Route + "/rotate", Holding(ServerRole.Publisher)).Status);
        Assert.Equal(403, api.Handle("GET", GatewayAccessManagement.Route, Holding(ServerRole.Viewer)).Status);
        Assert.Equal(405, api.Handle("DELETE", GatewayAccessManagement.Route, Holding(ServerRole.Admin)).Status);
        Assert.Empty(audit.Entries);

        var status = api.Handle("GET", GatewayAccessManagement.Route, Holding(ServerRole.Admin));
        Assert.False((bool)status.Body["exists"]!);
        var before = key.Current();
        var rotated = api.Handle("POST", GatewayAccessManagement.Route + "/rotate", Holding(ServerRole.Admin));
        Assert.Equal(200, rotated.Status);
        Assert.True((bool)rotated.Body["rotated"]!);
        Assert.NotEqual(before, key.Current());
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("access mask-key rotate", entry.Command);
        Assert.Equal("ada", entry.UserId);
        Assert.DoesNotContain(Convert.ToBase64String(key.Current()), rotated.Body.ToJsonString(), StringComparison.Ordinal);
        Assert.True((bool)api.Handle("GET", GatewayAccessManagement.Route, Holding(ServerRole.Admin)).Body["exists"]!);
    }
}
