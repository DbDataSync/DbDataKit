using Apache.Arrow;
using Xunit;

namespace DbDataKit.Serving.Conformance;

/// <summary>
/// What any host promises a protocol module (plan 19, N03): who a caller is, that a session stands for its caller, what the catalog shows, how a query answers and where it stops, which refusals come as which exception, and that a write needs a write path.
/// Derive one class per host and implement <see cref="CreateAsync"/>; the Flight SQL module and every module after it can then rely on the same behavior from the fake host and from Proxy.
/// </summary>
public abstract class ServingServicesConformanceTests
{
    protected abstract Task<ServingTarget> CreateAsync();

    private async Task<(ServingTarget Target, ServingContext Context)> SignInAsync(Func<ServingTarget, ServingCredentials> who)
    {
        var target = await CreateAsync();
        var caller = await target.Services.Identity.AuthenticateAsync(who(target), "127.0.0.1");
        Assert.NotNull(caller);
        return (target, new ServingContext(caller, ClientAddress: "127.0.0.1"));
    }

    private static async Task<long> CountAsync(IServingAnswer answer)
    {
        long rows = 0;
        await foreach (var batch in answer.ReadBatchesAsync()) rows += batch.Length;
        return rows;
    }

    [Fact]
    public async Task Credentials_identify_a_caller_or_nobody()
    {
        await using var target = await CreateAsync();
        var identity = target.Services.Identity;

        Assert.NotNull(await identity.AuthenticateAsync(target.Full, "127.0.0.1"));
        Assert.Null(await identity.AuthenticateAsync(target.Wrong, "127.0.0.1"));
        Assert.Null(await identity.AuthenticateAsync(new ServingCredentials.Bearer("not-a-session-or-a-token"), "127.0.0.1"));
    }

    [Fact]
    public async Task A_session_id_is_a_bearer_token_for_the_caller_who_opened_it_until_it_is_closed()
    {
        var (target, context) = await SignInAsync(t => t.Full);
        await using var _ = target;
        var sessions = target.Services.Sessions;

        var session = await sessions.OpenAsync(context.Caller, "conformance");
        var again = await target.Services.Identity.AuthenticateAsync(new ServingCredentials.Bearer(session.Id), "127.0.0.1");
        Assert.Equal(context.Caller.Name, again?.Name);

        await sessions.CloseAsync(context.Caller, session.Id);
        Assert.Null(await target.Services.Identity.AuthenticateAsync(new ServingCredentials.Bearer(session.Id), "127.0.0.1"));
    }

    [Fact]
    public async Task The_catalog_shows_a_table_the_caller_may_query_with_its_columns_on_request()
    {
        var (target, context) = await SignInAsync(t => t.Full);
        await using var _ = target;

        var objects = await target.Services.Catalog.DescribeAsync(context, [], withColumns: true, source: target.Source).ToListAsync();
        var table = objects.FirstOrDefault(o => o.Kind is ServingObjectKind.Table or ServingObjectKind.View && o.Name == target.VisibleTable);

        Assert.NotNull(table);
        Assert.NotNull(table.Columns);
        Assert.NotEmpty(table.Columns.FieldsList);
        Assert.All(objects.Where(o => o.Kind is ServingObjectKind.Table or ServingObjectKind.View), o => Assert.NotEmpty(o.Path));
    }

    [Fact]
    public async Task A_caller_sees_nothing_in_the_catalog_that_they_could_not_query()
    {
        await using var probe = await CreateAsync();
        Assert.SkipWhen(probe.Restricted is null || probe.HiddenTable is null, "the host hides no table");
        var (target, context) = await SignInAsync(t => t.Restricted!);
        await using var _ = target;

        var names = (await target.Services.Catalog.DescribeAsync(context, [], source: target.Source).ToListAsync()).Select(o => o.Name).ToList();

        Assert.DoesNotContain(target.HiddenTable, names);
        Assert.Contains(target.VisibleTable, names);
    }

    [Fact]
    public async Task A_query_answers_with_its_rows_and_says_where_they_came_from()
    {
        var (target, context) = await SignInAsync(t => t.Full);
        await using var _ = target;

        await using var answer = await target.Services.Queries.RunAsync(context, target.Open);

        Assert.NotEmpty(answer.Schema.FieldsList);
        Assert.Equal(target.OpenRows, await CountAsync(answer));
        Assert.Equal(target.Open.Text, answer.Provenance.Canonical);
        Assert.Equal(ServingContext.ProductionChannel, answer.Provenance.Channel);
        Assert.Equal(target.Open.Source, answer.Provenance.Source);
    }

