using DbDataKit.State;
using DbDataKit.Web;
using DbDataKit.Web.Trust;
using DbDataSync.Api.Management;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataSync.Api.Tests;

/// <summary>
/// A test console (plan 13a, X06): Sync keeps no sign-in, so every call these tests make is what a console's relay makes: a signed token naming a person, from a console paired in <c>server.yaml</c>.
/// It pairs itself with the host the first time a token is asked for (the host exists by then), and each request carries a token of its own, because a token is for one request.
/// </summary>
public sealed class ConsoleCaller(Func<IServiceProvider> services)
{
    private readonly ConsoleIdentity _console = new("test-console-" + Guid.NewGuid().ToString("N"), ConsoleSigningKey.Generate());
    private readonly object _gate = new();
    private bool _paired;

    private SyncManagement Management => services().GetRequiredService<SyncManagement>();

    /// <summary>The roles the product names, as the roles a console grants.</summary>
    public static ServerRole RolesOf(UserRole role) => role == UserRole.Admin ? ServerRole.Admin : ServerRole.Viewer;

    public string Token(UserRole role, string person = "test-person", string name = "Test Person")
    {
        lock (_gate)
        {
            if (!_paired)
            {
                Management.Files.Clients().Upsert(new PairedClient(_console.Id, "test console", _console.Key.PublicKey, ServerRoles.Close(ServerRole.Admin | ServerRole.Viewer), ClientKind.Console));
                _paired = true;
            }
            return _console.Issuer().Issue(new ConsoleTokenRequest(Management.InstanceId, person, name, RolesOf(role)));
        }
    }

    /// <summary>A handler that puts a new token on every request that passes through it, in front of <paramref name="inner"/> when one is given.</summary>
    public DelegatingHandler Handler(UserRole role, string person = "test-person", string name = "Test Person", HttpMessageHandler? inner = null) =>
        inner is null ? new TokenHandler(() => Token(role, person, name)) : new TokenHandler(() => Token(role, person, name)) { InnerHandler = inner };

    private sealed class TokenHandler(Func<string> token) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Remove(ManagementAuthentication.TokenHeader);
            request.Headers.Add(ManagementAuthentication.TokenHeader, token());
            return base.SendAsync(request, cancellationToken);
        }
    }
}
