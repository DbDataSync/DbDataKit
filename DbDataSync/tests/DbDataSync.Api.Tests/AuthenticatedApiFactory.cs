using System.Net;
using DbDataKit.State;
using DbDataSync.Core.Secrets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DbDataSync.Api.Tests;

/// <summary>
/// The API with **no caller by default**, for the tests of who may call what; <see cref="TestApiFactory"/> calls as an administrator, because its tests are about what an endpoint does.
/// <para>
/// Sync keeps no sign-in of its own (plan 13a, X06): a caller is a console's signed token. <see cref="SignedInAsAsync"/> pairs a test console with the host and returns a client whose every
/// request carries a new token for the role. That is the real handler and the real policies: only the console that issues the token is the test's.
/// </para>
/// </summary>
public class AuthenticatedApiFactory : WebApplicationFactory<Program>
{
    public string RepoRoot { get; } = Directory.CreateTempSubdirectory("dbdatasync-auth-tests-").FullName;

    private ConsoleCaller? _caller;

    public ConsoleCaller Caller => _caller ??= new ConsoleCaller(() => Services);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DbDataSync:App:RepoRoot"] = RepoRoot,
                ["DbDataSync:State:DbPath"] = Path.Combine(RepoRoot, "state.db"),
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<SecretStore>();
            services.AddSingleton(SecretStore.ForProviders(new InMemorySecretProvider()));
            // See TestServerConnectionItemsFilter: the Kestrel connection feature some hosts of this project need on Windows.
            services.AddTestServerConnectionItems();
        });
    }

    /// <summary>A client that calls as a new person (an id of its own, so per-person state such as a read cursor starts empty) who holds <paramref name="role"/>, through a console paired with this host.</summary>
    public async Task<HttpClient> SignedInAsAsync(UserRole role) => (await SignedInAsPersonAsync(role)).Client;

    /// <summary>The same, and who the person is: the name a token carries is the author of a config change.</summary>
    public Task<(HttpClient Client, string Id, string Name)> SignedInAsPersonAsync(UserRole role)
    {
        var id = $"{role}-{Guid.NewGuid():N}".ToLowerInvariant();
        var name = $"{role} {id[^6..]}";
        return Task.FromResult((CreateDefaultClient(Caller.Handler(role, id, name)), id, name));
    }

    /// <summary>A handler for a connection that is not an <see cref="HttpClient"/> of this factory's (SignalR over the test server): a new token on every request.</summary>
    public HttpMessageHandler Signed(UserRole role, HttpMessageHandler inner) => Caller.Handler(role, $"{role}-person".ToLowerInvariant(), $"{role} person", inner);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            GitTempDirectory.DeleteRecursively(RepoRoot);
    }
}
