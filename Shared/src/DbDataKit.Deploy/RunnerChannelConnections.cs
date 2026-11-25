using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataKit.Runner;
using DbDataKit.Runner.Client;
using DbDataKit.State;

namespace DbDataKit.Deploy;

/// <summary>A secret reference of a connection's copy, as the Runner checked it: where it is in the definition, the reference, whether it resolves on the Runner and, when it does not, why (never a value).</summary>
public sealed record CopySecret(string Path, string Reference, bool Resolves, string? Problem);

/// <summary>A copy of a connection the Runner keeps: its name, the version both sides compute (<c>ConnectionCopyVersion</c>), its kind and the secret references it names.</summary>
public sealed record ConnectionCopyPut(string Name, string Version, string Kind, IReadOnlyList<CopySecret> Secrets)
{
    /// <summary>The references the Runner cannot resolve; a run that uses the copy fails on the first of them.</summary>
    public IReadOnlyList<CopySecret> Unresolved => [.. Secrets.Where(s => !s.Resolves)];
}

/// <summary>
/// What a bind of a channel that uses production's connection needs an administrator to confirm (the Runner's 409 with <c>needsConfirmation</c>): the scope the confirmation names, the slot, the channel, the connection and the effect, in the
/// Runner's words.
/// </summary>
public sealed record BindingConfirmation(string Scope, string Slot, string Channel, string Connection, string Effect);

/// <summary>The bind of one slot: bound, with what the slot is now bound to, or refused until the <see cref="Needs"/> are confirmed (<see cref="Error"/> is the Runner's sentence).</summary>
public sealed record ChannelSlotBind(string Package, string Channel, string Slot, string Connection, bool Bound, IReadOnlyList<BindingConfirmation> Needs, string? Error);

/// <summary>A login sent to the Runner's secret store: the secret's name and its value. The value is in no message and no <see cref="ToString"/>.</summary>
public sealed record ChannelLogin(string Secret, string Value)
{
    public override string ToString() => $"ChannelLogin {{ Secret = {Secret} }}";
}

/// <summary>
/// One connection set up for a slot of a package in a channel of a Runner: the <see cref="Login"/> to store first (optional: a secret the Runner holds already is named by the definition alone), the copy (<see cref="Connection"/>, the
/// connection's name on the Runner, and its <see cref="Definition"/>, with secret references and never a value), then the bind of <see cref="Slot"/>. <see cref="Confirm"/> names the scopes an administrator confirmed when the channel shares
/// production's connection, and <see cref="Note"/> is kept with that confirmation.
/// </summary>
public sealed record ChannelConnectionSetUp(string Package, string Channel, string Slot, string Connection, string Definition, ChannelLogin? Login = null, IReadOnlyCollection<string>? Confirm = null, string? Note = null);

/// <summary>What <see cref="RunnerChannelConnections.SetUpAsync"/> did: the secret stored (its name), the copy the Runner keeps, the bind, and the steps in words.</summary>
public sealed record ChannelConnectionResult(string? LoginSecret, ConnectionCopyPut Copy, ChannelSlotBind Bind, IReadOnlyList<string> Steps);

/// <summary>
/// Puts a connection on a Runner for a channel, through a link the caller gives: a control service passes the link it makes as the signed-in person, so the Runner checks that person's roles and records them in its audit. Three of the
/// Runner's own calls, each usable alone:
/// <list type="bullet">
/// <item><see cref="SendLoginAsync"/> stores a secret's value in the Runner's secret store (<c>PUT /api/v1/secrets/{name}</c>, the Runner's admin role);</item>
/// <item><see cref="PutCopyAsync"/> puts a copy of a connection's definition, with its secret references, under a name (<c>PUT /api/v1/connection-copies/{name}</c>, admin role); the Runner checks the text with the shared loader and
/// says which references resolve there;</item>
/// <item><see cref="BindAsync"/> binds one slot of a package in a channel to that name (<c>PUT /api/v1/bindings/{channel}/integration/{package}</c>, admin or the package's publisher). A channel that would share production's connection is
/// refused by the Runner until an administrator confirms it, and the answer carries what to confirm.</item>
/// </list>
/// <see cref="SetUpAsync"/> does the three in that order, so the copy's references resolve when the Runner checks them and the slot is bound to a copy that is there. The link carries each call; this class holds no key and no value
/// after a call. A Runner's refusal is a <see cref="DeployException"/> in the Runner's words.
/// Design: docs/channel-bindings.md#the-runner-what-it-keeps-and-how-the-console-hands-it-a-connection
/// </summary>
public sealed class RunnerChannelConnections
{
    private readonly IDefinitionsLink link;
    private readonly DeployTarget target;

