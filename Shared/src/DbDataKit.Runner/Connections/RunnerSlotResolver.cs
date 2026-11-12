using DbDataKit.Connections;
using DbDataKit.Connections.Http;
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
/// The connections come from the catalog: the runner's own directory of files, then the copies the console handed over (<see cref="StoredConnectionCatalog"/>, plan 27a IX23); the runner never reads the console's folder, and a copy's secrets resolve here like any other.
/// </summary>
public sealed class RunnerSlotResolver(DefinitionPublisher publisher, IConnectionCatalog catalog, ISecretResolver secrets, IRedactor? redactor = null, PostureChoices? posture = null) : ISlotResolver
{
    /// <summary>The scope of the posture choice <see cref="PostureSetting.ProductionBindingOnNonProduction"/> for a slot of a package in a channel: the one scope the bind-time confirmation (<c>RunnerPublishing</c>), the Bindings page and <c>package allow-production-binding</c> write, in <c>runner.posture_choice</c> (the same shape as the console's bindings: <c>bindings/&lt;section&gt;/&lt;slot&gt;/&lt;release channel&gt;</c>, the slot written <c>package/slot</c>).</summary>
    public static string ProductionBindingScope(string package, string slot, string channel) => $"bindings/runner/{package}/{slot}/{channel}";

