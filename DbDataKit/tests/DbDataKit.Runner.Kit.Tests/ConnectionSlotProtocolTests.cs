using DbDataKit.Runner.Kit.Tests.Samples;
using DbDataKit.Runner.Protocol.Server;
using DbDataKit.Runner.Protocol.V1;
using Grpc.Core;

namespace DbDataKit.Runner.Kit.Tests;

/// <summary>Plan 28c E02: a task opens a connection slot through the protocol's <c>ResolveConnection</c>, and the kit's <c>Slot&lt;T&gt;.OpenAsync</c> is implemented over it.</summary>
public sealed class ConnectionSlotProtocolTests
{
    private sealed class FakeResolver : ISlotResolver
    {
        public List<(string Slot, string Connection, string Role)> Asked { get; } = [];

        public Func<string, string, string, ConnectionGrant>? Answer { get; set; }

        public IReadOnlyDictionary<string, string> Handles(TaskRef task, IReadOnlyList<ConnectionSlot> slots) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, string>> EnvironmentAsync(IReadOnlyDictionary<string, string> handles, IReadOnlyList<ConnectionSlot> slots, CancellationToken ct) => throw new NotSupportedException();

        public Task<ConnectionGrant> ResolveAsync(string slot, string connection, string role, CancellationToken ct)
        {
            Asked.Add((slot, connection, role));
            return Task.FromResult(Answer!(slot, connection, role));
        }
    }

    private static string Grant(Harness h, ISlotResolver? resolver, IReadOnlyDictionary<string, string> handles, string task = "grant")
    {
        var start = new TaskStart(new TaskRef(Harness.Package, Harness.Channel, task), h.Database.NewId(), 1, "1.0.0", new Dictionary<string, string>(), handles, null, TimeSpan.FromSeconds(30));
        return h.Host.Register(new RunGrant(start, Harness.Chain, h.Environment(), null, resolver));
    }

    private static ConnectionGrant Sample(string slot, string connection, string role) => new()
    {
        Slot = slot,
        Connection = connection,
        Engine = "sqlserver",
        Role = role,
        Host = "db.example",
        Port = 1433,
        Database = "sales",
        AuthMode = "sql",
        User = "svc_run",
        Password = "pw-" + role,
    };

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task A_job_opens_its_slot_through_the_runner_and_gets_the_connection_the_channel_binds(string engine)
    {
        using var h = new Harness(engine, remote: true);
        var resolver = new FakeResolver { Answer = Sample };
        var token = Grant(h, resolver, new Dictionary<string, string> { ["source"] = "sales_db" });
        GrantReader.Last = null;

        var result = await RemoteJobHost.RunAsync<GrantReader>(h.Host.Endpoint, token, CancellationToken.None, new RemoteRunOptions { HeartbeatInterval = TimeSpan.Zero });

        Assert.Equal(TaskEndState.Succeeded, result.End.State);
        Assert.Equal([("source", "sales_db", "default")], resolver.Asked);
        var grant = GrantReader.Last!;
        Assert.Equal(("sales_db", "sqlserver", "db.example", 1433, "sales", "svc_run", "pw-default"), (grant.Connection, grant.Engine, grant.Host, grant.Port, grant.Database, grant.User, grant.Password));
        Assert.DoesNotContain("pw-default", grant.ToString(), StringComparison.Ordinal);
        h.Host.Unregister(token);
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task A_slot_typed_for_something_else_needs_a_factory(string engine)
    {
        using var h = new Harness(engine, remote: true);
        var handles = new Dictionary<string, string> { ["warehouse"] = "sales_db" };

        // without a factory the runner's grant is not a warehouse, and the job fails saying what to do
        var token = Grant(h, new FakeResolver { Answer = Sample }, handles, "orders");
        var result = await RemoteJobHost.RunAsync<Orders>(h.Host.Endpoint, token, CancellationToken.None, new RemoteRunOptions { HeartbeatInterval = TimeSpan.Zero });
        Assert.Equal(TaskEndState.Failed, result.End.State);
        Assert.Contains("SlotFactory", result.End.Failure!.Message, StringComparison.Ordinal);

        // with one, the job gets the handle the factory made from the grant
        var seen = new List<string>();
        var again = Grant(h, new FakeResolver { Answer = Sample }, handles, "orders");
        var options = new RemoteRunOptions { HeartbeatInterval = TimeSpan.Zero, SlotFactory = (grant, type) => { seen.Add($"{type.Name}:{grant.Connection}"); return new MemoryWarehouse(h.World); } };
        var made = await RemoteJobHost.RunAsync<Orders>(h.Host.Endpoint, again, CancellationToken.None, options);
        Assert.Equal(["IWarehouse:sales_db"], seen);
        Assert.Equal(TaskEndState.Succeeded, made.End.State);
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task The_runner_refuses_a_slot_the_run_was_not_given_a_bad_role_and_a_missing_binding(string engine)
    {
        using var h = new Harness(engine, remote: true);
        var resolver = new FakeResolver { Answer = Sample };
        var token = Grant(h, resolver, new Dictionary<string, string> { ["source"] = "sales_db" });
        using var raw = new RawClient(h.Host.Endpoint, token);
        raw.Start();

        var other = await Assert.ThrowsAsync<RpcException>(async () => await raw.Client.ResolveConnectionAsync(new ResolveConnectionRequest { Slot = "target", Role = "default" }, raw.Headers));
        Assert.Equal(StatusCode.PermissionDenied, other.StatusCode);
        Assert.Contains("'target'", other.Status.Detail, StringComparison.Ordinal);
        Assert.Contains("channel 'prod'", other.Status.Detail, StringComparison.Ordinal);
        var role = await Assert.ThrowsAsync<RpcException>(async () => await raw.Client.ResolveConnectionAsync(new ResolveConnectionRequest { Slot = "source", Role = "root" }, raw.Headers));
        Assert.Equal(StatusCode.InvalidArgument, role.StatusCode);
        Assert.Empty(resolver.Asked);                                     // neither reached the resolver

        resolver.Answer = (slot, connection, _) => throw new SlotResolutionException($"The login of '{connection}' bound to '{slot}' is not in the secret store.");
        var missing = await Assert.ThrowsAsync<RpcException>(async () => await raw.Client.ResolveConnectionAsync(new ResolveConnectionRequest { Slot = "source", Role = "write" }, raw.Headers));
        Assert.Equal(StatusCode.FailedPrecondition, missing.StatusCode);
        Assert.Contains("not in the secret store", missing.Status.Detail, StringComparison.Ordinal);
        h.Host.Unregister(token);
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task A_runner_without_bindings_and_an_ended_token_answer_nothing(string engine)
    {
        using var h = new Harness(engine, remote: true);
        var none = Grant(h, null, new Dictionary<string, string> { ["source"] = "sales_db" });
        using (var raw = new RawClient(h.Host.Endpoint, none))
        {
            raw.Start();
            var refused = await Assert.ThrowsAsync<RpcException>(async () => await raw.Client.ResolveConnectionAsync(new ResolveConnectionRequest { Slot = "source" }, raw.Headers));
            Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        }

        var token = Grant(h, new FakeResolver { Answer = Sample }, new Dictionary<string, string> { ["source"] = "sales_db" });
        using var other = new RawClient(h.Host.Endpoint, token);
        other.Start();
        h.Host.Unregister(token);
        var gone = await Assert.ThrowsAsync<RpcException>(async () => await other.Client.ResolveConnectionAsync(new ResolveConnectionRequest { Slot = "source" }, other.Headers));
        Assert.Equal(StatusCode.Unauthenticated, gone.StatusCode);
    }
}
