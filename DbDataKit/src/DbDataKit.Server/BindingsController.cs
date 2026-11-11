using DbDataKit.Agents;
using DbDataKit.Connections;
using DbDataKit.Connections.Bindings;
using DbDataKit.Knowledge;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataKit.Server;

/// <summary>One choice of a slot as a screen sends it: the connection and, for a model slot, which of its models.</summary>
public sealed record BindingChoiceBody(string Connection, string? Model = null);

/// <summary>A slot's new bindings: per release channel (or <c>*</c>) its ordered choices, the scopes of the production-in-non-production choices the administrator confirms, and a note for the record.</summary>
public sealed record SlotBody(Dictionary<string, List<BindingChoiceBody>> Channels, List<string>? Confirm = null, string? Note = null);

public sealed record BindingsYamlBody(string Yaml, List<string>? Confirm = null, string? Note = null);

public sealed record BindingEntryView(string Connection, string? Model, bool Usable, string? Problem, string? Kind, string? Target);

public sealed record BindingRowView(string Release, bool Wildcard, IReadOnlyList<BindingEntryView> Entries, bool NeedsConfirmation, bool Confirmed, string? Effect);

public sealed record BindingSlotView(string Section, string Kind, string Name, IReadOnlyList<string> UsedBy, int? LimitPerMinute, IReadOnlyList<BindingRowView> Rows);

public sealed record BindingConnectionView(string Name, string Kind, string? Purpose, IReadOnlyList<string> Models, bool CanSend, string? Target, bool? Cloud);

public sealed record BindingsView(string File, string Yaml, IReadOnlyList<DiagnosticView> Diagnostics, string? Earlier, IReadOnlyList<string> ReleaseChannels, IReadOnlyList<BindingConnectionView> Connections,
    IReadOnlyList<BindingSlotView> Slots, string PostureSetting, string PostureEffect);

/// <summary>
/// The Bindings screen's API (plan 27a IX23, <c>docs/channel-bindings.md</c>): every slot of the console that binds a name to a connection per release channel (the model slots and the delivery channels), with whether each choice can be used now, and the writes through
/// <see cref="ConsoleBindings"/>: one slot at a time, or the file as text. The runner's package slots are not here: the screen reads them from the runner in the same shape. A save that would make production's connection serve a non-production release channel of a delivery
/// channel is refused (409) until it is confirmed, then recorded (IX17).
/// </summary>
[ApiController]
[Route("api/bindings")]
public sealed class BindingsController(ConsoleBindings bindings, ConsoleModels models, ConsoleDelivery delivery, ConsoleConnections connections, ConsoleAgents agents) : ControllerBase
{
    private string PersonId => User.FindFirst(ConsoleHost.Auth.UserIdClaim)?.Value ?? User.Identity?.Name ?? "unknown";

    private static string KindName(SlotKind kind) => kind switch { SlotKind.ModelChat => "model-chat", SlotKind.ModelEmbeddings => "model-embeddings", SlotKind.Delivery => "delivery", _ => "runner" };

    private IActionResult Outcome(BindingsSaveOutcome o, Func<object> saved) =>
        o.Saved ? Ok(saved()) : o.NeedsConfirmation.Count > 0
            ? Conflict(new { saved = false, diagnostics = o.Diagnostics, needsConfirmation = o.NeedsConfirmation, error = "Production's connection would serve a non-production release channel. Confirm each choice to record it." })
            : UnprocessableEntity(new { saved = false, diagnostics = o.Diagnostics });

