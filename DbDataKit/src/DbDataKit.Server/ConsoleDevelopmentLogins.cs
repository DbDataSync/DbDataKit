using System.Text.Json.Nodes;
using DbDataKit.Deploy;
using DbDataKit.Runner;
using DbDataKit.Secrets;
using DbDataKit.Web.Trust;

namespace DbDataKit.Server;

/// <summary>
/// What a screen sends to set up a development login: the Runner (a registered server's id or name), the channel, the project's connection, the login the person typed, and the draft (a workspace of the project) whose connection file
/// the copy is made from. Without a draft only the password is stored.
/// </summary>
public sealed record DevelopmentLoginBody(string? Runner, string? Channel, string? Connection, string? User, string? Password, string? Draft = null);

/// <summary>
/// The console sets up a project's connection for development from a login a person typed on a Workspace screen, for one package, channel and connection slot of a Runner. The password goes from the screen to the console and from
/// the console to the Runner's secret store, as the signed-in person, so the Runner decides whether they may store it and audits them. It is stored under a name made of the package, the channel and the slot (<see cref="SecretOf"/>), so
/// a login of one channel is never another channel's. The user is not a secret: it is checked and given back as plain text.
/// <para>
/// When the request names the draft, the console then reads the draft's connection file from the Workspace server as the person (a call that carries none of the login), writes the copy (<see cref="DevelopmentCopy"/>: the user as plain
/// text, the password a reference to the stored secret), puts it on the Runner and binds the slot in the channel (<see cref="ConsoleChannelConnections"/>), so reads, Test and runs in that channel use the login. Any channel is accepted,
/// production too: the Runner's roles decide whether the person may.
/// </para>
/// Design: docs/authoring-metadata.md#how-a-screen-sends-a-development-login
/// </summary>
public sealed class ConsoleDevelopmentLogins(ConsoleCatalogRead projects, ConsoleRelay relay, ConsoleChannelConnections channels, ConsoleInstances instances)
{
    /// <summary>The kind of the secret names a development login is stored under on a Runner.</summary>
    public const string Kind = "dev-login";

    /// <summary>The longest user name accepted; the Runner limits the password.</summary>
    public const int MaxUserChars = 256;

    /// <summary>The name a part (<c>password</c>) of the development login of a slot of a package in a channel is stored under: <c>dbdatakit:runner:dev-login:&lt;package&gt;.&lt;channel&gt;.&lt;slot&gt;:&lt;part&gt;</c>.</summary>
    /// <remarks>Package and channel names and slots hold no dot, so the three never run into each other.</remarks>
    public static SecretName SecretOf(string package, string channel, string slot, string part) => new("runner", Kind, $"{package}.{channel}.{slot}", part);

    /// <summary>
    /// Checks the request, asks the Workspace server for the project's package (a call that carries none of the login), then stores the password on the Runner, or, with a draft, sets the connection up there
    /// (<see cref="SetUpFromDraftAsync"/>). The answer names the secret stored, never its value, and gives the user back when one was sent: 200 when the password is stored; a refusal of the console (400, 404, 409, 422) before
    /// anything is sent; a refusal of the Workspace server or the Runner with its own words.
    /// </summary>
    public async Task<CatalogReadAnswer> SetUpAsync(string workspace, string project, DevelopmentLoginBody body, RelayPerson person, CancellationToken ct)
    {
        if (ConsoleCatalogRead.SlotProblem(body.Connection) is { } bad) return CatalogReadAnswer.Error(400, bad.Replace("to read", "to set up", StringComparison.Ordinal));
        if (!PackageManifestValidator.NameRule.IsMatch(body.Channel ?? "")) return CatalogReadAnswer.Error(400, "Name the channel to set up: `channel` (lower-case letters, digits, - and _).");
        if (string.IsNullOrEmpty(body.Password) || body.Password.Length > RunnerSecretLimit)
            return CatalogReadAnswer.Error(400, $"Type the login's password: `password`, 1 to {RunnerSecretLimit} characters.");
        if (body.User is { } named && (named.Length is 0 or > MaxUserChars || named.Any(char.IsControl) || named.Trim() != named))
            return CatalogReadAnswer.Error(400, $"The user is a name of 1 to {MaxUserChars} characters, with no space around it.");
        if (string.IsNullOrWhiteSpace(body.Runner)) return CatalogReadAnswer.Error(400, "Name the Runner to set up: `runner`.");
        if (body.Draft is { } draftId && (draftId.Length is 0 or > 128 || draftId.Any(c => char.IsControl(c) || c is '/' or '\\')))
            return CatalogReadAnswer.Error(400, "Name the draft whose connection is set up: `draft`, a workspace of the project.");

        var (package, refused) = await projects.PackageAsync(workspace, project, ct);
        if (refused is not null) return refused;
        var (target, problem) = await projects.RunnerAsync(body.Runner, ct);
        if (problem is not null) return problem;

        var slot = ConnectionSlots.NameOf(body.Connection!);
        var name = SecretOf(package!, body.Channel!, slot, "password").ToString();
        if (body.Draft is { } draft) return await SetUpFromDraftAsync(workspace, project, package!, slot, name, draft, body, target!, person, ct);
        var (status, answer) = await relay.CallAsync(target!, HttpMethod.Put, $"/api/v1/secrets/{Uri.EscapeDataString(name)}", new JsonObject { ["value"] = body.Password }, ct);
        if (status != 200) return ConsoleCatalogRead.Passed(status, answer, $"{target!.Name} did not store the login.");

        var stored = new JsonObject
        {
            ["package"] = package, ["connection"] = body.Connection, ["slot"] = slot, ["channel"] = body.Channel, ["production"] = body.Channel == ConsoleCatalogRead.Production,
            ["runner"] = target!.InstanceId, ["runner_name"] = target.Name, ["secrets"] = new JsonObject { ["password"] = name },
        };
        if (body.User is { } user) stored["user"] = user;
        return new(200, stored);
    }

