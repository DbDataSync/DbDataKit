using DbDataKit.Identity;
using DbDataKit.State;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Data;
using DbDataProxy.Server.Serving;
using DbDataProxy.Tests.Support;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace DbDataProxy.Tests.Auth;

/// <summary>
/// Proxy's data clients sign in with the identity home's identities (plan 13a P13; plan 16 "Sign-in decisions" item 2): a home user name with a personal access token, a home token, and Windows resolved by UPN; which data sources a home user
/// may use is decided by the home groups the data source files name. Over a real state database (SQLite in a folder of its own), with the validators, the principals and the catalog the server runs on.
/// </summary>
public sealed class HomeIdentityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dbproxy-home-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ProxyOptions _options;
    private readonly StaticMonitor _monitor;
    private readonly StateDatabase _state;
    private readonly HomeCallers _callers;

    public HomeIdentityTests()
    {
        Directory.CreateDirectory(_directory);
        _options = new ProxyOptions
        {
            DataDirectory = _directory,
            DataSources =
            {
                ["erp"] = Duck(groups: ["Finance"], isDefault: true),
                ["lake"] = Duck(groups: ["analysts", "finance"]),
                ["open"] = Duck(everyone: true),
                ["closed"] = Duck(),
                ["pt"] = new DataSourceOptions { Provider = "sqlserver", PassThrough = true, ConnectionString = "Server=s;Database=d" },
            },
        };
        _monitor = new StaticMonitor(_options);
        _state = DbDataProxy.Server.State.ProxyState.Open(_directory);
        _callers = new HomeCallers(_monitor, _state);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        DbDataProxy.Tests.Config.TestFolder.Delete(_directory);
    }

    private static DataSourceOptions Duck(string[]? groups = null, bool everyone = false, bool isDefault = false) =>
        new() { Provider = "duckdb", ConnectionString = "Data Source=:memory:", Groups = [.. groups ?? []], Everyone = everyone, Default = isDefault };

    private HomeCredentialValidator Validator() => new(_callers, _monitor);

    private DataSourceCatalog Catalog() => new(_monitor, TestSources.Registry());

    // ---- a home user name with a personal access token ----

    [Fact]
    public async Task A_person_signs_in_with_their_address_and_a_personal_access_token_and_is_known_by_the_home_name_and_groups()
    {
        var (ada, token) = HomeSeed.Person(_state, "Ada Lovelace", "ada@example.invalid", "finance");

        var result = await Validator().ValidateAsync("ada@example.invalid", token, default);

        var principal = result.Principal!;
        Assert.True(result.Definitive);
        Assert.Equal(("Ada Lovelace", "home-user", ada.Id), (principal.Name, principal.Method, principal.HomeUserId));
        Assert.Equal(["finance"], principal.HomeGroups);
        Assert.Null(principal.Credentials);
        // what the person may use is what the files name: the group's sources and the open one, not the closed one, not the other group's, not pass-through
        Assert.Equal(["erp", "lake", "open"], principal.DataSources.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_service_account_signs_in_with_its_name_and_its_token_and_the_user_id_works_too()
    {
        var token = HomeSeed.ServiceAccount(_state, "nightly-load", "analysts");

        var principal = (await Validator().ValidateAsync("nightly-load", token, default)).Principal!;

        Assert.Equal("nightly-load", principal.Name);
        Assert.Equal(["lake", "open"], principal.DataSources.Order(StringComparer.Ordinal));
        Assert.NotNull((await Validator().ValidateAsync(principal.HomeUserId!, token, default)).Principal);
    }

    [Fact]
    public async Task A_token_that_is_wrong_for_that_user_is_refused_for_good_and_a_password_that_is_no_token_is_left_to_the_next_validator()
    {
        var (_, adaToken) = HomeSeed.Person(_state, "Ada Lovelace", "ada@example.invalid");
        HomeSeed.Person(_state, "Bob Babbage", "bob@example.invalid");

        Assert.Equal(CredentialResult.Rejected, await Validator().ValidateAsync("bob@example.invalid", adaToken, default));       // another person's token
        Assert.Equal(CredentialResult.Rejected, await Validator().ValidateAsync("ada@example.invalid", "ddk_nothing_here", default)); // the shape of a token, not one
        Assert.Equal(CredentialResult.Rejected, await Validator().ValidateAsync("stranger@example.invalid", adaToken, default));
        Assert.Equal(CredentialResult.NotMine, await Validator().ValidateAsync("ada@example.invalid", "a-database-password", default));
    }

    [Fact]
    public async Task A_revoked_token_and_a_disabled_person_are_refused()
    {
        var users = new IdentityUserStore(_state);
        var tokens = new AccessTokens(_state, users);
        var (ada, token) = HomeSeed.Person(_state, "Ada Lovelace", "ada@example.invalid");
        Assert.NotNull((await Validator().ValidateAsync("ada@example.invalid", token, default)).Principal);

        users.SetEnabled(ada.Id, false);
        Assert.Equal(CredentialResult.Rejected, await Validator().ValidateAsync("ada@example.invalid", token, default));
        users.SetEnabled(ada.Id, true);
        Assert.True(tokens.Revoke(tokens.List(ada.Id).Single().Id));
        Assert.Equal(CredentialResult.Rejected, await Validator().ValidateAsync("ada@example.invalid", token, default));
    }

    [Fact]
    public async Task The_chain_asks_the_home_first_a_wrong_token_never_falls_through_to_pass_through_and_database_credentials_still_do()
    {
        var (_, token) = HomeSeed.Person(_state, "Ada Lovelace", "ada@example.invalid", "finance");
        var chain = new ChainedCredentialValidator([Validator(), new PassThroughValidator(_monitor)]);

        Assert.Equal("home-user", (await chain.ValidateAsync("ada@example.invalid", token, default))!.Method);
        Assert.Null(await chain.ValidateAsync("ada@example.invalid", "ddk_wrong_token", default));
        var passThrough = (await chain.ValidateAsync("dbuser", "dbpass", default))!;
        Assert.Equal("passthrough", passThrough.Method);                       // unchanged by P13
        Assert.Equal(new DbCredentials("dbuser", "dbpass"), passThrough.Credentials);
        Assert.Equal(["pt"], passThrough.DataSources);
        Assert.Null(passThrough.HomeUserId);
    }

    // ---- a home token ----

    [Fact]
    public async Task A_personal_access_token_alone_is_a_bearer_token_and_a_foreign_one_is_nobody()
    {
        var (ada, token) = HomeSeed.Person(_state, "Ada Lovelace", "ada@example.invalid", "analysts");
        var home = await _callers.GetAsync();

        var person = home.IdentifyBearer(token)!;
        Assert.Equal(ada.Id, person.Id);
        Assert.Equal("home-token", HomePrincipals.Create(home, person, "home-token", _options).Method);
        Assert.Null(home.IdentifyBearer("ddk_nope_nope"));
        Assert.Null(home.IdentifyBearer("not.a.jwt"));   // without the home's signing key on this instance only personal access tokens are known
    }

    // ---- which data sources a home user may use ----

    [Fact]
    public async Task A_person_not_in_a_named_group_is_refused_and_the_refusal_names_the_data_source()
    {
        var (_, token) = HomeSeed.Person(_state, "Ada Lovelace", "ada@example.invalid", "analysts");
        var ada = (await Validator().ValidateAsync("ada@example.invalid", token, default)).Principal!;

        var refused = Assert.Throws<RpcException>(() => Catalog().Resolve(ada, "erp"));    // erp is for finance
        Assert.Equal(StatusCode.PermissionDenied, refused.StatusCode);
        Assert.Contains("'erp'", refused.Status.Detail);
        Assert.Contains("Ada Lovelace", refused.Status.Detail);
        Assert.Contains("Finance", refused.Status.Detail);

        Assert.Equal("lake", Catalog().Resolve(ada, "lake").Name);      // analysts
        Assert.Equal("open", Catalog().Resolve(ada, "open").Name);      // everyone
    }

    [Fact]
    public async Task A_data_source_that_names_no_group_and_is_not_open_is_used_by_no_home_user_and_the_refusal_says_how_to_open_it()
    {
        var (_, token) = HomeSeed.Person(_state, "Ada Lovelace", "ada@example.invalid", "finance", "analysts");
        var ada = (await Validator().ValidateAsync("ada@example.invalid", token, default)).Principal!;

        var refused = Assert.Throws<RpcException>(() => Catalog().Resolve(ada, "closed"));
        Assert.Equal(StatusCode.PermissionDenied, refused.StatusCode);
        Assert.Contains("'closed'", refused.Status.Detail);
        Assert.Contains("no home group", refused.Status.Detail);
        Assert.Contains("extensions.proxy.groups", refused.Status.Detail);
    }

    [Fact]
    public async Task Group_names_match_whatever_the_case_and_the_default_is_the_one_the_person_may_use()
    {
        var (_, token) = HomeSeed.Person(_state, "Ada Lovelace", "ada@example.invalid", "FINANCE");    // the file says Finance
        var ada = (await Validator().ValidateAsync("ada@example.invalid", token, default)).Principal!;

        Assert.Equal("erp", Catalog().Resolve(ada, null).Name);     // the default source, named by none

        var (_, other) = HomeSeed.Person(_state, "Bob Babbage", "bob@example.invalid", "analysts");
        var bob = (await Validator().ValidateAsync("bob@example.invalid", other, default)).Principal!;
        var ambiguous = Assert.Throws<RpcException>(() => Catalog().Resolve(bob, null));     // erp is not his; lake and open are: he must say which
        Assert.Equal(StatusCode.InvalidArgument, ambiguous.StatusCode);
        Assert.Contains("lake", ambiguous.Status.Detail);
        Assert.DoesNotContain("erp", ambiguous.Status.Detail);
    }

    [Fact]
    public async Task The_groups_are_checked_again_against_the_configuration_in_force_when_a_session_uses_a_source()
    {
        var (_, token) = HomeSeed.Person(_state, "Ada Lovelace", "ada@example.invalid", "finance");
        var ada = (await Validator().ValidateAsync("ada@example.invalid", token, default)).Principal!;
        Assert.Equal("erp", Catalog().Resolve(ada, "erp").Name);

        _options.DataSources["erp"].Groups = ["auditors"];       // the file changed while the session lives
        Assert.Equal(StatusCode.PermissionDenied, Assert.Throws<RpcException>(() => Catalog().Resolve(ada, "erp")).StatusCode);
    }

    [Fact]
    public void A_pass_through_caller_is_unchanged_it_may_use_the_pass_through_sources_and_no_group_is_asked()
    {
        var principal = new ProxyPrincipal("dbuser", "passthrough", ["pt"], "pt", new DbCredentials("dbuser", "s3cret"));

        Assert.Equal("pt", Catalog().Resolve(principal, null).Name);
        Assert.Equal(StatusCode.PermissionDenied, Assert.Throws<RpcException>(() => Catalog().Resolve(principal, "open")).StatusCode);
    }

    // ---- Windows, resolved by UPN ----
    // The operating system's own check (NativeWindowsLogon, LogonUser) runs only on Windows and is covered by the Windows CI job; here a fake stands in for it.

    private sealed class FakeLogon(string expectedUpn, string expectedPassword, bool available = true) : IWindowsLogon
    {
        public (string?, string, string)? Last;
        public bool IsAvailable => available;
        public bool Validate(string? domain, string user, string password)
        {
            Last = (domain, user, password);
            return domain is null && string.Equals(user, expectedUpn, StringComparison.OrdinalIgnoreCase) && password == expectedPassword;
        }
    }

    private WindowsAccountValidator Windows(FakeLogon logon, bool enabled = true)
    {
        _options.Auth.Windows = enabled;
        return new WindowsAccountValidator(_monitor, logon, _callers, NullLogger<WindowsAccountValidator>.Instance);
    }

    [Fact]
    public async Task A_windows_sign_in_with_the_upn_is_the_home_account_that_holds_that_verified_address()
    {
        var (ada, _) = HomeSeed.Person(_state, "Ada Lovelace", "ada@corp.example", "finance");
        var logon = new FakeLogon("ada@corp.example", "windows-password");

        var result = await Windows(logon).ValidateAsync("ADA@corp.example", "windows-password", default);

        var principal = result.Principal!;
        Assert.Equal(("Ada Lovelace", "windows", ada.Id), (principal.Name, principal.Method, principal.HomeUserId));
        Assert.Equal(["erp", "lake", "open"], principal.DataSources.Order(StringComparer.Ordinal));
        Assert.Equal((null, "ADA@corp.example", "windows-password"), logon.Last);    // the UPN names its own domain
    }

    [Fact]
    public async Task A_wrong_windows_password_for_a_home_account_is_refused_for_good_and_so_is_a_platform_that_cannot_check()
    {
        HomeSeed.Person(_state, "Ada Lovelace", "ada@corp.example");

        Assert.Equal(CredentialResult.Rejected, await Windows(new FakeLogon("ada@corp.example", "right")).ValidateAsync("ada@corp.example", "wrong", default));
        var unavailable = new FakeLogon("ada@corp.example", "right", available: false);
        Assert.Equal(CredentialResult.Rejected, await Windows(unavailable).ValidateAsync("ada@corp.example", "right", default));
        Assert.Null(unavailable.Last);
    }

    [Fact]
    public async Task What_is_not_a_home_accounts_upn_is_left_alone_and_the_operating_system_is_never_asked()
    {
        HomeSeed.Person(_state, "Ada Lovelace", "ada@corp.example");
        var logon = new FakeLogon("ada@corp.example", "pw");
        var validator = Windows(logon);

        Assert.Equal(CredentialResult.NotMine, await validator.ValidateAsync("nobody@corp.example", "pw", default));    // no home user holds it: a database login for pass-through, maybe
        Assert.Equal(CredentialResult.NotMine, await validator.ValidateAsync(@"CORP\ada", "pw", default));              // no address to look up
        Assert.Equal(CredentialResult.NotMine, await validator.ValidateAsync("ada", "pw", default));
        Assert.Equal(CredentialResult.NotMine, await validator.ValidateAsync("ada@corp.example", "ddk_a_token", default));   // a token is the home validator's
        Assert.Equal(CredentialResult.NotMine, await Windows(logon, enabled: false).ValidateAsync("ada@corp.example", "pw", default));
        Assert.Null(logon.Last);
    }

    [Fact]
    public async Task An_address_nobody_verified_finds_nobody_for_windows()
    {
        // an address typed in without verification (an invitation not yet redeemed, say) is no key to an account
        new IdentityUserStore(_state).CreateUser("Ada Lovelace", "ada@corp.example", UserRole.Viewer, emailVerified: false);
        var logon = new FakeLogon("ada@corp.example", "pw");

        Assert.Equal(CredentialResult.NotMine, await Windows(logon).ValidateAsync("ada@corp.example", "pw", default));
        Assert.Null(logon.Last);
    }

    [Theory]
    [InlineData("ada@corp.example", true)]
    [InlineData("a@b", true)]
    [InlineData(@"CORP\ada", false)]
    [InlineData("ada", false)]
    [InlineData("@corp.example", false)]
    [InlineData("ada@", false)]
    [InlineData("a@b@c", false)]
    public void Only_a_upn_is_resolved(string name, bool upn) => Assert.Equal(upn, WindowsAccountValidator.IsUpn(name));

    // ---- the session keeps who the person is ----

    [Fact]
    public async Task A_session_keeps_the_home_user_and_groups_and_a_listing_names_the_home_user()
    {
        var (ada, token) = HomeSeed.Person(_state, "Ada Lovelace", "ada@example.invalid", "finance");
        var principal = (await Validator().ValidateAsync("ada@example.invalid", token, default)).Principal!;
        var sessions = new DbDataProxy.Server.Auth.SessionStore(_monitor, new FakeTimeProvider(), _state, new DbDataProxy.Server.State.GroupSessionKey(_state, _monitor));

        var id = sessions.Issue(principal);
        var back = sessions.TryGet(id)!;

        Assert.Equal((ada.Id, "Ada Lovelace", "home-user"), (back.HomeUserId, back.Name, back.Method));
        Assert.Equal(["finance"], back.HomeGroups);
        Assert.Contains(sessions.List(), s => s.User == "Ada Lovelace" && s.Method == "home-user");
        Assert.Equal(ada.Id, new ProxyCaller(back).Id);
    }

    private sealed class StaticMonitor(ProxyOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<ProxyOptions>
    {
        public ProxyOptions CurrentValue => value;
        public ProxyOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<ProxyOptions, string?> listener) => null;
    }
}
