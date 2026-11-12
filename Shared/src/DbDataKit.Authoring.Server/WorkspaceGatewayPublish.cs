using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.DataModel;
using DbDataKit.State;
using DbDataKit.State.Api;
using DbDataKit.Web.Trust;

namespace DbDataKit.Authoring.Server;

/// <summary>
/// The Workspace's publish of what a Store or a Build project made, over plan 30's shared route (plan 33 SW01): <see cref="DefinitionsClient"/> against the Gateway's <c>/api/v1/definitions/{kind}/{name}</c>, artifacts by hash, bindings and the signature
/// when the group trusts the publisher key, in place of the per-kind routes the Workspace used (SC20e, SC21b). The rules of plan 33 D20 stand: the Workspace holds no database login and publishes with the target's publisher key, and a data source's login stays on
/// the Gateway (a data source document is sent without one, and the login an administrator set on the Gateway is kept when the document is sent again). What the Gateway says in refusal comes back in its words.
/// <para>
/// Data sources: a model names the data sources it needs. One the publishing client already owns (it made it, or this publish makes it from the build's <c>data-sources/</c>) needs nothing more. One it did not make is a <b>slot</b> of the version (<c>slots</c> in the manifest):
/// the Gateway refuses the version naming the slot until the group binds it, and the Workspace binds it itself only where the Gateway lets a publishing client (a data source the client made); a data source made by hand is bound by an administrator of the Gateway, once,
/// and the refusal says so and carries the data source for the confirmation (<c>adopt</c>, SC21f) that is still there as the other way until it is retired.
/// </para>
/// </summary>
public sealed partial class WorkspaceGatewayPublish(IDefinitionsLink link, ConsoleIdentity identity, Action<string> step, CancellationToken ct = default)
{
    public const string ModelKind = "semantic-model";
    public const string EntityModelKind = "entity-model";
    public const string DataSourceKind = "data-source";
    private const string Channel = DefinitionStore.Production;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly DefinitionsClient client = new(link, identity.Key);

