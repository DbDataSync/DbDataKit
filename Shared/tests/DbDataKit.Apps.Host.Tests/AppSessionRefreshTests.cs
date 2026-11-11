using DbDataKit.Identity;
using Microsoft.Extensions.Time.Testing;

namespace DbDataKit.Apps.Tests;

/// <summary>Roles taken away at the identity home stop working here within the refresh interval, not at the end of the session (plan 26, AP05 left-over).</summary>
public sealed class AppSessionRefreshTests
{
    private static HomeIdentity Person(params string[] instances) => new("u1", "Ada", null, [], instances.ToDictionary(i => AppInstanceAudience.Of(i), i => (IReadOnlyList<string>)["viewer"]));

    private sealed class Home
    {
        public HomeIdentity Answer = Person("acme");
        public Exception? Fail;
        public int Asked;
        public List<string> Tokens = [];

        public Task<(HomeIdentity, HomeGrant)> Refresh(string token, CancellationToken ct)
        {
            Asked++;
            Tokens.Add(token);
            if (Fail is not null) throw Fail;
            return Task.FromResult((Answer, new HomeGrant("next-" + Asked, null)));
        }
    }

    private static (AppSessions Sessions, FakeTimeProvider Clock, Home Home) Make()
    {
        var clock = new FakeTimeProvider();
        var home = new Home();
        return (new AppSessions(clock, home.Refresh), clock, home);
    }

    [Fact]
    public async Task Within_the_interval_the_home_is_not_asked()
    {
        var (sessions, clock, home) = Make();
        var id = sessions.Create(Person("acme"), "r0").Id;

        clock.Advance(AppSessions.DefaultRefreshAfter - TimeSpan.FromSeconds(1));
        await sessions.FindAsync(id, default);

        Assert.Equal(0, home.Asked);
    }

    [Fact]
    public async Task After_the_interval_the_roles_are_the_homes_current_word_and_the_refresh_token_moves_on()
    {
        var (sessions, clock, home) = Make();
        var id = sessions.Create(Person("acme", "globex"), "r0").Id;
        home.Answer = Person("acme");

        clock.Advance(AppSessions.DefaultRefreshAfter);
        var session = await sessions.FindAsync(id, default);
        clock.Advance(AppSessions.DefaultRefreshAfter);
        await sessions.FindAsync(id, default);

        Assert.NotEmpty(session!.RolesOn("acme"));
        Assert.Empty(session.RolesOn("globex"));
        Assert.Equal(["r0", "next-1"], home.Tokens);
    }

    [Fact]
    public async Task A_home_that_refuses_ends_the_session()
    {
        var (sessions, clock, home) = Make();
        var id = sessions.Create(Person("acme"), "r0").Id;
        home.Fail = new OpenIdClientException("gone", homeRefused: true);

        clock.Advance(AppSessions.DefaultRefreshAfter);

        Assert.Null(await sessions.FindAsync(id, default));
        Assert.Null(sessions.Find(id));
    }

    [Fact]
    public async Task A_home_that_cannot_be_reached_leaves_the_last_answer_and_is_asked_again_soon_not_at_once()
    {
        var (sessions, clock, home) = Make();
        var id = sessions.Create(Person("acme"), "r0").Id;
        home.Fail = new OpenIdClientException("down");

        clock.Advance(AppSessions.DefaultRefreshAfter);
        var session = await sessions.FindAsync(id, default);
        await sessions.FindAsync(id, default);                       // straight away: not asked again
        clock.Advance(TimeSpan.FromSeconds(31));
        await sessions.FindAsync(id, default);

        Assert.NotEmpty(session!.RolesOn("acme"));
        Assert.Equal(2, home.Asked);
    }

    [Fact]
    public async Task Calls_at_the_same_time_ask_once_and_a_session_without_a_refresh_token_is_never_asked()
    {
        var (sessions, clock, home) = Make();
        var id = sessions.Create(Person("acme"), "r0").Id;
        var plain = sessions.Create(Person("acme")).Id;

        clock.Advance(AppSessions.DefaultRefreshAfter);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => sessions.FindAsync(id, default)));
        await sessions.FindAsync(plain, default);

        Assert.Equal(1, home.Asked);
    }

    [Fact]
    public async Task An_expired_session_is_gone_without_asking()
    {
        var (sessions, clock, home) = Make();
        var id = sessions.Create(Person("acme"), "r0").Id;

        clock.Advance(AppSessions.Lifetime + TimeSpan.FromSeconds(1));

        Assert.Null(await sessions.FindAsync(id, default));
        Assert.Equal(0, home.Asked);
    }
}
