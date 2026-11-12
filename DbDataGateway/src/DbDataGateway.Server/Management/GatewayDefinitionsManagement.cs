using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.State.Api;
using DbDataKit.Views;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Serving;

namespace DbDataGateway.Server.Management;

/// <summary>
/// The shared publish API (plan 30 PB02, <see cref="DefinitionsApi"/>) on the Gateway's management listener, for every serving kind of <see cref="ServingKinds"/> and the views' <c>dashboard</c> and <c>report</c>:
/// <c>HEAD</c> and <c>PUT /api/v1/artifacts/{sha256}</c>, <c>GET /api/v1/definitions/{kind}[/{name}]</c>, <c>PUT /api/v1/definitions/{kind}/{name}</c>, <c>POST .../activate|switch|rollback|retire|prune</c>, <c>PUT /api/v1/bindings/{channel}/{kind}/{name}</c>.
/// The management caller's roles and token decide who the caller is (a paired client's id is what the owner rules compare); every change is in the Gateway's audit. The per-kind endpoints are still there (plan 30: they go when Sync is
/// operational on the Runner) and write the same rows; a definition published here is served by every instance when its follower next looks.
/// </summary>
public sealed class GatewayDefinitionsManagement
{
    public const string Prefix = GatewayManagement.Prefix + "/";

    private readonly DefinitionsApi _api;

    /// <summary>The hooks of the kinds whose publish does more than the registry's checks (plan 30 PB02): the Gateway's semantic models and data sources, with what the per-kind routes check and do.</summary>
    public static IReadOnlyList<IDefinitionKindHook> HooksOf(StateDatabase state, GatewaySemanticModels semanticModels, PublishedSemanticModels models, GatewayDataSources dataSources, GatewayEntityModels? entityModels = null)
    {
        var policy = new SignaturePolicy(new GroupSettings(state));
        return [new SemanticModelKindHook(semanticModels, models, policy), new DataSourceKindHook(dataSources, policy), .. entityModels is null ? (IEnumerable<IDefinitionKindHook>)[] : [new EntityModelKindHook(entityModels, policy)]];
    }

    public GatewayDefinitionsManagement(StateDatabase state, IAuditLog? audit = null, TimeProvider? clock = null, IEnumerable<IDefinitionKindHook>? hooks = null)
    {
        var store = new DefinitionStore(state);
        var payloads = new PayloadStore(state);
        var kinds = ServingKinds.Register(ViewDefinitions.Register(new DefinitionKinds(), ViewPublishingSources.From(store, payloads)));
        var time = clock ?? TimeProvider.System;
        var trust = new DefinitionTrustStore(state);
        var settings = new GroupSettings(state);
        _api = new DefinitionsApi(new DefinitionPublisher(state, kinds, payloads, ServingKinds.TargetType), store, kinds, payloads, settings, ServingKinds.TargetType,
            (verb, caller, kind, name, channel, outcome, _) => audit?.Append(new AuditEntry(time.GetUtcNow(), "api", "management", caller.ClientId ?? caller.Actor, caller.Actor, false, verb, $"{kind}/{name}" + (channel == "-" ? "" : "/" + channel), outcome, "")),
            verifier: new TrustStoreVerifier(trust), policy: new SignaturePolicy(settings), trust: trust, hooks: hooks);
    }

    public static bool Handles(string route) => route.StartsWith(Prefix, StringComparison.Ordinal) && DefinitionsApi.Handles(route[Prefix.Length..]);

    private static DefinitionsCaller CallerOf(ManagementCaller caller)
    {
        var entry = caller.Audit("", null, "", DateTimeOffset.UtcNow);
        var actor = entry.UserName ?? entry.UserId;
        var client = caller.Token is { IsConsole: false } token ? token.Client.Id : null;
        return new DefinitionsCaller(actor, caller.Holds(ServerRole.Admin), caller.Holds(ServerRole.Publisher), caller.Holds(ServerRole.Viewer), client) { Context = caller };
    }

    public ManagementResult Handle(string method, string route, string? body, ManagementCaller caller) => HandleAsync(method, route, body, caller).GetAwaiter().GetResult();

    /// <summary>The same for a host that awaits: a kind's hook may wait on a data source's test query.</summary>
    public async Task<ManagementResult> HandleAsync(string method, string route, string? body, ManagementCaller caller, CancellationToken ct = default)
    {
        var answer = await _api.HandleAsync(method, route[Prefix.Length..], body, CallerOf(caller), ct);
        return new ManagementResult(answer.Status, answer.Body);
    }

    public bool HasArtifact(string sha256) => _api.HasArtifact(sha256);

    /// <summary>A stored file by hash for a promotion to another group (plan 30 PB06), or null.</summary>
    public Stream? OpenArtifact(string sha256) => _api.OpenArtifact(sha256);

    public async Task<ManagementResult> StoreArtifactAsync(string sha256, Stream body, string? mediaType, ManagementCaller caller, CancellationToken ct)
    {
        var answer = await _api.StoreArtifactAsync(sha256, body, mediaType, CallerOf(caller), ct);
        return new ManagementResult(answer.Status, answer.Body);
    }
}
