using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataKit.Server;

/// <summary>
/// Promotion between the groups a console manages (plan 30 PB06): <see cref="DefinitionPromoter"/> run by the console over the shared publish API of two servers, each reached <b>as the signed-in person</b> (the token the relay signs, the roles
/// the person holds there, the server's own check on top): the source is read as a viewer, the target is written as a publisher or an administrator, and each server records the person as the actor of what it was asked. A registered server is reached
/// through <see cref="ConsoleRelay.SendAsAsync"/> (for a runner only the calls of <see cref="ServerProducts.RunnerRoutes"/> pass).
/// </summary>
public sealed class ConsolePromotions(ConsoleInstances instances, ConsoleRelay relay)
{
    /// <summary>The server a call names as a link as <paramref name="person"/> reaches it, or null when none is called that (a registered server, by id or name).</summary>
    public IDefinitionsLink? LinkFor(string server, RelayPerson person)
    {
        return instances.Registry.Find(server) is { } instance ? new RegisteredLink(relay, instance, person) : null;
    }

    /// <summary>Promotes <paramref name="request"/> from <paramref name="from"/> to <paramref name="to"/> as <paramref name="person"/>. A server that is not registered or a promotion a group refuses is a <see cref="DefinitionPromotionException"/> with the group's words.</summary>
    public async Task<Promotion> PromoteAsync(string from, string to, PromoteRequest request, RelayPerson person, CancellationToken ct)
    {
        var source = LinkFor(from, person) ?? throw new DefinitionPromotionException($"No server '{from}' is registered.", 404, "not_found");
        var target = LinkFor(to, person) ?? throw new DefinitionPromotionException($"No server '{to}' is registered.", 404, "not_found");
        return await new DefinitionPromoter().PromoteAsync(source, target, request, null, ct);
    }

    private sealed class RegisteredLink(ConsoleRelay relay, RegisteredInstance instance, RelayPerson person) : IDefinitionsLink
    {
        public string Name => instance.Name;

        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct) =>
            relay.SendAsAsync(instance, person.Id, person.Name, person.Role, method, "api/v1/" + path, content, ct);
    }
}