    /// <summary>The Store project's models (<paramref name="models"/>: file name and text) to the Gateway as <c>entity-model</c> definitions, on the data source the model's storage names unless <paramref name="dataSource"/> names another.</summary>
    public async Task<JsonArray> PublishEntityModelsAsync(IReadOnlyList<(string File, string Text)> models, string? dataSource, IReadOnlyList<string>? adopt)
    {
        var owned = await OwnedAsync();
        var published = new JsonArray();
        foreach (var (file, text) in models)
        {
            var loaded = DataModelLoader.Load(text, Path.GetFileName(file));
            if (loaded.Model is not { } model) throw new WorkspacePublishException($"{Path.GetFileName(file)} is not a model that loads.", 409);
            var onto = string.IsNullOrWhiteSpace(dataSource) ? model.Connection : dataSource.Trim();
            // the person's confirmation names a data source; it holds for the data source the models go on, and nothing else (plan 33 SC21f)
            if (adopt is { Count: > 0 } && !adopt.Contains(onto, StringComparer.Ordinal))
                throw new WorkspacePublishException($"The confirmation names {string.Join(", ", adopt.Select(a => "'" + a + "'"))}, and {model.Name} goes on '{onto}': confirm the data source it goes on.", 400);
            var adopting = adopt is { Count: > 0 };
            string[] slots = adopting || owned.Contains(onto) ? [] : [onto];

            step($"Asked the Gateway for the version of {model.Name} it holds.");
            var held = await ServedAsync(EntityModelKind, model.Name, $"reading the model {model.Name}");
            var manifest = JsonSerializer.Serialize(new { model = text, dataSource = onto, managedBy = identity.Id, slots = slots.Length == 0 ? null : slots }, new JsonSerializerOptions(Json) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            var request = new DefinitionPublishRequest(EntityModelKind, model.Name, manifest, ExpectedVersion: held?.Latest ?? 0,
                DependsOn: owned.Contains(onto) ? ["data-source:" + onto] : null, Extra: adopting ? new JsonObject { ["adopt"] = onto } : null);
            var answer = await PublishBindingSlotsAsync(request, slots, $"the model {model.Name} on the data source '{onto}'");
            step($"Published {model.Name} to the Gateway on the data source '{onto}'.");
            published.Add(new JsonObject
            {
                ["model"] = model.Name, ["data_source"] = onto, ["version"] = (int?)answer.Body["version"], ["created"] = (bool?)answer.Body["created"], ["unchanged"] = (bool?)answer.Body["unchanged"],
                ["adopted"] = answer.Body["adopted"]?.DeepClone(), ["slots_used"] = answer.Body["slots_used"]?.DeepClone(),
            });
        }
        return published;
    }

    /// <summary>
    /// The compiled semantic models of a Build project (<paramref name="folder"/> is <c>artifact/gateway/</c>: <c>gateway.json</c>, <c>models/</c>, <c>data-sources/</c>; SC20e) to the Gateway as <c>data-source</c> and <c>semantic-model</c> definitions. A data source document is sent
    /// when the Gateway has none of that name or the client made it (the document names no login; the login set on the Gateway stays), and left alone when it was made by hand: the model then reads it through a slot or an adoption.
    /// </summary>
    public async Task<(JsonArray Models, JsonArray DataSources)> PublishSemanticModelsAsync(string folder, IReadOnlyList<string>? adopt)
    {
        var manifestPath = Path.Combine(folder, "gateway.json");
        if (!File.Exists(manifestPath)) throw new WorkspacePublishException("This build has no semantic model to publish (gateway.json is not in it).", 409);
        JsonObject manifest;
        try { manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { throw new WorkspacePublishException("gateway.json of this build is not a document.", 409); }
        if ((string?)manifest["schema"] != "gateway-publish/1") throw new WorkspacePublishException($"gateway.json of this build says '{(string?)manifest["schema"]}', which this server does not read (gateway-publish/1).", 409);
        var entries = (manifest["models"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        if (entries.Count == 0) throw new WorkspacePublishException("This build has no semantic model to publish.", 409);

        var owned = await OwnedAsync();
        var sources = new JsonArray();
        foreach (var entry in (manifest["data_sources"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var name = (string?)entry["name"] ?? throw new WorkspacePublishException("gateway.json names a data source with no name.", 409);
            var text = File.ReadAllText(FileIn(folder, (string?)entry["file"], $"The document of the data source {name}"));
            var done = await PublishDataSourceAsync(name, text);
            if (done is not null) owned.Add(name);
            sources.Add(done ?? new JsonObject { ["name"] = name, ["left_alone"] = true });
        }

        var models = new JsonArray();
        foreach (var entry in entries)
        {
            var name = (string?)entry["name"] ?? throw new WorkspacePublishException("gateway.json names a model with no name.", 409);
            var catalog = File.ReadAllText(FileIn(folder, (string?)entry["file"], $"The catalog of {name}"));
            var map = new JsonObject();
            if (entry["sources"] is JsonObject mapped) foreach (var (source, dataSource) in mapped) map[source] = (string?)dataSource;
            var used = map.Select(s => (string?)s.Value).Where(s => s is not null).Select(s => s!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var slots = used.Where(u => !owned.Contains(u) && adopt?.Contains(u, StringComparer.Ordinal) != true).ToArray();
            var lower = name.ToLowerInvariant();

            step($"Asked the Gateway for the version of {name} it holds.");
            var held = await ServedAsync(ModelKind, lower, $"reading the model {name}");
            var body = new JsonObject { ["catalog"] = catalog, ["managedBy"] = identity.Id, ["sources"] = map.DeepClone(), ["ssas"] = new JsonObject() };
            if (slots.Length > 0) body["slots"] = new JsonArray([.. slots.Select(s => (JsonNode?)s)]);
            var request = new DefinitionPublishRequest(ModelKind, lower, body.ToJsonString(), ExpectedVersion: held?.Latest ?? 0,
                DependsOn: [.. used.Where(owned.Contains).Select(u => "data-source:" + u)], Extra: adopt is { Count: > 0 } ? new JsonObject { ["adopt"] = new JsonArray([.. adopt.Select(a => (JsonNode?)a)]) } : null);
            var answer = await PublishBindingSlotsAsync(request, slots, $"the model {name} on the data sources {string.Join(", ", used.Select(u => "'" + u + "'"))}");
            step($"Published {name} to the Gateway.");
            models.Add(new JsonObject
            {
                ["model"] = name, ["data_sources"] = map.DeepClone(), ["version"] = (int?)answer.Body["version"], ["created"] = (bool?)answer.Body["created"], ["unchanged"] = (bool?)answer.Body["unchanged"],
                ["adopted"] = answer.Body["adopted"]?.DeepClone(), ["slots_used"] = answer.Body["slots_used"]?.DeepClone(),
            });
        }
        return (models, sources);
    }

    /// <summary>The names of the data-source definitions the publishing client owns: the Gateway lists a caller that only publishes the names it owns and no others.</summary>
    private async Task<HashSet<string>> OwnedAsync()
    {
        var names = await client.NamesAsync(DataSourceKind, null, ct) ?? [];
        return new HashSet<string>(names, StringComparer.Ordinal);
    }

    private async Task<ServedDefinition?> ServedAsync(string kind, string name, string doing)
    {
        DefinitionsAnswer? refusal = null;
        var held = await client.ServedAsync(kind, name, Channel, a => refusal = a, ct);
        if (refusal is not null) throw Refused(refusal, doing);
        return held;
    }

    /// <summary>One data source document, sent as the publishing client's own (<c>managed_by</c> is added: the manifest is signed, so the Gateway checks the line and does not write it). Null when the Gateway holds a data source of that name made by hand or by another client, which is left as it is.</summary>
    private async Task<JsonObject?> PublishDataSourceAsync(string name, string document)
    {
        DefinitionsAnswer? unreadable = null;
        var held = await client.ServedAsync(DataSourceKind, name, Channel, a => unreadable = a, ct);
        if (unreadable is not null)
        {
            if (unreadable.Status == 403) { step($"The Gateway's data source '{name}' is not this client's (made by hand or by another client): it is left as it is."); return null; }
            throw Refused(unreadable, $"reading the data source {name}");
        }
        var text = (document.EndsWith('\n') ? document : document + "\n");
        // the login an administrator set on the Gateway stays: the document names none, and the version replaces the file
        if (held?.Manifest is { } current && !ReferenceLine().IsMatch(text) && ReferenceLine().Match(current) is { Success: true } login) text += login.Value.TrimEnd() + "\n";
        if (!ManagedByLine().IsMatch(text)) text += $"managed_by: {identity.Id}\n";
        step($"Sent the data source document of '{name}' (no login in it).");
        var answer = await client.PublishAsync(new DefinitionPublishRequest(DataSourceKind, name, text, ExpectedVersion: held?.Latest ?? 0), ct);
        if (!answer.Ok) throw Refused(answer, $"the data source {name}");
        return new JsonObject { ["name"] = name, ["version"] = (int?)answer.Body["version"], ["created"] = (bool?)answer.Body["created"] ?? held is null };
    }

    /// <summary>
    /// Publishes; a version refused for a slot the channel does not bind has its slots bound by the Workspace when the Gateway lets a publishing client (its own data sources), and is published again once. What the Gateway refuses to bind
    /// is an administrator's to bind: the refusal then names the call and carries the data source, for the confirmation (<c>adopt</c>) that is still the other way.
    /// </summary>
    private async Task<DefinitionsAnswer> PublishBindingSlotsAsync(DefinitionPublishRequest request, string[] slots, string doing)
    {
        var answer = await client.PublishAsync(request, ct);
        if (answer.Ok) return answer;
        if (answer.Body["code"]?.GetValue<string>() != "unboundslot" || slots.Length == 0) throw Refused(answer, doing);

        var bound = await client.BindAsync(Channel, request.Kind, request.Name, slots.ToDictionary(s => s, s => s, StringComparer.Ordinal), null, ct);
        if (!bound.Ok)
        {
            var unbound = slots[0];
            var call = $"PUT /api/v1/bindings/{Channel}/{request.Kind}/{request.Name} {{\"slots\": {{\"{unbound}\": \"{unbound}\"}}}}";
            throw new WorkspacePublishException($"The Gateway refused {doing} (409): {answer.Error} The Workspace could not bind it itself ({bound.Error}). An administrator of the Gateway binds it once ({call}), or the person confirms that the model may go on '{unbound}' for this publish (adopt).",
                409, new JsonObject { ["data_source"] = unbound, ["made_by"] = "hand", ["publish_adopt"] = true, ["field"] = "adopt", ["slot"] = unbound, ["bind"] = call });
        }
        step($"Bound {string.Join(", ", slots.Select(s => "'" + s + "'"))} for {request.Name} on the Gateway.");
        var again = await client.PublishAsync(request, ct);
        if (!again.Ok) throw Refused(again, doing);
        return again;
    }

    private static string FileIn(string folder, string? relative, string what)
    {
        var full = Path.GetFullPath(Path.Combine(folder, relative ?? ""));
        if (relative is null || !full.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full)) throw new WorkspacePublishException($"{what} ({relative}) is not in this build.", 409);
        return full;
    }

    internal static WorkspacePublishException Refused(DefinitionsAnswer answer, string doing)
    {
        var words = answer.Error ?? "no reason given";
        if (answer.Body["diagnostics"] is JsonArray found && found.Count > 0) words += " " + string.Join(" ", found.Select(d => d is JsonValue v && v.TryGetValue<string>(out var text) ? text : d?.ToJsonString()));
        return new WorkspacePublishException($"The Gateway refused {doing} ({answer.Status}): {words}", answer.Status == 403 ? 403 : 409, answer.Body["adopt"] is JsonObject hint ? (JsonObject)hint.DeepClone() : null);
    }

    [GeneratedRegex(@"^connection_string_secret:.*$", RegexOptions.Multiline)]
    private static partial Regex ReferenceLine();

    [GeneratedRegex(@"^managed_by:", RegexOptions.Multiline)]
    private static partial Regex ManagedByLine();
}
