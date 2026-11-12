using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Connections;
using DbDataKit.Connections.Bindings;
using DbDataKit.Knowledge;
using DbDataKit.State;
using DbDataKit.State.Api;
using DbDataKit.Web.Auth;

namespace DbDataKit.Server;

/// <summary>
/// One thing the console and another server both hold, as the compare screen lists it (plan 27a IX25). <see cref="Family"/> is <c>binding</c> (a slot of a definition the other server holds, in a release channel, which stands for a slot
/// of the console's <c>bindings.yml</c>) or <c>connection</c> (a connection file of the console and the copy a Runner holds). <see cref="Here"/> and <see cref="There"/> are the values as shown
/// (a binding's choices as <see cref="BindingValueText"/> says them; a connection's version), null where that side holds none. A connection's texts hold secret references and never a value.
/// </summary>
public sealed record CompareItem(string Id, string Family, string Name, string? DefinitionKind, string? Slot, string? Channel, string State, string? Here, string? There, string? HereText, string? ThereText, bool CanAdopt, bool CanPublish, string? Problem);

/// <summary>What a compare answers: the server compared with, what could not be read, and the items.</summary>
public sealed record CompareView(string Server, string ServerName, string Product, IReadOnlyList<string> Problems, IReadOnlyList<CompareItem> Items);

/// <summary>What a person asks to adopt or publish: the server, the item (<see cref="CompareItem.Family"/>, name and, for a binding, the definition's kind, slot and channel) and the other side's value as the screen showed it (<see cref="Expected"/>).</summary>
public sealed record CompareDecision(string Server, string Family, string Name, string? DefinitionKind, string? Slot, string? Channel, string? Expected);

/// <summary>The outcome of an adopt or a publish, in the words of the side that refused when one did.</summary>
public sealed record CompareOutcome(bool Done, int Status, string? Error, IReadOnlyList<DiagnosticView> Diagnostics, CompareItem? Item = null);

/// <summary>The combined list of posture choices: each choice once, and what could not be read.</summary>
public sealed record PostureView(IReadOnlyList<PostureEntry> Entries, IReadOnlyList<PostureSource> Sources);

/// <summary>A table the combined list read: the console's, the embedded runner's or a registered runner's, and whether it answered.</summary>
public sealed record PostureSource(string Holder, bool Ok, int Choices, string? Problem);

/// <summary>
/// The console's compare, adopt and publish of its bindings and of the connections it keeps against a Runner's and a Gateway's (plan 27a IX25, owner decision of 2026-11-12, <c>docs/channel-bindings.md</c>), and the one combined,
/// read-only list of the posture choices of the console's table (<c>bindings.posture_choice</c>) and the Runners' (<c>runner.posture_choice</c>). <b>It has no path of its own to a server.</b> Every read and every write of the other
/// server is plan 30's shared route (<see cref="DefinitionsClient"/> over the link <see cref="ConsolePromotions"/> makes, as the signed-in person, so the server decides what the person may do and records them as the actor): a binding is
/// published with <c>PUT /api/v1/bindings/{channel}/{kind}/{name}</c> (or emptied with <c>DELETE</c>); a Runner's connection goes by the connection copy the Bindings page
/// already sends (<see cref="ConsoleRunnerBindings.SendAsync"/>), the same call. A Gateway is compared for its bindings only: it keeps database connections as <c>data-source</c> definitions and the console keeps connections of outside systems, so the two share no connection. Adopting writes the console's own file (<c>bindings.yml</c> through <see cref="ConsoleBindings.SetSlot"/>, a connection through
/// <see cref="ConsoleConnections.Save"/>: the strict readers and the checkers decide) and records who did it in the audit (<c>bindings.adopt</c>, <c>connection.adopt</c>). Enforcement stays local: a bind that makes production's connection serve
/// a non-production channel is still confirmed where it is made (the console's table or the Runner's), never here.
/// <para>
/// Which slots correspond: a slot of another server's definition named <c>model:&lt;slot&gt;</c> or <c>delivery:&lt;channel&gt;</c> (the name the registry gives an agent's model slot, <c>AgentDefinitions.SlotName</c>) stands for the
/// console's model slot or delivery channel of that name; every other slot (a package's own connection slot) is bound on the other server only and is not an item. The value of a model slot's binding is
/// <see cref="BindingValueText"/>: <c>connection/model, connection/model</c>.
/// </para>
/// </summary>
public sealed class ConsoleBindingCompare(ConsoleBindings bindings, ConsoleConnections connections, ConsolePromotions promotions, ConsoleInstances instances, ConsoleRunnerBindings runners, ConsoleRelay relay, AuditLog audit, IServiceProvider services)
{
    public const string BindingFamily = "binding";
    public const string ConnectionFamily = "connection";

