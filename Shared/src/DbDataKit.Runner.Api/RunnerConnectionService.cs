using System.Text.Json;
using DbDataKit.Connections;
using DbDataKit.State;

namespace DbDataKit.Runner.Api;

/// <summary>What <see cref="RunnerConnectionService.Put"/> did: the copy, or the HTTP status that refused it with the words and, for a definition that does not load, the loader's lines (never the value that was in the text).</summary>
public sealed record ConnectionPutResult(ConnectionCopy? Copy, int Status, string? Error, IReadOnlyList<string> Diagnostics)
{
    public bool Ok => Copy is not null;
}

/// <summary>
/// The copies of console connections a runner keeps, as a service the management API (<see cref="RunnerConnectionCopyApi"/>) and the console's embedded runner both call, so a connection handed over in process takes the same path (the strict
/// loader, the audit with the name and version only) as one handed over through the relay. Plan 27a IX23.
/// </summary>
public sealed class RunnerConnectionService(StateDatabase state, string serviceId = RunnerNames.ServiceId)
{
    public const string PutVerb = "runner.connection.put";
    public const string DeleteVerb = "runner.connection.delete";
    public const int MaxDefinitionChars = 64 * 1024;

    private readonly ConnectionCopies _copies = new(state);
    private readonly AuditLog _audit = new(state, serviceId);

    public ConnectionCopies Copies => _copies;

    public static bool NameRule(string name) => name.Length is > 0 and <= 64 && char.IsAsciiLetter(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');

    public ConnectionPutResult Put(string name, string? definition, RunnerActor actor)
    {
        ConnectionPutResult Refuse(int status, string error, IReadOnlyList<string>? diagnostics = null)
        {
            Record(PutVerb, actor, name, AuditEvent.Refused, new { status });
            return new ConnectionPutResult(null, status, error, diagnostics ?? []);
        }

        if (string.IsNullOrWhiteSpace(definition)) return Refuse(400, "The body is {\"definition\": \"<the connection's yaml, with secret references>\"}.");
        if (definition.Length > MaxDefinitionChars) return Refuse(413, $"A connection definition is at most {MaxDefinitionChars} characters.");
        if (!NameRule(name)) return Refuse(400, $"'{name}' is not a connection name.");

        // the strict loader refuses a secret written as itself (without echoing it), an unknown key and a kind it does not know
        var load = ConnectionDefinitionLoader.LoadConnectionOfAnyKind(definition, $"the connection '{name}' sent to this runner");
        if (!load.Ok)
            return Refuse(422, $"The connection '{name}' does not load.", [.. load.Diagnostics.Where(d => d.Severity == DbDataKit.Config.Yaml.ConfigSeverity.Error).Select(d => d.ToString())]);
        if (!string.Equals(load.Definition!.Name, name, StringComparison.Ordinal)) return Refuse(422, $"The definition names the connection '{load.Definition.Name}', not '{name}'.");
        var copy = _copies.Put(name, load.Definition.KindName, definition, actor.Id);
        Record(PutVerb, actor, name, AuditEvent.Ok, new { version = copy.Version, kind = copy.Kind });
        return new ConnectionPutResult(copy, 200, null, []);
    }

    public bool Delete(string name, RunnerActor actor)
    {
        var removed = _copies.Delete(name);
        if (removed) Record(DeleteVerb, actor, name, AuditEvent.Ok, null);
        return removed;
    }

    public void RefuseRole(string verb, RunnerActor actor, string name) => Record(verb, actor, name, AuditEvent.Refused, new { status = 403 });

    private void Record(string verb, RunnerActor actor, string target, string outcome, object? detail) =>
        _audit.Record(new AuditEvent("person", actor.Id, verb, outcome, ViaKind: actor.ViaKind, ViaId: actor.ViaId, TargetKind: "connection", TargetId: target, Detail: detail is null ? null : JsonSerializer.Serialize(detail)));
}
