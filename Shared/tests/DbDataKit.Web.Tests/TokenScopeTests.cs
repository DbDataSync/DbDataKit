using DbDataKit.Web.Trust;
using Xunit;

namespace DbDataKit.Web.Tests;

/// <summary>Plan 26 section 3: an app's token names the instance and what it may touch; a console's token has no scope and is not limited by it.</summary>
public class TokenScopeTests
{
    private const string Server = "store-1", Host = "apps-host";
    private readonly TestClock _clock = new();
    private readonly ConsoleSigningKey _key = ConsoleSigningKey.Generate();

    private (ConsoleTokenIssuer Issuer, ConsoleTokenValidator Validator) Make() =>
        (new ConsoleTokenIssuer(Host, _key, _clock),
         new ConsoleTokenValidator(Server, new Consoles(new PairedClient(Host, "Apps", _key.PublicKey, ServerRole.Viewer | ServerRole.Author, ClientKind.Server)), new MemoryReplayCache(_clock), _clock));

    [Fact]
    public void The_app_and_the_scope_come_back_from_a_validated_token()
    {
        var (issuer, validator) = Make();
        var result = validator.Validate(issuer.Issue(new ConsoleTokenRequest(Server, "user-7", "Ada", ServerRole.Author, App: "acme", Scope: [TokenScope.Entry("territories", "territory", write: true), TokenScope.Entry("territories", "region", write: false)])));

        Assert.True(result.Ok, result.Detail);
        Assert.Equal("acme", result.Token!.App);
        Assert.Equal(["territories/territory:write", "territories/region:read"], result.Token.Scope);
    }

    [Fact]
    public void A_scope_allows_what_it_names_write_includes_read_and_nothing_else()
    {
        var (issuer, validator) = Make();
        var token = validator.Validate(issuer.Issue(new ConsoleTokenRequest(Server, "u", null, ServerRole.Viewer, App: "acme", Scope: ["m/a:write", "m/b:read"]))).Token!;

        Assert.True(TokenScope.Allows(token, "m", "a", write: true));
        Assert.True(TokenScope.Allows(token, "m", "a", write: false));
        Assert.True(TokenScope.Allows(token, "m", "b", write: false));
        Assert.False(TokenScope.Allows(token, "m", "b", write: true));
        Assert.False(TokenScope.Allows(token, "m", "c", write: false));
        Assert.False(TokenScope.Allows(token, "other", "a", write: false));
    }

    [Fact]
    public void An_empty_scope_allows_nothing_and_no_scope_is_not_limited()
    {
        var (issuer, validator) = Make();
        var empty = validator.Validate(issuer.Issue(new ConsoleTokenRequest(Server, "u", null, ServerRole.Viewer, App: "acme", Scope: []))).Token!;
        Assert.NotNull(empty.Scope);
        Assert.False(TokenScope.Allows(empty, "m", "a", write: false));

        var console = validator.Validate(issuer.Issue(new ConsoleTokenRequest(Server, "u", null, ServerRole.Viewer))).Token!;
        Assert.Null(console.Scope);
        Assert.True(TokenScope.Allows(console, "m", "a", write: true));
    }
}
