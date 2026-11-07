using System.Net;
using System.Reflection;
using DbDataSync.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using DbDataSync.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Every endpoint has had a decision made about it, and the decision is enumerated from the
/// controllers rather than from a list somebody maintains — a hand-written list of routes is a list
/// that goes stale the first time somebody adds one in a hurry.
/// </summary>
public sealed class AuthorizationCoverageTests
{
    private static IEnumerable<(Type Controller, MethodInfo Action)> Endpoints() =>
        typeof(DbDataSync.Api.Controllers.ReplicationsController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any())
                .Select(m => (t, m)));

    /// <summary>
    /// The fallback policy closes anything unmarked, so this asserts the *shape* rather than the
    /// permission: an action is either explicitly anonymous, explicitly viewer-readable, or falls
    /// through to admin. What must not exist is an action nobody thought about that reads as open.
    /// </summary>
    [Fact]
    public void EveryEndpoint_IsEitherAnonymous_ViewerReadable_OrAdminByFallback()
    {
        var endpoints = Endpoints().ToList();

        Assert.NotEmpty(endpoints);

        foreach (var (controller, action) in endpoints)
        {
            var anonymous = action.GetCustomAttribute<AllowAnonymousAttribute>() is not null
                || controller.GetCustomAttribute<AllowAnonymousAttribute>() is not null;
            var policy = action.GetCustomAttribute<AuthorizeAttribute>()?.Policy
                ?? controller.GetCustomAttribute<AuthorizeAttribute>()?.Policy;

            Assert.True(
                anonymous || policy is null or Policies.Viewer or Policies.Admin,
                $"{controller.Name}.{action.Name} carries an authorize policy this app does not define ('{policy}').");
        }
    }

    /// <summary>
    /// **The two that look like reads and are not.** `connections/{name}/test` opens a connection to
    /// somebody's database and `scripts/{name}/test` compiles and executes operator-authored C#. Both
    /// are GET-shaped in intent and neither belongs to a viewer, which is why the dividing line is
    /// "does this make something happen" rather than the HTTP verb.
    /// </summary>
    [Theory]
    [InlineData("ConnectionsController", "Test")]
    [InlineData("ScriptsController", "Test")]
    [InlineData("ScriptsController", "Compile")]
    public void TheActionsThatLookLikeReads_AreNotViewerReadable(string controllerName, string actionName)
    {
        var (controller, action) = Endpoints()
            .Single(e => e.Controller.Name == controllerName && e.Action.Name == actionName);

        var policy = action.GetCustomAttribute<AuthorizeAttribute>()?.Policy
            ?? controller.GetCustomAttribute<AuthorizeAttribute>()?.Policy;

        Assert.NotEqual(Policies.Viewer, policy);
    }
}

/// <summary>
/// The same thing over the wire, because attributes are a claim about what happens and a request is
/// the thing that happens.
/// </summary>
public sealed class AuthorizationEnforcementTests(AuthenticatedApiFactory factory)
    : IClassFixture<AuthenticatedApiFactory>
{
    [Theory]
    [InlineData("/api/connections")]
    [InlineData("/api/replications")]
    [InlineData("/api/scripts")]
    public async Task WithoutAToken_EverythingIsRefused(string path)
    {
        var response = await factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Health is the container's own probe. A probe that needs a session reports a healthy
    /// application as unreachable.</summary>
    [Fact]
    public async Task Health_IsReachableWithoutAToken() =>
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/api/health")).StatusCode);

    /// <summary>
    /// Plan 13a, X06: Sync maps none of the shared sign-in controllers (the console serves them), so the routes are plain 404s even for an administrator's token, and no controller of that assembly is part of this host.
    /// </summary>
    [Theory]
    [InlineData("/api/auth/status")]
    [InlineData("/api/auth/sign-out")]
    [InlineData("/api/users")]
    [InlineData("/api/invites")]
    public async Task TheSharedSignInRoutes_AreNotMapped(string path)
    {
        var client = await factory.SignedInAsAsync(DbDataKit.State.UserRole.Admin);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(path, null)).StatusCode);
        var parts = factory.Services.GetRequiredService<Microsoft.AspNetCore.Mvc.ApplicationParts.ApplicationPartManager>().ApplicationParts.Select(part => part.Name);
        Assert.DoesNotContain(typeof(DbDataKit.Web.Auth.Controllers.AuthController).Assembly.GetName().Name, parts);
    }

    /// <summary>A token that is not a console's, or that a console of ours did not sign, is no way in; a cookie or Basic credentials are not read at all.</summary>
    [Fact]
    public async Task ABadToken_AndEveryOldWayIn_IsRefused()
    {
        using var bad = factory.CreateClient();
        bad.DefaultRequestHeaders.Add(DbDataKit.Web.Trust.ManagementAuthentication.TokenHeader, "not-a-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await bad.GetAsync("/api/replications")).StatusCode);

        using var cookie = factory.CreateClient();
        cookie.DefaultRequestHeaders.Add("Cookie", "dbdatasync.session=anything");
        Assert.Equal(HttpStatusCode.Unauthorized, (await cookie.GetAsync("/api/replications")).StatusCode);

        using var basic = factory.CreateClient();
        basic.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String("ada:secret"u8.ToArray()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await basic.GetAsync("/api/replications")).StatusCode);
    }

    [Fact]
    public async Task AViewer_CanRead()
    {
        var client = await factory.SignedInAsAsync(DbDataKit.State.UserRole.Viewer);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/replications")).StatusCode);
    }

    [Theory]
    [InlineData("/api/replications/anything/runs")]
    [InlineData("/api/connections/anything/test")]
    [InlineData("/api/scripts/anything/compile")]
    public async Task AViewer_CannotMakeAnythingHappen(string path)
    {
        var client = await factory.SignedInAsAsync(DbDataKit.State.UserRole.Viewer);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(path, null)).StatusCode);
    }

    /// <summary>An admin is a viewer too — stated once in the policy rather than by putting both roles
    /// on every read endpoint.</summary>
    [Fact]
    public async Task AnAdmin_CanRead()
    {
        var client = await factory.SignedInAsAsync(DbDataKit.State.UserRole.Admin);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/replications")).StatusCode);
    }
}
