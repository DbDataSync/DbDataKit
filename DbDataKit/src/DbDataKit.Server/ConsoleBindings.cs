using DbDataKit.Config.Yaml;
using DbDataKit.Connections;
using DbDataKit.Connections.Bindings;
using DbDataKit.Knowledge;
using System.Text.Json;
using DbDataKit.State;

namespace DbDataKit.Server;

/// <summary>The connections of the console as the bindings' checker finds them: the files of <see cref="ConsoleConnections"/> that read.</summary>
public sealed class ConsoleBindingTargets(ConsoleConnections connections) : IBindingTargets
{
    public BindingTarget? Find(string connection) => connections.Find(connection) is { } d ? BindingTarget.From(d) : null;

    public IEnumerable<BindingTarget> All => connections.Read().Where(r => r.Result.Definition is not null).Select(r => BindingTarget.From(r.Result.Definition!));
}

/// <summary>A binding that needs the administrator's confirmed choice (plan 27a IX17): a non-production release channel of a slot that sends or writes uses a connection that production uses.</summary>
public sealed record UnconfirmedBinding(string Scope, string Section, string Slot, string Release, string Connection, string Effect);

/// <summary>What a save did: whether the file was written, what the reader and checker said, and the choices that must be confirmed first.</summary>
public sealed record BindingsSaveOutcome(bool Saved, IReadOnlyList<DiagnosticView> Diagnostics, IReadOnlyList<UnconfirmedBinding> NeedsConfirmation);

/// <summary>
/// The bindings of the console (plan 27a IX23, <c>docs/channel-bindings.md</c>): <c>bindings.yml</c> in the console's directory, which connection each model slot and each delivery channel uses in each release channel, read by the one strict reader
/// (<see cref="BindingsLoader"/>) and written by the one writer. <see cref="ConsoleModels"/> and <see cref="ConsoleDelivery"/> read their slots from here and no place else; the screens edit through <see cref="SetSlot"/>, <see cref="RemoveSlot"/> and <see cref="Save"/>.
/// <b>A production connection in a non-production release channel of a slot that sends or writes (a delivery channel) is an explicit, confirmed, recorded choice</b> (IX17; the posture setting <see cref="PostureSetting.ProductionBindingOnNonProduction"/>, scoped to the slot and release channel):
/// a save that would make one is refused until it is confirmed, and the choice is recorded with who and when. The runner's own package slots are not here (the runner keeps its store, <c>package/1</c> is frozen); the screens read them from the runner.
/// </summary>
public sealed class ConsoleBindings
{
    private readonly string _directory;
    private readonly ConsoleConnections _connections;
    private readonly object _lock = new();
    private string _stamp = "\0";
    private (string Yaml, BindingsResult Result) _read = ("", new BindingsResult(BindingSet.Empty, []));

    private readonly AuditLog _audit;

    public ConsoleBindings(ConsoleSettings settings, ConsoleConnections connections, AuditLog audit)
    {
        _directory = settings.Directory;
        _connections = connections;
        _audit = audit;
        Targets = new ConsoleBindingTargets(connections);
    }

    public string File => Path.Combine(_directory, BindingsLoader.FileName);

    /// <summary>The place of the earlier model slots (<c>model-slots/1</c>), refused when it is still there.</summary>
    public string EarlierModelSlotsFile => Path.Combine(_directory, "models", "_slots.yml");

    public IBindingTargets Targets { get; }

    /// <summary>Raised after the file was written: the slots' readers use the new bindings with the next call.</summary>
    public event Action? Changed;

    /// <summary>The words about files of an earlier form still lying around (they are not read), or null.</summary>
    public string? Earlier => System.IO.File.Exists(EarlierModelSlotsFile)
        ? $"{EarlierModelSlotsFile} is the earlier form of the model slots (`model-slots: 1`) and is not read any more. Move each slot into {BindingsLoader.FileName} under `models:` (`provider:` is `connection:`), then delete that file."
        : null;

    /// <summary>A change to the file or to a connection is seen by the next read; the stamp says when to read again.</summary>
    public string Stamp() => System.IO.File.Exists(File) ? $"{System.IO.File.GetLastWriteTimeUtc(File).Ticks}:{new FileInfo(File).Length}" : "";

    public (string Yaml, BindingsResult Result) Read()
    {
        lock (_lock)
        {
            var stamp = Stamp();
            if (stamp == _stamp) return _read;
            var yaml = System.IO.File.Exists(File) ? System.IO.File.ReadAllText(File) : "";
            _read = (yaml, BindingsLoader.Load(yaml, BindingsLoader.FileName));
            _stamp = stamp;
            return _read;
        }
    }