    /// <summary>The definition this runner has under <paramref name="connection"/> (a file or a copy), or null when it has none or it does not load.</summary>
    public ConnectionDefinition? Known(string connection)
    {
        try { return catalog.Find(connection); }
        catch (InvalidDataException) { return null; }
    }

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
        RefuseProductionBindings(task, slots, bound);
        foreach (var slot in slots) handles[slot.Name] = bound[slot.Name];
        return handles;
    }

    /// <summary>
    /// Plan 27a IX17, at the moment a run starts (so a binding written by hand is held to it as much as one made through the API): a task in a channel other than production does not get the connection that production's channel binds the same slot to,
    /// because what it sends or writes would reach production's far side, unless an administrator chose <see cref="PostureSetting.ProductionBindingOnNonProduction"/> for that slot and channel (confirmed, recorded with who and when, audited). This is the backstop of the rule the bind refuses first (409 until confirmed): the same record, the same scope, no second mechanism. A slot with no binding in the channel is refused by <see cref="Handles"/> already: nothing is inherited from production.
    /// </summary>
    private void RefuseProductionBindings(TaskRef task, IReadOnlyList<ConnectionSlot> slots, IReadOnlyDictionary<string, string> bound)
    {
        if (posture is null || task.Channel == DefinitionStore.Production) return;
        var production = publisher.Bindings(DefinitionStore.Production, PackageActivator.Kind, task.Package).ToDictionary(b => b.Slot, b => b.BoundTo, StringComparer.Ordinal);
        foreach (var slot in slots)
        {
            if (!production.TryGetValue(slot.Name, out var prod) || !string.Equals(prod, bound[slot.Name], StringComparison.Ordinal)) continue;
            if (posture.IsChosen(ProductionBindingScope(task.Package, slot.Name, task.Channel), PostureSetting.ProductionBindingOnNonProduction.Name)) continue;
            throw new SlotResolutionException($"The task {task} would use the connection '{prod}' for the slot '{slot.Name}' in channel '{task.Channel}', and that is the connection production uses for it: what the task sends or writes would reach production's far side. "
                + $"Bind the slot in '{task.Channel}' to a connection of its own (a sandbox), or have an administrator confirm it (package allow-production-binding {task.Package} {slot.Name} {task.Channel} --confirm); the choice is recorded with who and when.");
        }
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
        if (definition.KindName == ConnectionKinds.Http) return await ResolveHttpAsync(slot, connection, role, definition, ct).ConfigureAwait(false);
        if (definition.KindName != ConnectionKinds.Database)
            throw new SlotResolutionException($"The connection '{connection}' bound to the slot '{slot}' is of kind '{definition.KindName}', which a runner slot does not take (a database or an http connection).");
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

    /// <summary>
    /// An http connection (a copy the console sent, plan 27a IX23): the address as the file says it (a template whose secret parts stay out of the grant) and the sign-in method, with every secret of it resolved <b>here</b>, in this runner's store. In the grant,
    /// <c>AuthMode</c> is the method (<c>api_key</c>, <c>basic</c>, <c>oauth2_client_credentials</c>, <c>client_certificate</c>, <c>none</c>), <c>Token</c> the API key or the client secret, <c>Password</c> the password of <c>basic</c>, <c>User</c> its user or the client id.
    /// A task that wants a ready client asks <see cref="OpenHttp"/>: <c>HttpConnections.Create</c> over this runner's secrets.
    /// </summary>
    private async Task<ConnectionGrant> ResolveHttpAsync(string slot, string connection, string role, ConnectionDefinition definition, CancellationToken ct)
    {
        var sign = definition.SignIn;
        var address = definition.Endpoint is { } endpoint && !EndpointTemplate.IsHidden(endpoint) && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri : null;
        string? key = null, password = null;
        if (sign is not null)
        {
            key = await RevealAsync(slot, connection, sign.KeySecret ?? sign.ClientSecretSecret, ct).ConfigureAwait(false);
            password = await RevealAsync(slot, connection, sign.PasswordSecret, ct).ConfigureAwait(false);
            await RevealAsync(slot, connection, sign.CertificateSecret, ct).ConfigureAwait(false);
            await RevealAsync(slot, connection, sign.CertificatePasswordSecret, ct).ConfigureAwait(false);
        }

        // every reference of the definition, resolved here and handed over by the reference as written: the task signs in inside its own process (OAuth, a client certificate, an API key in a query) and the manifest and the log never see a value
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (_, reference) in ConnectionSecretReferences.Of(definition))
        {
            var parsed = SecretRef.TryParse(reference, out var r) ? r : null;
            if (parsed is null || await RevealAsync(slot, connection, reference, ct).ConfigureAwait(false) is not { } value) continue;
            values[parsed.ToString()] = value;
        }

        return new ConnectionGrant
        {
            Slot = slot,
            Connection = connection,
            Engine = ConnectionKinds.Http,
            Role = role,
            Host = address?.Host,
            Port = address is null ? null : address.Port,
            ConnectionString = address?.ToString(),
            ReadOnly = false,
            AuthMode = sign?.Method ?? "none",
            User = sign?.User ?? sign?.ClientId,
            Password = password,
            Token = key,
            HttpDefinition = catalog.Text(connection),
            HttpSecrets = values,
        };
    }

    /// <summary>A client of the http connection bound to a slot, made by <c>DbDataKit.Connections.Http</c> from the runner's copy of the definition and signed in as it says, over this runner's secrets. The caller disposes it.</summary>
    public HttpConnectionClient OpenHttp(string slot, string connection, HttpConnectionOptions? options = null)
    {
        var definition = Definition(slot, connection);
        if (definition.KindName != ConnectionKinds.Http) throw new SlotResolutionException($"The connection '{connection}' bound to the slot '{slot}' is of kind '{definition.KindName}', not http.");
        try { return HttpConnections.Create(definition, secrets, options); }
        catch (SecretNotFoundException ex) { throw new SlotResolutionException($"The login of the connection '{connection}' bound to the slot '{slot}' is not in this runner's secret store: {ex.Message}", ex); }
    }

    /// <summary>The secret references of the connection bound to a slot (a name the runner knows, from a file or a copy) and whether each resolves <b>on this runner</b>; the value is dropped at once. A name this runner does not know has none.</summary>
    public async Task<IReadOnlyList<SecretCheck>> CheckReferencesAsync(ConnectionDefinition definition, CancellationToken ct)
    {
        var list = new List<SecretCheck>();
        foreach (var (path, reference) in ConnectionSecretReferences.Of(definition))
        {
            try
            {
                using var value = await secrets.ResolveAsync(SecretRef.Parse(reference), ct).ConfigureAwait(false);
                list.Add(new SecretCheck(path, reference, true, null));
            }
            catch (SecretNotFoundException) { list.Add(new SecretCheck(path, reference, false, $"The secret '{reference}' is not in this runner's secret store, the environment or the credentials directory. Send it with `secret send --to <this runner>`, or set it there.")); }
            catch (Exception ex) when (ex is ArgumentException or FormatException) { list.Add(new SecretCheck(path, reference, false, $"'{reference}' is not a secret reference this runner can parse.")); }
        }

        return list;
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

/// <summary>One secret reference of a connection and whether it resolves on this runner; <see cref="Problem"/> says what to do when it does not. Never a value.</summary>
public sealed record SecretCheck(string Path, string Reference, bool Resolves, string? Problem);