    /// <summary>
    /// The whole set-up from a draft: the draft's connection file read from the Workspace server as the person, the copy written from it, then the login stored, the copy put and the slot bound on the Runner
    /// (<see cref="ConsoleChannelConnections.SetUpAsync"/>). The answer is the stored login's answer with the copy, the bind and the steps; 409 with <c>needsConfirmation</c> when the Runner holds the bind for an administrator's
    /// confirmation.
    /// </summary>
    private async Task<CatalogReadAnswer> SetUpFromDraftAsync(string workspace, string project, string package, string slot, string secret, string draft, DevelopmentLoginBody body, RegisteredInstance target, RelayPerson person, CancellationToken ct)
    {
        var (file, text, unread) = await ConnectionFileAsync(workspace, project, draft, body.Connection!, ct);
        if (unread is not null) return unread;
        var copyName = DevelopmentCopy.NameOf(package, slot, body.Channel!);
        var copy = DevelopmentCopy.Write(text!, file!, copyName, body.User, secret);
        if (copy.Error is { } bad) return CatalogReadAnswer.Error(copy.Status, bad);

        var request = new ChannelConnectionSetUp(package, body.Channel!, slot, copyName, copy.Text!, new ChannelLogin(secret, body.Password!));
        var answer = await channels.SetUpAsync(target.InstanceId, request, person, ct);
        if (answer.Status is not (200 or 409)) return answer;
        answer.Body["connection"] = body.Connection;
        answer.Body["copy"] = copyName;
        answer.Body["file"] = file;
        answer.Body["production"] = body.Channel == ConsoleCatalogRead.Production;
        answer.Body["user"] = copy.User;
        answer.Body["secrets"] = new JsonObject { ["password"] = secret };
        if (answer.Body["steps"] is JsonArray steps) foreach (var warning in copy.Warnings) steps.Add(warning);
        return answer;
    }

    /// <summary>
    /// The draft's file of <paramref name="connection"/> (<c>connections/&lt;name&gt;.yaml</c> of a Build or Store project, <c>config/connections/&lt;name&gt;.yaml</c> of a Sync set, <c>.yml</c> too) and its text, read from the
    /// Workspace server as the signed-in person; the Workspace server decides whether they may read the draft. The calls carry no part of the login.
    /// </summary>
    private async Task<(string? File, string? Text, CatalogReadAnswer? Error)> ConnectionFileAsync(string workspace, string project, string draft, string connection, CancellationToken ct)
    {
        var server = instances.Registry.Find(workspace)!;
        var root = $"/api/v1/projects/{Uri.EscapeDataString(project)}/workspaces/{Uri.EscapeDataString(draft)}/files";
        var (listed, files) = await relay.CallAsync(server, HttpMethod.Get, root, null, ct);
        if (listed == 404) return (null, null, CatalogReadAnswer.Error(404, $"{server.Name} has no draft '{draft}' of the project '{project}'."));
        if (listed != 200) return (null, null, ConsoleCatalogRead.Passed(listed, files, $"{server.Name} did not list the draft's files."));
        string[] wanted = [.. new[] { "connections/", "config/connections/" }.SelectMany(d => new[] { d + connection + ".yaml", d + connection + ".yml" })];
        var paths = (files?["files"] as JsonArray ?? []).OfType<JsonObject>().Select(f => (string?)f["path"]).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (wanted.FirstOrDefault(paths.Contains) is not { } path)
            return (null, null, CatalogReadAnswer.Error(404, $"The draft has no file of the connection '{connection}' ({wanted[0]} or {wanted[2]}): save the connection's file first."));
        var (read, file) = await relay.CallAsync(server, HttpMethod.Get, root + "/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString)), null, ct);
        if (read != 200 || (string?)file?["content"] is not { } text) return (null, null, ConsoleCatalogRead.Passed(read, file, $"{server.Name} did not give the draft's {path}."));
        return (path, text, null);
    }

    /// <summary>The longest value a Runner stores (<c>RunnerSecretsApi.MaxValueChars</c>).</summary>
    private const int RunnerSecretLimit = DbDataKit.Runner.Api.RunnerSecretsApi.MaxValueChars;
}