    /// <summary>The bindings as the file says them; nothing is bound when the file does not read (the screen shows why).</summary>
    public BindingSet Current => Read().Result.Bindings ?? BindingSet.Empty;

    /// <summary>Every problem of the file as it is: the reader's, and what names a connection that is not there or does not fit.</summary>
    public IReadOnlyList<DiagnosticView> Diagnostics()
    {
        var (_, result) = Read();
        var list = result.Diagnostics.Select(DiagnosticView.Of).ToList();
        if (result.Bindings is { } set) list.AddRange(BindingsLoader.Check(set, Targets, BindingsLoader.FileName).Select(DiagnosticView.Of));
        return list;
    }

    public (BindingsResult Result, IReadOnlyList<DiagnosticView> Diagnostics) Check(string yaml)
    {
        var result = BindingsLoader.Load(yaml, BindingsLoader.FileName);
        var diagnostics = result.Diagnostics.Select(DiagnosticView.Of).ToList();
        if (result.Bindings is { } set) diagnostics.AddRange(BindingsLoader.Check(set, Targets, BindingsLoader.FileName).Select(DiagnosticView.Of));
        return (result, diagnostics);
    }

    /// <summary>The kind of slot a section and name make: <c>delivery</c> channels, <c>models</c> by the slot's name.</summary>
    public static SlotKind? KindOf(string section, string name) => section switch
    {
        "delivery" => SlotKind.Delivery,
        "models" => BindingsLoader.ModelKindOf(name),
        _ => null,
    };

    public static string SectionOf(SlotKind kind) => kind == SlotKind.Delivery ? "delivery" : "models";

    /// <summary>A slot that sends or writes: a production connection in another release channel is a recorded choice. A model slot only reads (a prompt goes out, nothing in the world changes; what may leave is the agent policy's rule).</summary>
    public static bool SendsOrWrites(SlotKind kind) => kind is SlotKind.Delivery or SlotKind.Runner;

    public static string ScopeOf(SlotKind kind, string slot, string release) => $"bindings/{SectionOf(kind)}/{slot}/{release}";

    /// <summary>The bindings of a set that make a production connection serve a non-production release channel, for a slot that sends or writes. A <c>*</c> entry serves every release channel without its own, so it counts for the release channels it covers.</summary>
    public static IReadOnlyList<UnconfirmedBinding> Unconfirmed(BindingSet set)
    {
        var list = new List<UnconfirmedBinding>();
        foreach (var slot in set.Slots.Where(s => SendsOrWrites(s.Kind)))
        {
            var production = slot.Resolve(BindingSet.Production);
            if (!production.IsBound) continue;
            var prodConnections = production.Entries.Select(e => e.Connection).ToHashSet(StringComparer.Ordinal);
            foreach (var (release, entries) in slot.PerChannel)
            {
                if (release == BindingSet.Production) continue;
                var shared = entries.FirstOrDefault(e => prodConnections.Contains(e.Connection));
                if (shared is null) continue;
                var where = release == BindingSet.AnyReleaseChannel ? "every release channel without an entry of its own" : $"the release channel '{release}'";
                list.Add(new UnconfirmedBinding(ScopeOf(slot.Kind, slot.Name, release), SectionOf(slot.Kind), slot.Name, release, shared.Connection,
                    $"The {SlotBinding.Describe(slot.Kind)} '{slot.Name}' would use '{shared.Connection}', which production uses, for {where}: what is sent there reaches production's far side. {PostureSetting.ProductionBindingOnNonProduction.Effect}"));
            }
        }

        return list;
    }

    /// <summary>Writes the file given as text (the person's own, comments kept): refused when it does not read or names a connection that does not fit, and until every production-in-non-production choice it makes is confirmed.</summary>
    public BindingsSaveOutcome Save(string yaml, IReadOnlyCollection<string>? confirmed, string actor, string? note)
    {
        var (result, diagnostics) = Check(yaml);
        if (diagnostics.Any(d => d.Severity == "error") || result.Bindings is not { } set) return new(false, diagnostics, []);
        return Write(yaml, set, diagnostics, confirmed, actor, note);
    }

