using DbDataKit.State;
using DbDataKit.Web.Auth;
using DbDataKit.Web.Trust;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server;

/// <summary>
/// The releases of the console (plan 21 CH02): a release spans the products of one install, so the console hosts the <c>releases</c> state module and runs its items. An item promotes a package (<c>integration</c> of a <c>runner</c>) on its target server from one
/// channel to the next, reading it from the item's source server (the target itself when none is named), through <see cref="ConsolePromotions"/> as the person who started or resumed the release.
/// <b>The approval permission of an item is the administrator role on its target server</b> (owner default, 2026-11-12): what the promotion itself needs there, as the person holds it (the identity home's roles, or the console's own, within what the server lets this console grant).
/// </summary>
public sealed class ConsoleReleases(StateDatabase state, ConsoleInstances instances, ConsoleRelay relay, ConsolePromotions promotions, IUserAccounts users, IServiceProvider services)
{
    public const string Product = "runner";
    public const string Kind = "integration";

    public ReleaseStore Store { get; } = new(state);

    private bool KnownServer(string server) => server == ConsoleRunnerSettings.ServerId ? services.GetService<ConsoleRunner>() is not null : instances.Registry.Find(server) is not null;

    /// <summary>Why an item cannot be part of a release, or null.</summary>
    public string? Problem(ReleaseItem item)
    {
        if (item.Product != Product) return $"The console runs releases of '{Product}' items; '{item.Product}' is not one yet.";
        if (item.Kind != Kind) return $"The console promotes packages ('{Kind}'); '{item.Kind}' is not one.";
        if (string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.FromChannel) || string.IsNullOrWhiteSpace(item.ToChannel) || item.Version < 1) return "An item names a package, a version (1 or more) and the two channels.";
        if (!KnownServer(item.Target)) return $"No server '{item.Target}' is registered.";
        if (item.Source is { Length: > 0 } source && !KnownServer(source)) return $"No server '{source}' is registered.";
        return null;
    }

    /// <summary>Whether the user holds the approval permission (the administrator role) on the item's target server.</summary>
    private sealed class Permissions(IUserAccounts users, ConsoleInstances instances, ConsoleRelay relay) : IReleaseApprovalPermissions
    {
        public bool Holds(string approver, ReleaseItem item)
        {
            if (users.Get(approver) is not { Enabled: true } user) return false;
            if (item.Target == ConsoleRunnerSettings.ServerId) return ServerRoles.Allows(ConsoleRunner.RolesOf(user.Role), ServerRole.Admin);
            return instances.Registry.Find(item.Target) is { } instance && ServerRoles.Allows(relay.RolesHeldOn(instance, user.Id, user.Role), ServerRole.Admin);
        }
    }

    public ReleaseRecord Approve(string id, string approver) => Store.Approve(id, approver, new Permissions(users, instances, relay));

    /// <summary>Runs (or resumes) the release as <paramref name="person"/>: each item is a promotion made with that person's roles.</summary>
    public Task<ReleaseRecord> StartAsync(string id, RelayPerson person, bool resume, CancellationToken ct)
    {
        var runner = new ReleaseRunner(Store, new ItemRunner(promotions, person));
        return resume ? runner.ResumeAsync(id, ct) : runner.StartAsync(id, ct);
    }

    /// <summary>Promotes one item through <see cref="ConsolePromotions"/>; a refusal of either group is the item's failure, in the group's words.</summary>
    public sealed class ItemRunner(ConsolePromotions promotions, RelayPerson person) : IReleaseItemRunner
    {
        public async Task<ReleaseItemResult> RunAsync(ReleaseRecord release, ReleaseItem item, CancellationToken cancellationToken)
        {
            try
            {
                var done = await promotions.PromoteAsync(item.Source ?? item.Target, item.Target,
                    new PromoteRequest(item.Kind, item.Name, item.FromChannel, item.ToChannel, item.Version, Note: $"release '{release.Name}', item {item.Position}"), person, cancellationToken);
                return ReleaseItemResult.Success;
            }
            catch (DefinitionPromotionException ex) { return ReleaseItemResult.Failure(ex.Message); }
        }
    }

    /// <summary>The name a person goes by, for a screen (ids are what the release stores).</summary>
    public string NameOf(string? id) => id is null ? "" : users.Get(id)?.DisplayName ?? id;
}
