using Microsoft.Extensions.Time.Testing;

namespace DbDataKit.State.Tests;

/// <summary>
/// Plan 20 O02b: what a sign-in holds between two requests (a passkey challenge, an upstream sign-in's verifier) and the management tokens already used are in the state, so every instance that shares it can finish what
/// another started and refuses what another accepted. Two <see cref="OneTimeValues"/> on one state stand for two instances: they share nothing but the database.
/// </summary>
public sealed class ModuleOneTimeValuesTests : IDisposable
{
    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_value_held_by_one_instance_is_taken_once_by_another(string engine)
    {
        var location = _engines.Provision(engine);
        var state = location.Open([IdentityModule.Module]);
        var (a, b) = (new IdentityOneTimeValues(state), new IdentityOneTimeValues(state));

        a.Hold("passkey-assertion", "handle-1", "{\"challenge\":\"abc\"}", DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Null(b.Take("passkey-registration", "handle-1"));   // another purpose's key is not this one
        Assert.Equal("{\"challenge\":\"abc\"}", b.Take("passkey-assertion", "handle-1"));
        Assert.Null(a.Take("passkey-assertion", "handle-1"));      // once
        Assert.Null(b.Take("passkey-assertion", "handle-1"));
        Assert.Null(a.Take("passkey-assertion", "never-held"));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void An_expired_value_is_not_given_and_is_gone(string engine)
    {
        var location = _engines.Provision(engine);
        var state = location.Open([IdentityModule.Module]);
        var start = DateTimeOffset.UtcNow;
        var clock = new FakeTimeProvider(start);
        var values = new IdentityOneTimeValues(location.Open([IdentityModule.Module], time: clock));
        values.Hold("upstream:entra", "state-1", "attempt", start.AddMinutes(10));
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Null(values.Take("upstream:entra", "state-1"));
        // taken by the expired read: not there for a caller whose clock is still before the expiry
        Assert.Null(new IdentityOneTimeValues(location.Open([IdentityModule.Module], time: new FakeTimeProvider(start))).Take("upstream:entra", "state-1"));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Of_instances_that_take_a_value_at_once_one_gets_it(string engine)
    {
        var location = _engines.Provision(engine);
        var state = location.Open([IdentityModule.Module]);
        var instances = Enumerable.Range(0, 8).Select(_ => new IdentityOneTimeValues(state)).ToArray();
        for (var round = 0; round < 5; round++)
        {
            instances[0].Hold("passkey-assertion", $"h{round}", "state", DateTimeOffset.UtcNow.AddMinutes(5));
            var taken = instances.AsParallel().WithDegreeOfParallelism(8).Select(i => i.Take("passkey-assertion", $"h{round}")).Count(v => v is not null);
            Assert.Equal(1, taken);
        }
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_key_claimed_by_one_instance_is_refused_by_another_until_it_is_forgotten(string engine)
    {
        var location = _engines.Provision(engine);
        var state = location.Open([IdentityModule.Module]);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        state = location.Open([IdentityModule.Module], time: clock);
        var (a, b) = (new IdentityOneTimeValues(state), new IdentityOneTimeValues(state));

        Assert.True(a.TryClaim("management:proxy-1", "console-1\njti-1", clock.GetUtcNow().AddMinutes(10)));
        Assert.False(b.TryClaim("management:proxy-1", "console-1\njti-1", clock.GetUtcNow().AddMinutes(10)));
        Assert.False(a.TryClaim("management:proxy-1", "console-1\njti-1", clock.GetUtcNow().AddMinutes(10)));
        Assert.True(b.TryClaim("key-rotation:proxy-1", "console-1\njti-1", clock.GetUtcNow().AddMinutes(10)));   // another purpose

        // at once, on several instances: one wins
        var many = Enumerable.Range(0, 8).Select(_ => new IdentityOneTimeValues(state)).ToArray();
        Assert.Equal(1, many.AsParallel().WithDegreeOfParallelism(8).Count(i => i.TryClaim("management:proxy-1", "console-1\njti-2", clock.GetUtcNow().AddMinutes(10))));

        // once it could no longer be valid, the sweep forgets it
        clock.Advance(TimeSpan.FromMinutes(12));
        Assert.True(new IdentityOneTimeValues(state).TryClaim("management:proxy-1", "console-1\njti-1", clock.GetUtcNow().AddMinutes(10)));
    }
}
