using DbDataKit.State;
using DbDataKit.Web.Auth;
using DbDataKit.Web.Trust;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server;

/// <summary>
/// The releases of the console (plan 21 CH02): a release spans the products of one install, so the console hosts the <c>releases</c> state module and runs its items. An item promotes a package (<c>integration</c> of a <c>runner</c>) or a Gateway's definition (<see cref="GatewayKinds"/>) on its target server from one
/// channel to the next, reading it from the item's source server (the target itself when none is named), through <see cref="ConsolePromotions"/> as the person who started or resumed the release.
/// <b>The approval permission of an item is the administrator role on its target server</b> (owner default, 2026-11-12): what the promotion itself needs there, as the person holds it (the identity home's roles, or the console's own, within what the server lets this console grant).
/// </summary>
public sealed class ConsoleReleases(StateDatabase state, ConsoleInstances instances, ConsoleRelay relay, ConsolePromotions promotions, IUserAccounts users, IServiceProvider services)
{
    public const string Product = "runner";
    public const string Kind = "integration";
    /// <summary>The product of a Gateway item (owner decision, 2026-11-12: releases include the Gateway's definitions, approved by the administrator role on the item's target Gateway, as for a runner).</summary>
    public const string GatewayProduct = "gateway";

    /// <summary>
    /// The kinds of a Gateway's definitions a release promotes: the serving kinds, the views (<c>dashboard</c>, <c>report</c>, <c>report-catalog</c>) and the app kinds that are a definition to move between groups (<c>app</c> and <c>app-nav</c>).
    /// An app <i>instance</i> (its addresses and clients belong to one group) and an app approval are not: a group keeps its own.
    /// </summary>
    public static readonly IReadOnlyList<string> GatewayKinds = ["semantic-model", "data-source", "entity-model", "dashboard", "report", "report-catalog", "app", "app-nav"];

    public ReleaseStore Store { get; } = new(state);

    private bool KnownServer(string server) => server == ConsoleRunnerSettings.ServerId ? services.GetService<ConsoleRunner>() is not null : instances.Registry.Find(server) is not null;

    /// <summary>Whether the server is of the product the item names (a Gateway is a registered server; a runner is one or the console's own).</summary>
    private async Task<string?> WrongProductAsync(string server, string product, CancellationToken ct)
    {
        var want = product == GatewayProduct ? ServerProducts.Gateway : ServerProducts.Runner;
        if (server == ConsoleRunnerSettings.ServerId) return want == ServerProducts.Runner ? null : $"'{server}' is the console's own runner, not a Gateway.";
        var found = instances.Registry.Find(server);
        if (found is null) return $"No server '{server}' is registered.";
        var actual = await relay.ProductOfAsync(found, ct);
        return actual == want || (actual is null && want == ServerProducts.Runner) ? null : $"'{found.Name}' is {(actual is null ? "not reachable, so it is not known to be" : $"a {actual} server, not")} {(want == ServerProducts.Gateway ? "a Gateway" : "a runner")}.";
    }

    /// <summary>Why an item cannot be part of a release, or null.</summary>
    public async Task<string?> ProblemAsync(ReleaseItem item, CancellationToken ct = default)
    {
        if (item.Product is not (Product or GatewayProduct)) return $"The console runs releases of '{Product}' and '{GatewayProduct}' items; '{item.Product}' is not one yet.";
        if (item.Product == Product && item.Kind != Kind) return $"The console promotes runner packages ('{Kind}'); '{item.Kind}' is not one.";
        if (item.Product == GatewayProduct && !GatewayKinds.Contains(item.Kind)) return $"The console promotes a Gateway's {string.Join(", ", GatewayKinds)}; '{item.Kind}' is not one.";
        if (string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.FromChannel) || string.IsNullOrWhiteSpace(item.ToChannel) || item.Version < 1) return $"An item names a {(item.Product == Product ? "package" : "definition")}, a version (1 or more) and the two channels.";
        if (!KnownServer(item.Target)) return $"No server '{item.Target}' is registered.";
        if (item.Source is { Length: > 0 } source && !KnownServer(source)) return $"No server '{source}' is registered.";
        if (await WrongProductAsync(item.Target, item.Product, ct) is { } target) return target;
        if (item.Source is { Length: > 0 } from && await WrongProductAsync(from, item.Product, ct) is { } other) return other;
        return null;
    }

    /// <summary>Whether the user holds the approval permission (the administrator role) on the item's target server: a runner's or a Gateway's alike, what the relay would let the person do there (the identity home's roles, or the console's within the server's ceiling).</summary>
    private sealed class Permissions(IUserAccounts users, ConsoleInstances instances, ConsoleRelay relay) : IReleaseApprovalPermissions
    {
        public bool Holds(string approver, ReleaseItem item)
        {
            if (users.Get(approver) is not { Enabled: true } user) return false;
            if (item.Target == ConsoleRunnerSettings.ServerId) return item.Product == Product && ServerRoles.Allows(ConsoleRunner.RolesOf(user.Role), ServerRole.Admin);
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