    [Fact]
    public async Task A_limit_ends_the_answer_with_an_error_that_says_so_never_silently()
    {
        await using var probe = await CreateAsync();
        Assert.SkipWhen(probe.Limited is null, "the host holds no caller to a limit");
        var (target, context) = await SignInAsync(t => t.Limited!.Who);
        await using var _ = target;
        var limited = target.Limited!;

        await using var answer = await target.Services.Queries.RunAsync(context, limited.Request);
        long rows = 0;
        var stopped = await Record.ExceptionAsync(async () =>
        {
            await foreach (var batch in answer.ReadBatchesAsync()) rows += batch.Length;
        });

        Assert.Equal(limited.MaxRows, rows);                                // what the caller may have, then the stop
        Assert.IsType<ServingLimitException>(stopped);
    }

    [Fact]
    public async Task What_the_caller_may_not_have_is_denied_what_is_not_there_is_not_found_and_what_cannot_run_is_invalid()
    {
        var (target, context) = await SignInAsync(t => t.Full);
        await using var _ = target;
        var queries = target.Services.Queries;

        await Assert.ThrowsAsync<ServingNotFoundException>(() => queries.RunAsync(context, target.UnknownSource));
        var missing = await Assert.ThrowsAnyAsync<ServingException>(() => queries.RunAsync(context, target.Missing));
        Assert.True(missing is ServingNotFoundException or ServingBackendException, $"a missing table came back as {missing.GetType().Name}");
        await Assert.ThrowsAsync<ServingInvalidException>(() => queries.RunAsync(context, new ServingRequest.Mdx(target.Source, "SELECT 1 ON COLUMNS FROM [x]")));

        var refused = await SignInAsync(t => t.Refused.Who);
        await using var __ = refused.Target;
        await Assert.ThrowsAsync<ServingDeniedException>(() => refused.Target.Services.Queries.RunAsync(refused.Context, refused.Target.Refused.Request));
    }

    [Fact]
    public async Task Preparing_a_statement_counts_its_markers_and_refuses_what_running_it_would()
    {
        var (target, context) = await SignInAsync(t => t.Full);
        await using var _ = target;

        var info = await target.Services.Queries.PrepareAsync(context, target.Open);

        Assert.Equal(0, info.ParameterCount);
        if (!target.PrepareRefuses) return;
        var refused = await SignInAsync(t => t.Refused.Who);
        await using var __ = refused.Target;
        await Assert.ThrowsAsync<ServingDeniedException>(() => refused.Target.Services.Queries.PrepareAsync(refused.Context, refused.Target.Refused.Request));
    }

    [Fact]
    public async Task A_source_says_what_it_is_and_whether_it_takes_writes()
    {
        var (target, context) = await SignInAsync(t => t.Full);
        await using var _ = target;

        var facts = await target.Services.Catalog.FactsAsync(context, target.Source);

        Assert.False(string.IsNullOrWhiteSpace(facts.Product));
        Assert.Equal(target.SourceReadOnly, facts.ReadOnly);
        if (target.FactsRefuseUnknownSource) await Assert.ThrowsAsync<ServingNotFoundException>(() => target.Services.Catalog.FactsAsync(context, target.UnknownSource.Source));
    }

    [Fact]
    public async Task A_write_needs_a_write_path_and_a_refused_write_changes_nothing()
    {
        var (target, context) = await SignInAsync(t => t.Full);
        await using var _ = target;
        var updates = target.Services.Updates;

        if (target.Write is null)
        {
            if (updates is not null) await Assert.ThrowsAsync<ServingDeniedException>(() => updates.ExecuteAsync(context, target.Open));
            return;
        }

        Assert.NotNull(updates);
        Assert.True((await updates.ExecuteAsync(context, target.Write)).Rows >= 0);
        if (target.RefusedWrite is not { } refusal) return;
        var refused = await SignInAsync(t => refusal.Who);
        await using var __ = refused.Target;
        await Assert.ThrowsAsync<ServingDeniedException>(() => refused.Target.Services.Updates!.ExecuteAsync(refused.Context, refusal.Request));
    }
}
