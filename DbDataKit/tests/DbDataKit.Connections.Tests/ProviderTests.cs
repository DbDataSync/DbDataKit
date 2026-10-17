using System.Data.Common;
using DbDataKit.Dialect;
using DbDataKit.Secrets;

namespace DbDataKit.Connections.Tests;

public class ProviderTests
{
    private sealed class FakeFactory : DbProviderFactory
    {
        public Func<FakeConnection> Make { get; init; } = () => new FakeConnection();
        public override DbConnection CreateConnection() => Make();
    }

    private sealed class ThrowingConnection : DbConnection
    {
        #pragma warning disable CS8765
        public override string ConnectionString { get; set; } = "";
#pragma warning restore CS8765
        public override string Database => "";
        public override string DataSource => "";
        public override string ServerVersion => "";
        public override System.Data.ConnectionState State => System.Data.ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() => throw new InvalidOperationException("Login failed for user 'sa' with password hunter2");
        public override Task OpenAsync(CancellationToken cancellationToken) => throw new TimeoutException("server prod-db-01 did not answer");
        protected override DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }

    private sealed class ThrowingFactory : DbProviderFactory
    {
        public override DbConnection CreateConnection() => new ThrowingConnection();
    }

    private static GenericAdoProvider Provider(DbProviderFactory f, ReadOnlySession? ro = null) =>
        new(new EngineId("fake"), f, new GenericAdoComposer(new EngineId("fake")), ro);

    private static ConnectionContext Ctx(AccessMode access = AccessMode.Readwrite) => new()
    {
        Definition = new ConnectionDefinition { Name = "c", Engine = "fake", Access = access, Address = new AddressSpec { Host = "h" } },
    };

    [Fact]
    public async Task Open_applies_the_read_only_recipe_only_for_read_only_contexts()
    {
        var made = new List<FakeConnection>();
        var ro = new ReadOnlySession("fake", ReadOnlyEnforcement.SessionDefault, ["SET ro = on"]);
        var p = Provider(new FakeFactory { Make = () => { var c = new FakeConnection(); made.Add(c); return c; } }, ro);
        await using (await p.OpenAsync(Ctx())) { }
        await using (await p.OpenAsync(Ctx(AccessMode.Readonly))) { }
        Assert.Empty(made[0].Executed);
        Assert.Equal(["SET ro = on"], made[1].Executed);
    }

    [Fact]
    public async Task Open_failures_become_classified_failures_without_driver_text()
    {
        var p = Provider(new ThrowingFactory());
        var ex = await Assert.ThrowsAsync<ConnectionFailureException>(async () => await p.OpenAsync(Ctx()));
        Assert.Equal(ConnectionFailureKind.Timeout, ex.Failure.Kind);
        Assert.DoesNotContain("prod-db-01", ex.Message, StringComparison.Ordinal);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public async Task Policy_violations_pass_through_unwrapped()
    {
        var p = Provider(new FakeFactory());
        var ctx = new ConnectionContext
        {
            Definition = new ConnectionDefinition { Name = "c", Engine = "fake", Address = new AddressSpec { Host = "h" }, Properties = { ["Password"] = "x" } },
        };
        await Assert.ThrowsAsync<ConnectionPolicyException>(async () => await p.OpenAsync(ctx));
    }

    [Fact]
    public async Task Test_reports_success_and_classified_failure()
    {
        var ok = await Provider(new FakeFactory()).TestAsync(Ctx());
        Assert.True(ok.Success);
        Assert.Equal("1.0", ok.ServerVersion);
        var bad = await Provider(new ThrowingFactory()).TestAsync(Ctx());
        Assert.False(bad.Success);
        Assert.Equal(ConnectionFailureKind.Timeout, bad.Failure!.Kind);
    }

    [Fact]
    public void Dialect_falls_back_from_odbc_names_and_unknown_engines()
    {
        var p = Provider(new FakeFactory());
        Assert.Equal(EngineId.SqlServer, p.Dialect(new ConnectionDefinition { Engine = "fake", Dialect = "sqlserver" }).Id);
        Assert.Equal(EngineId.Postgres, p.Dialect(new ConnectionDefinition { Engine = "postgres:thing" }).Id);
        Assert.Equal(EngineId.Ansi, p.Dialect(new ConnectionDefinition { Engine = "nothing" }).Id);
    }

    [Fact]
    public void Registry_resolves_ids_aliases_and_odbc_names()
    {
        var generic = Provider(new FakeFactory());
        var odbc = new GenericAdoProvider(new EngineId("odbc"), new FakeFactory(), new OdbcComposer());
        var mssql = new GenericAdoProvider(new EngineId("sqlserver"), new FakeFactory(), new GenericAdoComposer(new EngineId("sqlserver")));
        var reg = new ProviderRegistry().Register(generic).Register(odbc).Register(mssql, "ms");
        Assert.Same(generic, reg.Get("FAKE"));
        Assert.Same(odbc, reg.Get("odbc:Some Driver"));
        Assert.Same(mssql, reg.Get("mssql"));
        Assert.Same(mssql, reg.Get("ms"));
        var ex = Assert.Throws<NotSupportedException>(() => reg.Get("oracle"));
        Assert.Contains("fake, ms, odbc, sqlserver", ex.Message, StringComparison.Ordinal);
        Assert.False(reg.TryGet("", out _));
    }
}