    /// <summary>Binds one slot: <paramref name="perChannel"/> is its release channels and, for each, its ordered choices (nothing at all removes the slot). The file is written again from what it holds (its comments are not kept).</summary>
    public BindingsSaveOutcome SetSlot(string section, string name, IReadOnlyDictionary<string, IReadOnlyList<BindingEntry>> perChannel, IReadOnlyCollection<string>? confirmed, string actor, string? note)
    {
        if (KindOf(section, name) is not { } kind) return Refused($"There is no section '{section}'. Sections: models, delivery.");
        var (_, read) = Read();
        if (read.Bindings is not { } current) return new(false, [.. read.Diagnostics.Select(DiagnosticView.Of)], []);
        var slot = new SlotBinding(kind, name, new Dictionary<string, IReadOnlyList<BindingEntry>>(perChannel.Where(c => c.Value.Count > 0), StringComparer.Ordinal));
        var set = current.With(slot);
        var text = BindingsWriter.Write(set);
        var reread = BindingsLoader.Load(text, BindingsLoader.FileName);
        var diagnostics = reread.Diagnostics.Select(DiagnosticView.Of).ToList();
        if (reread.Bindings is { } checkedSet) diagnostics.AddRange(BindingsLoader.Check(checkedSet, Targets, BindingsLoader.FileName).Select(DiagnosticView.Of));
        if (diagnostics.Any(d => d.Severity == "error") || reread.Bindings is null) return new(false, diagnostics, []);
        return Write(text, reread.Bindings, diagnostics, confirmed, actor, note);
    }

    public BindingsSaveOutcome RemoveSlot(string section, string name, string actor)
    {
        if (KindOf(section, name) is null) return Refused($"There is no section '{section}'. Sections: models, delivery.");
        return SetSlot(section, name, new Dictionary<string, IReadOnlyList<BindingEntry>>(), null, actor, null);
    }

    private static BindingsSaveOutcome Refused(string message) => new(false, [new DiagnosticView(BindingsLoader.BadValue, "error", BindingsLoader.FileName, 0, 0, message, null)], []);

    private BindingsSaveOutcome Write(string text, BindingSet set, IReadOnlyList<DiagnosticView> diagnostics, IReadOnlyCollection<string>? confirmed, string actor, string? note)
    {
        lock (_lock)
        {
            var before = Unconfirmed(Current);
            var needs = Unconfirmed(set).Where(u => !Confirmed(u.Scope) && confirmed?.Contains(u.Scope, StringComparer.Ordinal) != true).ToList();
            if (needs.Count > 0) return new(false, diagnostics, needs);

            Directory.CreateDirectory(_directory);
            System.IO.File.WriteAllText(File, text);
            _stamp = "\0";
            var now = Unconfirmed(set);
            foreach (var u in now.Where(u => !Confirmed(u.Scope)))
                Choose(u.Scope, actor, note ?? $"{u.Slot} ({u.Release}) uses {u.Connection}");
            foreach (var gone in before.Where(b => now.All(n => n.Scope != b.Scope)))
                Revoke(gone.Scope, actor, "the binding no longer shares production's connection");
        }

        Changed?.Invoke();
        return new(true, diagnostics, []);
    }

    /// <summary>A recorded production-in-non-production choice: who, when, why.</summary>
    public sealed record Choice(string Scope, string ChosenBy, DateTimeOffset ChosenAt, string? Note);

    private string ChoicesFile => Path.Combine(_directory, "bindings-posture.json");

    private Dictionary<string, Choice> Choices()
    {
        try { return System.IO.File.Exists(ChoicesFile) ? JsonSerializer.Deserialize<Dictionary<string, Choice>>(System.IO.File.ReadAllText(ChoicesFile)) ?? [] : []; }
        catch (JsonException) { return []; }
    }

    private void SaveChoices(Dictionary<string, Choice> choices) => System.IO.File.WriteAllText(ChoicesFile, JsonSerializer.Serialize(choices, new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>Whether the production-in-non-production choice of a scope was recorded (confirmed by an administrator).</summary>
    public bool Confirmed(string scope) => Choices().ContainsKey(scope);

    public Choice? ChoiceOf(string scope) => Choices().GetValueOrDefault(scope);

    private void Choose(string scope, string actor, string? note)
    {
        var all = Choices();
        all[scope] = new Choice(scope, actor, DateTimeOffset.UtcNow, note);
        SaveChoices(all);
        _audit.Record(new AuditEvent("person", actor, "bindings.posture.choose", ViaKind: "console", TargetKind: PostureGuard.SettingKind, TargetId: PostureSetting.ProductionBindingOnNonProduction.Name, Detail: JsonSerializer.Serialize(new { scope, note })));
    }

    private void Revoke(string scope, string actor, string? note)
    {
        var all = Choices();
        if (!all.Remove(scope)) return;
        SaveChoices(all);
        _audit.Record(new AuditEvent("person", actor, "bindings.posture.revoke", ViaKind: "console", TargetKind: PostureGuard.SettingKind, TargetId: PostureSetting.ProductionBindingOnNonProduction.Name, Detail: JsonSerializer.Serialize(new { scope, note })));
    }
}