    public RunnerChannelConnections(IDefinitionsLink link, DeployTarget target)
    {
        RunnerCalls.Require(target);
        (this.link, this.target) = (link, target);
    }

    /// <summary>The Runner the calls go to.</summary>
    public DeployTarget Target => target;

    /// <summary>Stores <paramref name="login"/>'s value under its secret's name in the Runner's secret store. The answer is the name the Runner stored it under.</summary>
    public async Task<string> SendLoginAsync(ChannelLogin login, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(login.Secret)) throw new DeployException("Name the secret the login is stored under.", 400);
        if (string.IsNullOrEmpty(login.Value)) throw new DeployException($"The login for {login.Secret} has no value to store.", 400);
        try { return await RunnerPublishClient.ForLink(link).SendSecretAsync(target.Name, null, login.Secret, login.Value, null, ct); }
        catch (RunnerClientException ex) { throw RunnerCalls.Refused(ex); }
    }

    /// <summary>
    /// Puts the copy <paramref name="name"/> of a connection on the Runner, with <paramref name="definition"/> as its text (secret references only). The same text again changes nothing on the Runner; another text replaces the copy, and the
    /// version in the answer says which text it keeps.
    /// </summary>
    public async Task<ConnectionCopyPut> PutCopyAsync(string name, string definition, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DeployException("Name the connection to put on the Runner.", 400);
        if (string.IsNullOrWhiteSpace(definition)) throw new DeployException($"The connection '{name}' has no definition to put on {target.Name}.", 400);
        var (status, body) = await SendAsync(HttpMethod.Put, $"connection-copies/{Uri.EscapeDataString(name)}", new JsonObject { ["definition"] = definition }, ct);
        if (status != 200 || body is not JsonObject copy)
        {
            var lines = body?["diagnostics"] is JsonArray d ? d.Select(x => x?.GetValue<string>()).OfType<string>().ToList() : [];
            throw new DeployException(ErrorOf(body, $"{target.Name} did not keep the connection '{name}'") + (lines.Count > 0 ? " " + string.Join(" ", lines) : ""), StatusOf(status));
        }
        var secrets = (copy["secrets"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(s => new CopySecret((string?)s["path"] ?? "", (string?)s["reference"] ?? "", (bool?)s["resolves"] ?? false, (string?)s["problem"])).ToList();
        return new ConnectionCopyPut((string?)copy["name"] ?? name, (string?)copy["version"] ?? "", (string?)copy["kind"] ?? "", secrets);
    }

    /// <summary>
    /// Binds <paramref name="slot"/> of <paramref name="package"/> in <paramref name="channel"/> to <paramref name="connection"/> (a name the Runner knows: a copy, or a connection file of its own). A bind the Runner holds for a
    /// confirmation answers <c>Bound: false</c> with the <see cref="ChannelSlotBind.Needs"/>; send it again with their scopes in <paramref name="confirm"/>.
    /// </summary>
    public async Task<ChannelSlotBind> BindAsync(string package, string channel, string slot, string connection, IReadOnlyCollection<string>? confirm = null, string? note = null, CancellationToken ct = default)
    {
        if (!PackageManifestValidator.NameRule.IsMatch(package ?? "")) throw new DeployException($"'{package}' is not a package name (lower-case letters, digits, - and _).", 400);
        if (!PackageManifestValidator.NameRule.IsMatch(channel ?? "")) throw new DeployException($"'{channel}' is not a channel name (lower-case letters, digits, - and _).", 400);
        if (string.IsNullOrWhiteSpace(slot)) throw new DeployException("Name the slot to bind.", 400);
        if (string.IsNullOrWhiteSpace(connection)) throw new DeployException($"Name the connection to bind the slot '{slot}' to.", 400);
        var request = new JsonObject { ["slots"] = new JsonObject { [slot] = connection } };
        if (confirm is { Count: > 0 }) request["confirm"] = new JsonArray([.. confirm.Select(c => (JsonNode?)c)]);
        if (!string.IsNullOrWhiteSpace(note)) request["note"] = note;
        var (status, body) = await SendAsync(HttpMethod.Put, $"bindings/{Uri.EscapeDataString(channel!)}/integration/{Uri.EscapeDataString(package!)}", request, ct);
        if (status == 409 && body?["needsConfirmation"] is JsonArray needs)
        {
            var list = needs.OfType<JsonObject>().Select(n => new BindingConfirmation((string?)n["scope"] ?? "", (string?)n["slot"] ?? slot, (string?)n["release"] ?? channel!, (string?)n["connection"] ?? connection, (string?)n["effect"] ?? "")).ToList();
            return new ChannelSlotBind(package!, channel!, slot, connection, false, list, ErrorOf(body, $"{target.Name} asks an administrator to confirm the bind of '{slot}' in {channel}"));
        }
        if (status != 200) throw new DeployException(ErrorOf(body, $"{target.Name} did not bind the slot '{slot}' of {package} in {channel}"), StatusOf(status));
        var boundTo = (body?["bindings"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(b => (string?)b["slot"] == slot)?["bound_to"]?.GetValue<string>() ?? connection;
        return new ChannelSlotBind(package!, channel!, slot, boundTo, true, [], null);
    }

    /// <summary>
    /// Sets up one connection for a slot in a channel: stores the login when one is given, puts the copy, then binds the slot. <paramref name="step"/> hears each step in words, and a warning when the copy names a reference the Runner
    /// cannot resolve or does not name the login's secret. A refusal stops at the step that was refused; what was done before it stays (a login stored, a copy kept), and the same call again finishes it.
    /// </summary>
    public async Task<ChannelConnectionResult> SetUpAsync(ChannelConnectionSetUp request, Action<string>? step = null, CancellationToken ct = default)
    {
        var steps = new List<string>();
        void Say(string line) { steps.Add(line); step?.Invoke(line); }

        string? stored = null;
        if (request.Login is { } login)
        {
            stored = await SendLoginAsync(login, ct);
            Say($"Stored the login in {target.Name}'s secret store as {stored}.");
            if (!request.Definition.Contains("secret:" + stored, StringComparison.Ordinal))
                Say($"Warning: the definition of '{request.Connection}' does not name secret:{stored}, so the login just stored is not used by this copy.");
        }

        var copy = await PutCopyAsync(request.Connection, request.Definition, ct);
        Say($"{target.Name} keeps the connection '{copy.Name}' ({copy.Kind}, version {copy.Version}).");
        foreach (var missing in copy.Unresolved)
            Say($"Warning: {target.Name} cannot resolve {missing.Reference} ({missing.Path}){(missing.Problem is { Length: > 0 } why ? ": " + why : "")}; a run that uses '{copy.Name}' fails until it does.");

        var bind = await BindAsync(request.Package, request.Channel, request.Slot, copy.Name, request.Confirm, request.Note, ct);
        Say(bind.Bound
            ? $"Bound the slot '{bind.Slot}' of {bind.Package} in {bind.Channel} to '{bind.Connection}'."
            : $"{target.Name} did not bind the slot '{bind.Slot}' of {bind.Package} in {bind.Channel}: {bind.Error}");
        return new ChannelConnectionResult(stored, copy, bind, steps);
    }

    private async Task<(int Status, JsonNode? Body)> SendAsync(HttpMethod method, string path, JsonObject body, CancellationToken ct)
    {
        try
        {
            using var content = JsonContent.Create(body);
            using var response = await link.SendAsync(method, path, content, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            JsonNode? json = null;
            if (text.Length > 0)
            {
                try { json = JsonNode.Parse(text); }
                catch (System.Text.Json.JsonException) { json = new JsonObject { ["error"] = text.Length > 300 ? text[..300] : text }; }
            }
            return ((int)response.StatusCode, json);
        }
        catch (HttpRequestException ex) { throw new DeployException($"{target.Name} could not be reached ({ex.Message}).", 502); }
    }

    private static string ErrorOf(JsonNode? body, string otherwise) => (body as JsonObject)?["error"] is JsonValue v && v.TryGetValue<string>(out var error) ? error : otherwise + ".";

    /// <summary>The status a refusal is answered with: the Runner's own for a refusal of the request (400 to 499), 502 for anything else.</summary>
    private static int StatusOf(int status) => status is >= 400 and < 500 ? status : 502;
}
