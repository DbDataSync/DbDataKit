using System.Text.Json.Nodes;
using DbDataKit.Runner;
using DbDataKit.Secrets;

namespace DbDataKit.Server;

/// <summary>What a screen sends to set up a development login: the Runner (a registered server's id or name), the channel, the project's connection, and the login the person typed.</summary>
public sealed record DevelopmentLoginBody(string? Runner, string? Channel, string? Connection, string? User, string? Password);

/// <summary>
/// The console puts the password of a development login a person typed on a Workspace screen into the secret store of a Runner, for one package, channel and connection slot. The password goes from the screen to the console
/// and from the console to the Runner (<c>PUT /api/v1/secrets/{name}</c>, as the signed-in person, so the Runner decides whether they may store it and audits them); the Workspace server is asked only which package the
/// project becomes, and no call to it carries the login. The password is stored under a name made of the package, the channel and the slot (<see cref="SecretOf"/>), so a connection the channel binds can name it, and a
/// login of one channel is never another channel's. The user is not a secret: it is checked and given back as plain text, for the connection's copy that names the password's secret. Any channel is accepted, production
/// too: the person stores a login for the channel they pick, and the Runner's roles decide whether they may.
/// Design: docs/authoring-metadata.md#how-a-screen-sends-a-development-login
/// </summary>
public sealed class ConsoleDevelopmentLogins(ConsoleCatalogRead projects, ConsoleRelay relay)
{
    /// <summary>The kind of the secret names a development login is stored under on a Runner.</summary>
    public const string Kind = "dev-login";

    /// <summary>The longest user name accepted; the Runner limits the password.</summary>
    public const int MaxUserChars = 256;

    /// <summary>The name a part (<c>password</c>) of the development login of a slot of a package in a channel is stored under: <c>dbdatakit:runner:dev-login:&lt;package&gt;.&lt;channel&gt;.&lt;slot&gt;:&lt;part&gt;</c>.</summary>
    /// <remarks>Package and channel names and slots hold no dot, so the three never run into each other.</remarks>
    public static SecretName SecretOf(string package, string channel, string slot, string part) => new("runner", Kind, $"{package}.{channel}.{slot}", part);

    /// <summary>
    /// Checks the request, asks the Workspace server for the project's package (a call that carries none of the login), then stores the password on the Runner. The answer names the secret stored, never its value, and
    /// gives the user back when one was sent: 200 when the password is stored; a refusal of the console (400, 404, 409) before anything is sent; a refusal of the Workspace server or the Runner with its own words.
    /// </summary>
    public async Task<CatalogReadAnswer> SetUpAsync(string workspace, string project, DevelopmentLoginBody body, CancellationToken ct)
    {
        if (ConsoleCatalogRead.SlotProblem(body.Connection) is { } bad) return CatalogReadAnswer.Error(400, bad.Replace("to read", "to set up", StringComparison.Ordinal));
        if (!PackageManifestValidator.NameRule.IsMatch(body.Channel ?? "")) return CatalogReadAnswer.Error(400, "Name the channel to set up: `channel` (lower-case letters, digits, - and _).");
        if (string.IsNullOrEmpty(body.Password) || body.Password.Length > RunnerSecretLimit)
            return CatalogReadAnswer.Error(400, $"Type the login's password: `password`, 1 to {RunnerSecretLimit} characters.");
        if (body.User is { } named && (named.Length is 0 or > MaxUserChars || named.Any(char.IsControl) || named.Trim() != named))
            return CatalogReadAnswer.Error(400, $"The user is a name of 1 to {MaxUserChars} characters, with no space around it.");
        if (string.IsNullOrWhiteSpace(body.Runner)) return CatalogReadAnswer.Error(400, "Name the Runner to set up: `runner`.");

        var (package, refused) = await projects.PackageAsync(workspace, project, ct);
        if (refused is not null) return refused;
        var (target, problem) = await projects.RunnerAsync(body.Runner, ct);
        if (problem is not null) return problem;

        var slot = ConnectionSlots.NameOf(body.Connection!);
        var name = SecretOf(package!, body.Channel!, slot, "password").ToString();
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

    /// <summary>The longest value a Runner stores (<c>RunnerSecretsApi.MaxValueChars</c>).</summary>
    private const int RunnerSecretLimit = DbDataKit.Runner.Api.RunnerSecretsApi.MaxValueChars;
}
