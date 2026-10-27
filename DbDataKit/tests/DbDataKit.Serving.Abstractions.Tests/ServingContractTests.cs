using Apache.Arrow;

namespace DbDataKit.Serving.Tests;

/// <summary>The core services of plan 20 section 10 over the fake host (<see cref="HostServices"/>), and a fake module that uses only them.</summary>
public class ServingContractTests
{
    private readonly HostServices _services = new(new FakeServingHost());

    private async Task<ServingContext> As(string user) =>
        new((await _services.Identity.AuthenticateAsync(new ServingCredentials.Password(user, $"{user}-pw")))!);

    [Fact]
    public async Task A_sql_request_answers_with_rows_and_where_they_came_from()
    {
        var context = await As("admin");
        await using var answer = await _services.Queries.RunAsync(context, new ServingRequest.Sql("main", "public.items"));
        var rows = 0;
        await foreach (var batch in answer.ReadBatchesAsync()) rows += batch.Length;

        Assert.Equal(5, rows);
        Assert.Equal(new ServingProvenance("public.items", null, "prod"), answer.Provenance);
    }

    [Fact]
    public async Task A_request_kind_the_host_cannot_run_is_refused_naming_the_kind()
    {
        var context = await As("admin");
        var ex = await Assert.ThrowsAsync<ServingInvalidException>(() => _services.Queries.RunAsync(context, new ServingRequest.Mdx("sales", "SELECT")));
        Assert.Contains("mdx", ex.Message);
    }

    [Fact]
    public async Task The_catalog_lists_only_what_the_caller_may_see_and_narrows_by_path()
    {
        var reader = await _services.Catalog.DescribeAsync(await As("reader"), []).Select(o => string.Join('.', o.Path)).ToListAsync();
        var admin = await _services.Catalog.DescribeAsync(await As("admin"), []).Select(o => string.Join('.', o.Path)).ToListAsync();
        var under = await _services.Catalog.DescribeAsync(await As("admin"), ["secret"]).Select(o => o.Name).ToListAsync();

        Assert.Equal(["public.items"], reader);
        Assert.Equal(["public.items", "secret.salaries"], admin);
        Assert.Equal(["salaries"], under);
    }

    [Fact]
    public async Task A_session_keeps_the_adapters_state_for_its_caller_only()
    {
        var admin = (await As("admin")).Caller;
        var reader = (await As("reader")).Caller;
        var session = await _services.Sessions.OpenAsync(admin, "flightsql", TimeSpan.FromMinutes(5));
        await _services.Sessions.SaveStateAsync(admin, session.Id, [1, 2, 3], TimeSpan.FromMinutes(5));

        Assert.Equal([1, 2, 3], (await _services.Sessions.GetAsync(admin, session.Id))!.State);
        Assert.Null(await _services.Sessions.GetAsync(reader, session.Id));

        await _services.Sessions.CloseAsync(admin, session.Id);
        Assert.Null(await _services.Sessions.GetAsync(admin, session.Id));
    }

    [Fact]
    public async Task An_expired_session_is_gone()
    {
        var admin = (await As("admin")).Caller;
        var session = await _services.Sessions.OpenAsync(admin, "pgwire", TimeSpan.FromMilliseconds(-1));
        Assert.Null(await _services.Sessions.GetAsync(admin, session.Id));
    }

    [Fact]
    public async Task A_module_starts_with_the_services_reports_health_and_drains()
    {
        var module = new EchoModule();
        await module.StartAsync(_services);
        Assert.Equal(ServingHealth.Healthy, module.Health());
        Assert.Equal(5, await module.CountAsync(await As("admin")));

        await module.DrainAsync();
        Assert.Equal(ServingHealthState.Degraded, module.Health().State);
        var admin = await As("admin");
        await Assert.ThrowsAsync<ServingInvalidException>(() => module.CountAsync(admin));

        await module.StopAsync();
        Assert.Equal(ServingHealthState.Down, module.Health().State);
    }

    /// <summary>A module that only uses core services: no host, no table, no limit of its own.</summary>
    private sealed class EchoModule : IServingModule
    {
        private IServingServices? _services;
        private bool _draining;

        public string Name => "echo";

        public Task StartAsync(IServingServices services, CancellationToken cancellationToken = default) { _services = services; return Task.CompletedTask; }

        public Task DrainAsync(CancellationToken cancellationToken = default) { _draining = true; return Task.CompletedTask; }

        public Task StopAsync(CancellationToken cancellationToken = default) { _services = null; return Task.CompletedTask; }

        public ServingHealth Health() => _services is null ? new(ServingHealthState.Down) : _draining ? new(ServingHealthState.Degraded, "draining") : ServingHealth.Healthy;

        public async Task<int> CountAsync(ServingContext context)
        {
            if (_draining) throw new ServingInvalidException("The module is draining.");
            await using var answer = await _services!.Queries.RunAsync(context, new ServingRequest.Sql("main", "public.items"));
            var rows = 0;
            await foreach (var batch in answer.ReadBatchesAsync()) rows += batch.Length;
            return rows;
        }
    }
}