    public async Task<BindingsView> BuildAsync(CancellationToken ct)
    {
        var (yaml, _) = bindings.Read();
        var set = bindings.Current;
        var delivered = (await delivery.ChannelViewsAsync(ct)).ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var unconfirmed = ConsoleBindings.Unconfirmed(set).ToDictionary(u => u.Scope, StringComparer.Ordinal);
        var asked = agents.List().GroupBy(a => a.ModelSlot, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(a => a.Name).Order(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

        var slots = new List<BindingSlotView>();
        foreach (var slot in set.Slots.OrderBy(s => s.Kind == SlotKind.Delivery ? 1 : 0).ThenBy(s => s.Name, StringComparer.Ordinal))
        {
            var rows = new List<BindingRowView>();
            foreach (var release in slot.ReleaseChannels.OrderBy(r => r == BindingSet.Production ? "" : r == BindingSet.AnyReleaseChannel ? "~" : r, StringComparer.Ordinal))
            {
                var entries = slot.PerChannel[release].Select(e => EntryOf(slot, release, e, delivered)).ToList();
                var scope = ConsoleBindings.ScopeOf(slot.Kind, slot.Name, release);
                unconfirmed.TryGetValue(scope, out var need);
                rows.Add(new BindingRowView(release, release == BindingSet.AnyReleaseChannel, entries, need is not null, need is not null && bindings.Confirmed(scope), need?.Effect));
            }

            slots.Add(new BindingSlotView(ConsoleBindings.SectionOf(slot.Kind), KindName(slot.Kind), slot.Name, slot.Kind == SlotKind.ModelChat ? asked.GetValueOrDefault(slot.Name) ?? [] : [], delivered.GetValueOrDefault(slot.Name)?.LimitPerMinute, rows));
        }

        // a slot an agent asks for and nothing binds is listed too, with no row: it is where a person starts
        foreach (var (name, users) in asked.Where(a => set.Find(BindingsLoader.ModelKindOf(a.Key), a.Key) is null).OrderBy(a => a.Key, StringComparer.Ordinal))
            slots.Add(new BindingSlotView("models", KindName(BindingsLoader.ModelKindOf(name)), name, users, null, []));

        var releases = set.Slots.SelectMany(s => s.PerChannel.Keys).Where(k => k != BindingSet.AnyReleaseChannel).Prepend(BindingSet.Production).Distinct(StringComparer.Ordinal).ToList();
        var targets = bindings.Targets.All.OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => new BindingConnectionView(t.Name, t.Kind, t.Purpose, t.Models ?? [], t.CanSend,
            connections.Find(t.Name) is { } d ? ConsoleConnections.TargetOf(d) : null, connections.Find(t.Name) is { ModelProvider: not null } md ? ModelProviderConnection.From(md).Cloud : null)).ToList();
        return new BindingsView(BindingsLoader.FileName, yaml, bindings.Diagnostics(), bindings.Earlier, releases, targets, slots, PostureSetting.ProductionBindingOnNonProduction.Name, PostureSetting.ProductionBindingOnNonProduction.Effect);
    }

    private BindingEntryView EntryOf(SlotBinding slot, string release, BindingEntry e, IReadOnlyDictionary<string, ChannelView> delivered)
    {
        var found = bindings.Targets.Find(e.Connection);
        if (slot.Kind == SlotKind.Delivery)
        {
            var row = delivered.GetValueOrDefault(slot.Name)?.Bindings.FirstOrDefault(b => b.Release == release);
            return new BindingEntryView(e.Connection, null, row?.Ready ?? false, row is null ? "Not read yet." : row.Ready ? null : row.Problem ?? string.Join("; ", row.Missing), row?.Kind ?? found?.Kind, row?.Target);
        }

        var (usable, problem) = models.EntryState(e.Connection, e.Model);
        var d = connections.Find(e.Connection);
        return new BindingEntryView(e.Connection, e.Model, usable, problem, found?.Kind, d is null ? null : ConsoleConnections.TargetOf(d));
    }

    /// <summary>Every slot with its release channels, the choices and whether each can be used, the connections that exist, and the file's problems.</summary>
    [HttpGet]
    [Authorize(Policies.Viewer)]
    public async Task<IActionResult> Get(CancellationToken ct) => Ok(await BuildAsync(ct));

    /// <summary>Binds one slot (an administrator's act): its release channels and their ordered choices, read back through the strict reader and checked against the connections. Nothing at all removes the slot.</summary>
    [HttpPut("slots/{section}/{name}")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> SetSlot(string section, string name, [FromBody] SlotBody body, CancellationToken ct)
    {
        var channels = body.Channels.ToDictionary(c => c.Key, c => (IReadOnlyList<BindingEntry>)[.. c.Value.Select(x => new BindingEntry(x.Connection, string.IsNullOrWhiteSpace(x.Model) ? null : x.Model))], StringComparer.Ordinal);
        var outcome = bindings.SetSlot(section, name, channels, body.Confirm, PersonId, body.Note);
        return outcome.Saved ? Ok(new { saved = true, diagnostics = outcome.Diagnostics, view = await BuildAsync(ct) }) : Outcome(outcome, () => new { });
    }

    [HttpDelete("slots/{section}/{name}")]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> RemoveSlot(string section, string name, CancellationToken ct)
    {
        var outcome = bindings.RemoveSlot(section, name, PersonId);
        return outcome.Saved ? Ok(new { saved = true, diagnostics = outcome.Diagnostics, view = await BuildAsync(ct) }) : Outcome(outcome, () => new { });
    }

    /// <summary>Reads a text of the file and says what is wrong with it, without writing it.</summary>
    [HttpPost("check")]
    [Authorize(Policies.Admin)]
    public IActionResult Check([FromBody] BindingsYamlBody body)
    {
        var (result, diagnostics) = bindings.Check(body.Yaml);
        return Ok(new { diagnostics, needsConfirmation = result.Bindings is { } set ? ConsoleBindings.Unconfirmed(set).Where(u => !bindings.Confirmed(u.Scope)) : [] });
    }

    /// <summary>Writes the file as text (comments kept), as the strict reader and the checker allow it.</summary>
    [HttpPut]
    [Authorize(Policies.Admin)]
    public async Task<IActionResult> Save([FromBody] BindingsYamlBody body, CancellationToken ct)
    {
        var outcome = bindings.Save(body.Yaml, body.Confirm, PersonId, body.Note);
        return outcome.Saved ? Ok(new { saved = true, diagnostics = outcome.Diagnostics, view = await BuildAsync(ct) }) : Outcome(outcome, () => new { });
    }
}