    /// <summary>The kinds of definition whose binding rows a server is read for: the ones that declare slots named for a console slot (<c>agent</c>, whose model slot is <c>model:&lt;slot&gt;</c>) and a runner's packages.</summary>
    private static IReadOnlyList<string> KindsOf(string product) => product == ServerProducts.Runner ? ["integration", "agent"] : ["agent"];

    private static string StateName(CompareState state) => state switch { CompareState.Same => "same", CompareState.Differs => "differs", CompareState.OnlyHere => "only-here", _ => "only-there" };

    private async Task<(string? Product, string? Name, string? Problem)> ServerOfAsync(string server, CancellationToken ct)
    {
        if (server == ConsoleRunnerSettings.ServerId) return services.GetService<ConsoleRunner>() is null ? (null, null, "The console's embedded runner is not running.") : (ServerProducts.Runner, "the console's runner", null);
        if (instances.Registry.Find(server) is not { } instance) return (null, null, $"No server '{server}' is registered.");
        var status = await instances.StatusAsync(instance, ct);
        return !status.Reachable ? (null, instance.Name, status.Problem ?? $"{instance.Name} is not answering.") : (status.Product, instance.Name, null);
    }

    // ---- compare ----

    public async Task<(CompareView? View, int Status, string? Error)> CompareAsync(string server, RelayPerson person, CancellationToken ct)
    {
        var (product, name, problem) = await ServerOfAsync(server, ct);
        if (problem is not null) return (null, 404, problem);
        if (product is not (ServerProducts.Runner or ServerProducts.Gateway)) return (null, 400, $"{name} is a {product} server; the console compares its bindings with a Runner's and a Gateway's.");
        var link = promotions.LinkFor(server, person);
        if (link is null) return (null, 404, $"No server '{server}' is registered.");

        var problems = new List<string>();
        var items = new List<CompareItem>();
        var client = new DefinitionsClient(link);
        items.AddRange(await BindingItemsAsync(client, product!, name!, problems, ct));
        if (product == ServerProducts.Runner) items.AddRange(await RunnerConnectionItemsAsync(link, name!, problems, ct));
        return (new CompareView(server, name!, product!, problems, [.. items.OrderBy(i => i.Family, StringComparer.Ordinal).ThenBy(i => i.Name, StringComparer.Ordinal).ThenBy(i => i.Slot, StringComparer.Ordinal).ThenBy(i => i.Channel, StringComparer.Ordinal)]), 200, null);
    }

    private static string BindingId(string kind, string name, string slot, string channel) => $"{BindingFamily}/{kind}/{name}/{slot}/{channel}";

    /// <summary>The value the console holds for a console slot in a release channel, as the group's binding row would say it; null when the channel resolves to nothing.</summary>
    private string? HereValue(SlotKind kind, string slot, string channel)
    {
        var resolved = bindings.Current.Resolve(kind, slot, channel);
        return resolved.IsBound ? BindingValueText.Encode(resolved.Entries) : null;
    }

