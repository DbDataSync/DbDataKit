namespace DbDataKit.State.Tests;

/// <summary>13a U22: a session's length and idle timeout, on every engine.</summary>
public sealed class ModuleSessionSettingsTests : IDisposable
{
    private readonly StateEngines _engines = new();
    private readonly StateTestClock _clock = new(DateTimeOffset.Parse("2026-11-07T08:00:00Z"));

    public void Dispose() => _engines.Dispose();

    private (IdentitySessionStore Sessions, IdentityUserStore Users, UserRecord User) Open(string engine, SessionSettings? settings)
    {
        var database = _engines.Provision(engine).Open([IdentityModule.Module], time: _clock);
        var users = new IdentityUserStore(database);
        return (new IdentitySessionStore(database, settings), users, users.CreateUser("Ada", null, UserRole.Viewer));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void The_defaults_are_as_they_were_twelve_hours_from_sign_in_and_no_idle_timeout(string engine)
    {
        var (sessions, users, user) = Open(engine, null);
        var session = sessions.Create(user.Id);
        Assert.Equal(_clock.GetUtcNow().AddHours(12), session.ExpiresAtUtc);

        // unused for eleven hours and used just before the end: still good; use does not extend it
        _clock.Advance(TimeSpan.FromHours(11.5));
        Assert.NotNull(sessions.Resolve(session.Id, users));
        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Null(sessions.Resolve(session.Id, users));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_length_is_counted_from_sign_in(string engine)
    {
        var (sessions, users, user) = Open(engine, new SessionSettings(TimeSpan.FromHours(2)));
        var session = sessions.Create(user.Id);
        Assert.Equal(_clock.GetUtcNow().AddHours(2), session.ExpiresAtUtc);

        _clock.Advance(TimeSpan.FromMinutes(110));
        Assert.NotNull(sessions.Resolve(session.Id, users));
        _clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Null(sessions.Resolve(session.Id, users));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void With_an_idle_timeout_an_unused_session_is_refused_and_a_used_one_is_not(string engine)
    {
        var (sessions, users, user) = Open(engine, new SessionSettings(TimeSpan.FromHours(12), TimeSpan.FromMinutes(30)));
        var used = sessions.Create(user.Id);
        var unused = sessions.Create(user.Id);

        // used every twenty minutes for two hours
        for (var i = 0; i < 6; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(20));
            Assert.NotNull(sessions.Resolve(used.Id, users));
        }

        Assert.Null(sessions.Resolve(unused.Id, users));          // idle for two hours
        Assert.NotNull(sessions.Resolve(used.Id, users));

        _clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Null(sessions.Resolve(used.Id, users));             // now it has been idle too
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void An_idle_timeout_does_not_extend_the_length(string engine)
    {
        var (sessions, users, user) = Open(engine, new SessionSettings(TimeSpan.FromHours(1), TimeSpan.FromMinutes(30)));
        var session = sessions.Create(user.Id);
        for (var i = 0; i < 3; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(20));
            sessions.Resolve(session.Id, users);
        }
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(sessions.Resolve(session.Id, users));   // used a minute ago, but an hour has passed since sign-in
    }

    [Fact]
    public void Settings_out_of_range_have_a_problem()
    {
        Assert.Null(SessionSettings.Default.Problem("f"));
        Assert.Contains("session.length_hours", new SessionSettings(TimeSpan.FromHours(25)).Problem("server.yaml"));
        Assert.Contains("session.length_hours", new SessionSettings(TimeSpan.FromMinutes(30)).Problem("server.yaml"));
        Assert.Contains("session.idle_minutes", new SessionSettings(TimeSpan.FromHours(12), TimeSpan.FromMinutes(-1)).Problem("server.yaml"));
    }
}
