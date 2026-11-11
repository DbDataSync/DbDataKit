using DbDataKit.Connections;
using DbDataKit.Runner.Kit;
using DbDataKit.Runner.Protocol.Server;
using DbDataKit.Runner.Staging;
using DbDataKit.Secrets;
using DbDataKit.State;

namespace DbDataKit.Runner.Connections;

/// <summary>
/// Resolves connection slots on the runner (plan 28c section 6, E02). A slot of a manifest is bound <b>per channel</b> by <c>definition_binding</c> to the name of a <c>connection/1</c> (<see cref="IConnectionCatalog"/>) whose
/// secrets are references into the runner's secret store (<see cref="ISecretResolver"/>): a package never holds a value. Three uses:
/// <list type="bullet">
/// <item><see cref="Handles"/>, when a run starts: slot to connection name from the task's own channel (never another channel's binding); a slot with no binding is refused naming the slot and the channel.</item>
/// <item><see cref="EnvironmentAsync"/>, for the slots that declare an injection form: the variables of <see cref="SlotEnvironmentFormats"/> (<c>sync-secret</c>: <c>DBDATAKIT_SECRET_&lt;NAME&gt;</c>; <c>build-login</c>:
/// <c>DBDATAKIT_BUILD_&lt;CONNECTION&gt;_READ|WRITE</c>), written by the runner into the child's environment and nowhere else. Every value is registered with the redactor.</item>
/// <item><see cref="ResolveAsync"/>, for a task that asks through the protocol: a <see cref="ConnectionGrant"/> for one role. A read request never gets the write login, and a write request on a read-only connection is refused.</item>
/// </list>
/// A secret that is not in the store is a <see cref="SlotResolutionException"/> naming the slot, the connection and the reference, never a fallback to another role.
/// </summary>
public sealed class RunnerSlotResolver(DefinitionPublisher publisher, IConnectionCatalog catalog, ISecretResolver secrets, IRedactor? redactor = null) : ISlotResolver
{
    public const string SyncSecretPrefix = "dbdatakit:sync:connection:";

    /// <summary>The part of Sync's secret name for the run login of a connection that has a second one (<c>dbdatakit:sync:connection:&lt;name&gt;:run</c>, plan 28c E20).</summary>
    public const string SyncRunPart = "run";

    public IReadOnlyDictionary<string, string> Handles(TaskRef task, IReadOnlyList<ConnectionSlot> slots)
    {
        var handles = new Dictionary<string, string>(StringComparer.Ordinal);
        if (slots.Count == 0) return handles;
        var bound = publisher.Bindings(task.Channel, PackageActivator.Kind, task.Package).ToDictionary(b => b.Slot, b => b.BoundTo, StringComparer.Ordinal);
        var unbound = slots.Where(s => !bound.ContainsKey(s.Name)).Select(s => $"'{s.Name}'").ToList();
        if (unbound.Count > 0)
            throw new SlotResolutionException($"The task {task} needs the connection {(unbound.Count == 1 ? "slot" : "slots")} {string.Join(", ", unbound)} bound in channel '{task.Channel}', and {(unbound.Count == 1 ? "it has" : "they have")} no binding there.");
        foreach (var slot in slots) handles[slot.Name] = bound[slot.Name];
        return handles;
    }