    private async Task<List<CompareItem>> BindingItemsAsync(DefinitionsClient client, string product, string serverName, List<string> problems, CancellationToken ct)
    {
        var items = new List<CompareItem>();
        foreach (var kind in KindsOf(product))
        {
            DefinitionsAnswer? refused = null;
            var names = await client.NamesAsync(kind, a => refused = a, ct);
            if (names is null)
            {
                if (refused?.Status is not (400 or 404)) problems.Add($"{serverName} would not list its {kind} definitions: {refused?.Error ?? "no answer"}");
                continue;
            }

            foreach (var definition in names)
            {
                var described = await client.DescribeAsync(kind, definition, ct);
                if (!described.Ok) { problems.Add($"{serverName} would not describe {kind} {definition}: {described.Error ?? "no answer"}"); continue; }
                var rows = (described.Body["bindings"] as JsonArray ?? []).OfType<JsonObject>()
                    .Select(b => (Channel: (string?)b["channel"] ?? "", Slot: (string?)b["slot"] ?? "", Bound: (string?)b["bound_to"]))
                    .Where(b => b.Channel.Length > 0 && BindingCompare.ConsoleSlotOf(b.Slot) is not null).ToList();
                var slots = rows.Select(r => r.Slot).Distinct(StringComparer.Ordinal).ToList();
                foreach (var slot in slots)
                {
                    var (_, slotKind, slotName) = BindingCompare.ConsoleSlotOf(slot)!.Value;
                    var held = rows.Where(r => r.Slot == slot).ToDictionary(r => r.Channel, r => r.Bound, StringComparer.Ordinal);
                    var channels = held.Keys.Concat(bindings.Current.Find(slotKind, slotName)?.ReleaseChannels.Where(c => c != BindingSet.AnyReleaseChannel) ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
                    foreach (var channel in channels)
                    {
                        var here = HereValue(slotKind, slotName, channel);
                        var there = held.GetValueOrDefault(channel);
                        var state = BindingCompare.Of(here, there);
                        items.Add(new CompareItem(BindingId(kind, definition, slot, channel), BindingFamily, definition, kind, slot, channel, StateName(state), here, there, null, null,
                            CanAdopt: state is CompareState.Differs or CompareState.OnlyThere, CanPublish: state != CompareState.Same, null));   // publishing the console's "none" empties the slot there
                    }
                }
            }
        }

        return items;
    }

    private CompareItem ConnectionItem(string name, string? hereText, string? thereText, string? hereVersion, string? thereVersion, string? problem)
    {
        var state = BindingCompare.Of(hereVersion, thereVersion);
        return new CompareItem($"{ConnectionFamily}/{name}", ConnectionFamily, name, null, null, null, StateName(state), hereVersion, thereVersion, hereText, thereText,
            CanAdopt: state is CompareState.Differs or CompareState.OnlyThere && thereText is not null, CanPublish: state is CompareState.Differs or CompareState.OnlyHere && hereText is not null && problem is null, problem);
    }

    private async Task<List<CompareItem>> RunnerConnectionItemsAsync(IDefinitionsLink link, string serverName, List<string> problems, CancellationToken ct)
    {
        var items = new List<CompareItem>();
        var (status, body) = await GetAsync(link, "connection-copies", ct);
        if (status != 200 || body?["copies"] is not JsonArray copies)
        {
            problems.Add($"{serverName} would not list the connection copies it holds: {body?["error"]?.GetValue<string>() ?? $"status {status}"}");
            return items;
        }

        foreach (var copy in copies.OfType<JsonObject>())
        {
            var name = (string?)copy["name"] ?? "";
            var here = runners.TextOf(name);
            items.Add(ConnectionItem(name, here, (string?)copy["definition"], here is null ? null : ConnectionCopyVersion.Of(here), (string?)copy["version"], null));
        }

        return items;
    }

    private static async Task<(int Status, JsonNode? Body)> GetAsync(IDefinitionsLink link, string path, CancellationToken ct)
    {
        using var response = await link.SendAsync(HttpMethod.Get, path, null, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonNode? json = null;
        try { json = text.Length > 0 ? JsonNode.Parse(text) : null; } catch (JsonException) { }
        return ((int)response.StatusCode, json);
    }

    // ---- adopt and publish ----

    private static CompareOutcome Refused(int status, string error, IReadOnlyList<DiagnosticView>? diagnostics = null) => new(false, status, error, diagnostics ?? []);

    private async Task<(CompareItem? Item, CompareOutcome? Refusal)> CurrentAsync(CompareDecision decision, RelayPerson person, CancellationToken ct)
    {
        var (view, status, error) = await CompareAsync(decision.Server, person, ct);
        if (view is null) return (null, Refused(status, error!));
        var id = decision.Family == ConnectionFamily ? $"{ConnectionFamily}/{decision.Name}" : BindingId(decision.DefinitionKind ?? "", decision.Name, decision.Slot ?? "", decision.Channel ?? "");
        // a connection the Runner does not hold yet is still the console's to publish (the Bindings page sends it the same way)
        var item = view.Items.FirstOrDefault(i => i.Id == id)
            ?? (decision.Family == ConnectionFamily && view.Product == ServerProducts.Runner ? NewConnection(decision.Name) : null);
        if (item is null) return (null, Refused(404, $"There is nothing to compare for '{id}' on {view.ServerName}."));
        // the screen's decision was taken on the value it showed: when the other side changed since, nothing is done
        if (decision.Expected is not null && !string.Equals(item.There, decision.Expected, StringComparison.Ordinal))
            return (item, Refused(409, $"{view.ServerName} changed since you looked ({(item.There is null ? "it holds none now" : $"it now holds {item.There}")}). Compare again."));
        return (item, null);
    }

    private CompareItem NewConnection(string name)
    {
        var here = runners.TextOf(name);
        return ConnectionItem(name, here, null, here is null ? null : ConnectionCopyVersion.Of(here), null, null);
    }

    /// <summary>Takes the other side's value into the console's own files, as the signed-in person.</summary>
    public async Task<CompareOutcome> AdoptAsync(CompareDecision decision, RelayPerson person, CancellationToken ct)
    {
        var (item, refusal) = await CurrentAsync(decision, person, ct);
        if (refusal is not null) return refusal;
        if (!item!.CanAdopt) return Refused(409, item.State == "same" ? "Both sides hold the same value." : "The other side holds nothing to adopt.");
        return item.Family == ConnectionFamily ? AdoptConnection(item, decision, person) : AdoptBinding(item, decision, person);
    }

    private CompareOutcome AdoptConnection(CompareItem item, CompareDecision decision, RelayPerson person)
    {
        if (!ConsoleConnections.IsName(item.Name)) return Refused(400, $"'{item.Name}' is not a connection name the console keeps.");
        var (saved, diagnostics) = connections.Save(item.Name, item.ThereText!);
        if (!saved) return Refused(422, $"The console would not keep {item.Name} as {decision.Server} holds it.", diagnostics);
        Record(person, "connection.adopt", "connection", item.Name, new { server = decision.Server, version = item.There, replaced = item.Here });
        return new CompareOutcome(true, 200, null, diagnostics, item with { State = "same", Here = item.There, HereText = item.ThereText, CanAdopt = false, CanPublish = false });
    }

    private CompareOutcome AdoptBinding(CompareItem item, CompareDecision decision, RelayPerson person)
    {
        var (section, kind, slotName) = BindingCompare.ConsoleSlotOf(item.Slot!)!.Value;
        var entries = BindingValueText.Decode(item.There, wantsModel: kind is SlotKind.ModelChat or SlotKind.ModelEmbeddings, kind == SlotKind.ModelChat ? 8 : 1, out var problem);
        if (entries is null) return Refused(422, $"The console cannot read {item.There} as a {SlotBinding.Describe(kind)} choice: {problem}");
        var current = bindings.Current.Find(kind, slotName);
        var perChannel = (current?.PerChannel ?? new Dictionary<string, IReadOnlyList<BindingEntry>>()).ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal);
        perChannel[item.Channel!] = entries;
        var outcome = bindings.SetSlot(section, slotName, perChannel, null, person.Id, $"adopted from {decision.Server}");
        if (!outcome.Saved)
        {
            var words = outcome.NeedsConfirmation.Count > 0 ? "It would make production's connection serve a non-production release channel: bind it on the Bindings page, where that is confirmed." : "The console would not keep it.";
            return Refused(outcome.NeedsConfirmation.Count > 0 ? 409 : 422, words, outcome.Diagnostics);
        }

        Record(person, "bindings.adopt", "binding", $"{section}/{slotName}/{item.Channel}", new { server = decision.Server, definition = item.DefinitionKind + ":" + item.Name, slot = item.Slot, value = item.There, replaced = item.Here });
        return new CompareOutcome(true, 200, null, outcome.Diagnostics, item with { State = "same", Here = item.There, CanAdopt = false, CanPublish = false });
    }

    /// <summary>Puts the console's value on the other server through the shared route, as the signed-in person.</summary>
    public async Task<CompareOutcome> PublishAsync(CompareDecision decision, RelayPerson person, CancellationToken ct)
    {
        var (item, refusal) = await CurrentAsync(decision, person, ct);
        if (refusal is not null) return refusal;
        if (!item!.CanPublish) return Refused(409, item.State == "same" ? "Both sides hold the same value." : item.Problem ?? "The console holds nothing to publish.");
        var (product, name, problem) = await ServerOfAsync(decision.Server, ct);
        if (problem is not null) return Refused(404, problem);
        return item.Family == ConnectionFamily ? await PublishConnectionAsync(item, decision, product!, name!, person, ct) : await PublishBindingAsync(item, decision, name!, person, ct);
    }

    private async Task<CompareOutcome> PublishBindingAsync(CompareItem item, CompareDecision decision, string serverName, RelayPerson person, CancellationToken ct)
    {
        var client = new DefinitionsClient(promotions.LinkFor(decision.Server, person)!);
        var answer = item.Here is { } value
            ? await client.BindAsync(item.Channel!, item.DefinitionKind!, item.Name, new Dictionary<string, string> { [item.Slot!] = value }, null, ct)
            : await client.UnbindAsync(item.Channel!, item.DefinitionKind!, item.Name, item.Slot!, ct);
        if (!answer.Ok) return Refused(answer.Status is >= 400 and < 600 ? answer.Status : 502, $"{serverName} refused it: {answer.Error ?? $"status {answer.Status}"}", DiagnosticsOf(answer.Body));
        Record(person, "bindings.publish", "binding", $"{decision.Server}/{item.DefinitionKind}/{item.Name}/{item.Slot}/{item.Channel}", new { server = decision.Server, value = item.Here, replaced = item.There });
        return new CompareOutcome(true, 200, null, [], item with { State = "same", There = item.Here, CanAdopt = false, CanPublish = false });
    }

    private async Task<CompareOutcome> PublishConnectionAsync(CompareItem item, CompareDecision decision, string product, string serverName, RelayPerson person, CancellationToken ct)
    {
        if (product != ServerProducts.Runner)
            return Refused(409, $"{serverName} is a Gateway: it keeps database connections as data sources, and the console keeps connections of outside systems (http, model providers, mail, files), so there is no connection of the console to publish to it.");
        // the connection copy of the Bindings page: the definition with its secret references, never a value
        var (step, sent) = await runners.SendAsync(decision.Server, item.Name, person.Id, person.Name, ct);
        if (!step.Ok || sent is null) return Refused(step.Status, step.Error ?? $"{serverName} did not accept the connection.", [.. step.Diagnostics.Select(d => new DiagnosticView("DBD-COMPARE", "error", item.Name, 0, 0, d, null))]);
        Record(person, "connection.publish", "connection", item.Name, new { server = decision.Server, version = sent.Version, replaced = item.There });
        return new CompareOutcome(true, 200, null, [], item with { State = "same", There = sent.Version, ThereText = item.HereText, CanAdopt = false, CanPublish = false });
    }

    private static IReadOnlyList<DiagnosticView> DiagnosticsOf(JsonObject body) =>
        [.. (body["diagnostics"] as JsonArray ?? []).Select(d => new DiagnosticView("DBD-COMPARE", "error", "", 0, 0, d is JsonValue v && v.TryGetValue<string>(out var text) ? text : d?.ToJsonString() ?? "", null))];

    private void Record(RelayPerson person, string verb, string targetKind, string target, object detail) =>
        audit.Record(new AuditEvent("person", person.Id, verb, ViaKind: "console", ViaId: "console", TargetKind: targetKind, TargetId: target, Detail: JsonSerializer.Serialize(detail)));

    // ---- the combined posture list ----

    /// <summary>The console's table, the embedded runner's and each registered runner's, read and merged: a choice once whichever table holds it. A runner that does not answer is named, and the rest is still listed.</summary>
    public async Task<PostureView> PostureAsync(RelayPerson person, CancellationToken ct)
    {
        var holdings = new List<PostureHolding>();
        var sources = new List<PostureSource>();

        var own = bindings.AllChoices();
        holdings.AddRange(own.Select(c => new PostureHolding("console", c)));
        sources.Add(new PostureSource("console", true, own.Count, null));

        if (services.GetService<ConsoleRunner>() is { } embedded)
        {
            var list = embedded.Posture.Choices.List();
            holdings.AddRange(list.Select(c => new PostureHolding("the console's runner", c)));
            sources.Add(new PostureSource("the console's runner", true, list.Count, null));
        }

        foreach (var instance in instances.Registry.All())
        {
            var status = await instances.StatusAsync(instance, ct);
            if (!status.Reachable || status.Product != ServerProducts.Runner)
            {
                if (status.Product == ServerProducts.Runner || !status.Reachable) sources.Add(new PostureSource(instance.Name, false, 0, status.Problem ?? $"{instance.Name} is not answering."));
                continue;
            }

            var (code, body) = await relay.CallAsAsync(instance, person.Id, person.Name, person.Role, HttpMethod.Get, "/api/v1/posture-choices", null, ct);
            if (code != 200 || body?["choices"] is not JsonArray choices)
            {
                sources.Add(new PostureSource(instance.Name, false, 0, body?["error"]?.GetValue<string>() ?? $"{instance.Name} answered {code}."));
                continue;
            }

            var read = choices.OfType<JsonObject>().Select(c => new PostureChoice((string?)c["scope"] ?? "", (string?)c["setting"] ?? "", (string?)c["chosen_by"] ?? "", DateTimeOffset.TryParse((string?)c["chosen_at"], out var at) ? at : DateTimeOffset.MinValue, (string?)c["note"])).ToList();
            holdings.AddRange(read.Select(c => new PostureHolding(instance.Name, c)));
            sources.Add(new PostureSource(instance.Name, true, read.Count, null));
        }

        return new PostureView(PostureListing.Merge(holdings), sources);
    }
}
