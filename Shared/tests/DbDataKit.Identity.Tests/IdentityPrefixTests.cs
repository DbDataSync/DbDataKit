using DbDataKit.State;
using DbDataKit.Web.Auth;

namespace DbDataKit.Identity.Tests;

/// <summary>A home reached under a path (<c>https://gateway:7820/identity</c>) and one with an address of its own (<c>https://localhost:7819</c>): the issuer, the endpoints discovery lists and the pages' addresses.</summary>
public sealed class IdentityPrefixTests
{
    private static IdentityHomeOptions Options(string url) =>
        new(new Uri(url), Path.GetTempPath(), _ => throw new InvalidOperationException(), _ => throw new InvalidOperationException(), new AuthProduct("Test", "test"));

    [Fact]
    public void A_home_under_a_path_has_the_address_and_the_path_as_its_issuer_and_the_endpoints_under_it()
    {
        var o = Options("https://gateway:7820/identity/");
        Assert.Equal("/identity", o.Prefix);
        Assert.Equal("https://gateway:7820/identity", o.Issuer);
        Assert.Equal("https://gateway:7820/identity", o.EndpointBase);
        Assert.Equal("/identity/sign-in", o.SignInUrl);

        var moved = Options("https://gateway:7820/accounts/");
        Assert.Equal("https://gateway:7820/accounts", moved.Issuer);
        Assert.Equal("/accounts", moved.ProviderPrefix);
        Assert.Equal("/accounts/sign-in", moved.SignInUrl);
    }

    [Fact]
    public void A_home_with_an_address_of_its_own_keeps_its_issuer_and_its_identity_endpoints()
    {
        var o = Options("https://localhost:7819/");
        Assert.Equal("", o.Prefix);
        Assert.Equal("https://localhost:7819", o.Issuer);
        Assert.Equal("https://localhost:7819/identity", o.EndpointBase);
        Assert.Equal("/identity/sign-in", o.SignInUrl);
    }

    [Fact]
    public void The_pages_call_the_sign_in_api_and_the_invitations_under_the_homes_path()
    {
        var under = Options("https://gateway:7820/accounts/");
        var invite = IdentityHomePages.Invite(under);
        Assert.Contains("const PREFIX = \"/accounts\";", invite);
        Assert.DoesNotContain("@PREFIX@", invite);
        var signIn = IdentityHomePages.SignIn(under);
        Assert.Contains("const PREFIX = \"/accounts\", PROVIDER = \"/accounts\";", signIn);

        var own = IdentityHomePages.SignIn(Options("https://localhost:7819/"));
        Assert.Contains("const PREFIX = \"\", PROVIDER = \"/identity\";", own);
    }

    [Fact]
    public void The_keys_of_a_home_are_asked_for_under_its_path()
    {
        var http = new HttpClient();
        Assert.Equal("https://gateway:7820/identity/jwks", new JwksTokenVerifier("https://gateway:7820/identity/", http).JwksUri);
        Assert.Equal("https://localhost:7819/identity/jwks", new JwksTokenVerifier("https://localhost:7819", http).JwksUri);
    }
}