    public async Task<IReadOnlyDictionary<string, string>> EnvironmentAsync(IReadOnlyDictionary<string, string> handles, IReadOnlyList<ConnectionSlot> slots, CancellationToken ct)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var slot in slots)
        {
            if (slot.Env is null) continue;
            if (!handles.TryGetValue(slot.Name, out var connection)) throw new SlotResolutionException($"The slot '{slot.Name}' has no binding, so its login cannot be put in the task's environment.");
            switch (slot.Env)
            {
                case SlotEnvironmentFormats.SyncSecret:
                    {
                        var value = await PrimarySecretAsync(slot.Name, connection, LoginRole.Default, ct).ConfigureAwait(false)
                            ?? throw new SlotResolutionException($"The connection '{connection}' bound to the slot '{slot.Name}' has no login to put in the environment.");
                        // The login the runner holds for the connection is its run login (Sync keeps the read login to itself, and it is never on a runner). A Sync connection with one login reads its secret under the plain name, one with a second
                        // login under the run name (E20); the runner does not know which, so the same value is given under both names. The read secret has no name here at all.
                        variables[EnvSecretProvider.VariableNameFor(SyncSecretPrefix + connection)] = value;
                        variables[EnvSecretProvider.VariableNameFor($"{SyncSecretPrefix}{connection}:{SyncRunPart}")] = value;
                        break;
                    }

                case SlotEnvironmentFormats.BuildLogin:
                    foreach (var role in new[] { LoginRole.Read, LoginRole.Write })
                    {
                        // a role the connection has no login for gets no variable; a login whose secret is missing is an error, never the other role's login
                        if (role == LoginRole.Write && Definition(slot.Name, connection).Access == AccessMode.Readonly) continue;
                        if (await PrimarySecretAsync(slot.Name, connection, role, ct).ConfigureAwait(false) is { } value)
                            variables[LoginEnvironment.Name("build", connection, role)] = value;
                    }

                    break;
                default:
                    throw new SlotResolutionException($"The slot '{slot.Name}' names the injection form '{slot.Env}', which this runner does not know.");
            }
        }

        return variables;
    }

    public async Task<ConnectionGrant> ResolveAsync(string slot, string connection, string role, CancellationToken ct)
    {
        var definition = Definition(slot, connection);
        var loginRole = role switch { "read" => LoginRole.Read, "write" => LoginRole.Write, _ => LoginRole.Default };
        if (loginRole == LoginRole.Write && definition.Access == AccessMode.Readonly)
            throw new SlotResolutionException($"A write login was asked for the slot '{slot}', and the connection '{connection}' it is bound to is read-only.");
        var spec = definition.LoginFor(loginRole);
        string? user = null, password = null, whole = null, token = null;
        var mode = spec?.Mode ?? AuthMode.None;
        if (spec is not null)
        {
            user = spec.User;
            password = await RevealAsync(slot, connection, spec.PasswordSecret, ct).ConfigureAwait(false);
            whole = await RevealAsync(slot, connection, spec.ConnectionStringSecret, ct).ConfigureAwait(false);
            token = await RevealAsync(slot, connection, spec.TokenSecret, ct).ConfigureAwait(false);
        }

        whole ??= spec is null ? await RevealAsync(slot, connection, definition.ConnectionStringSecret, ct).ConfigureAwait(false) : null;
        if (spec is null && whole is null && loginRole != LoginRole.Default)
            throw new SlotResolutionException($"The connection '{connection}' bound to the slot '{slot}' has no {role} login; a {role} request does not fall back to another role.");
        return new ConnectionGrant
        {
            Slot = slot,
            Connection = connection,
            Engine = definition.Engine,
            Role = role,
            Host = definition.Address?.Host,
            Port = definition.Address?.Port,
            Database = definition.Address?.Database,
            ConnectionString = definition.ConnectionString,
            ReadOnly = definition.Access == AccessMode.Readonly,
            AuthMode = mode.ToString().ToLowerInvariant(),
            User = user,
            Password = password,
            ConnectionStringSecret = whole,
            Token = token,
        };
    }

    private ConnectionDefinition Definition(string slot, string connection)
    {
        try
        {
            return catalog.Find(connection) ?? throw new SlotResolutionException($"The slot '{slot}' is bound to the connection '{connection}', which this runner does not know (no connection/1 of that name).");
        }
        catch (InvalidDataException ex)
        {
            throw new SlotResolutionException($"The connection '{connection}' bound to the slot '{slot}' cannot be read: {ex.Message}", ex);
        }
    }

    /// <summary>The login's one secret as the engines read it: the whole connection string, else the password, else the token; null when the connection has no login for the role.</summary>
    private async Task<string?> PrimarySecretAsync(string slot, string connection, LoginRole role, CancellationToken ct)
    {
        var definition = Definition(slot, connection);
        var spec = definition.LoginFor(role);
        var reference = spec?.ConnectionStringSecret ?? spec?.PasswordSecret ?? spec?.TokenSecret ?? (spec is null && role != LoginRole.Read ? definition.ConnectionStringSecret : null);
        return await RevealAsync(slot, connection, reference, ct).ConfigureAwait(false);
    }

    private async Task<string?> RevealAsync(string slot, string connection, string? reference, CancellationToken ct)
    {
        if (reference is null) return null;
        SecretRef parsed;
        try
        {
            parsed = SecretRef.Parse(reference);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new SlotResolutionException($"The login of the connection '{connection}' bound to the slot '{slot}' names a secret reference that does not parse: {ex.Message}", ex);
        }

        try
        {
            using var value = await secrets.ResolveAsync(parsed, ct).ConfigureAwait(false);
            redactor?.Register(value);
            return value.RevealString();
        }
        catch (SecretNotFoundException ex)
        {
            throw new SlotResolutionException($"The login of the connection '{connection}' bound to the slot '{slot}' is not in this runner's secret store: {ex.Message}", ex);
        }
    }
}
