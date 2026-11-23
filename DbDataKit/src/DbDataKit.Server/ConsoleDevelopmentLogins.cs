using System.Text.Json.Nodes;
using DbDataKit.Runner;
using DbDataKit.Secrets;

namespace DbDataKit.Server;

/// <summary>What a screen sends to set up a development login: the Runner (a registered server's id or name), the channel, the project's connection, and the login the person typed.</summary>
public sealed record DevelopmentLoginBody(string? Runner, string? Channel, string? Connection, string? User, string? Password);

/// <summary>
/// The console puts a development login a person typed on a Workspace screen into the secret store of a Runner, for one package, channel and connection slot. The login goes from the screen to the console and from the console
/// to the Runner (<c>PUT /api/v1/secrets/{name}</c>, as the signed-in person, so the Runner decides whether they may store it and audits them); the Workspace server is asked only which package the project becomes, and no call
/// to it carries the login. Each part is stored under a name made of the package, the channel and the slot (<see cref="SecretOf"/>), so a connection the channel binds can name it, and a login of one channel is never
/// another channel's. Production is refused: its logins are bound by an administrator on the Runner.
/// Design: docs/authoring-metadata.md#before-anything-is-published-set-up-for-development
/// </summary>
public sealed class ConsoleDevelopmentLogins(ConsoleCatalogRead projects, ConsoleRelay relay)
{
    /// <summary>The kind of the secret names a development login is stored under on a Runner.</summary>
    public const string Kind = "dev-login";

    /// <summary>The longest user name accepted; the Runner limits the password.</summary>
    public const int MaxUserChars = 256;

    /// <summary>The name a part (<c>user</c>, <c>password</c>) of the development login of a slot of a package in a channel is stored under: <c>dbdatakit:runner:dev-login:&lt;package&gt;.&lt;channel&gt;.&lt;slot&gt;:&lt;part&gt;</c>.</summary>
    /// <remarks>Package and channel names and slots hold no dot, so the three never run into each other.</remarks>
    public static SecretName SecretOf(string package, string channel, string slot, string part) => new("runner", Kind, $"{package}.{channel}.{slot}", part);

    /// <summary>
    /// Checks the request, asks the Workspace server for the project's package (a call that carries none of the login), then stores the password and, when given, the user on the Runner. The answer names what is stored,
    /// never a value: 200 when every part is stored; a refusal of the console (400, 404, 409) before anything is sent; a refusal of the Workspace server or the Runner with its own words.
    /// </summary>
    public async Task<CatalogReadAnswer> SetUpAsync(string workspace, string project, DevelopmentLoginBody body, CancellationToken ct)
    {
        if (ConsoleCatalogRead.SlotProblem(body.Connection) is { } bad) return CatalogReadAnswer.Error(400, bad.Replace("to read", "to set up", StringComparison.Ordinal));
        if (!PackageManifestValidator.NameRule.IsMatch(body.Channel ?? "")) return CatalogReadAnswer.Error(400, "Name the channel to set up: `channel` (lower-case letters, digits, - and _).");
        if (body.Channel == ConsoleCatalogRead.Production)
            return CatalogReadAnswer.Error(409, $"A development login is set up in a channel other than {ConsoleCatalogRead.Production}. Production's logins are bound by an administrator on the Runner's Packages page (Bindings).");
        if (string.IsNullOrEmpty(body.Password) || body.Password.Length > RunnerSecretLimit)
            return CatalogReadAnswer.Error(400, $"Type the login's password: `password`, 1 to {RunnerSecretLimit} characters.");
        if (body.User is { } user && (user.Length is 0 or > MaxUserChars || user.Any(char.IsControl) || user.Trim() != user))
            return CatalogReadAnswer.Error(400, $"The user is a name of 1 to {MaxUserChars} characters, with no space around it.");
        if (string.IsNullOrWhiteSpace(body.Runner)) return CatalogReadAnswer.Error(400, "Name the Runner to set up: `runner`.");

        var (package, refused) = await projects.ProjectAsync(workspace, project, ct);
        if (refused is not null) return refused;
        var (target, problem) = await projects.RunnerAsync(body.Runner, ct);
        if (problem is not null) return problem;

        var slot = ConnectionSlots.NameOf(body.Connection!);
        var stored = new JsonObject();
        var parts = new List<(string Part, string Value)> { ("password", body.Password) };
        if (body.User is { } named) parts.Insert(0, ("user", named));
        foreach (var (part, value) in parts)
        {
            var name = SecretOf(package!, body.Channel!, slot, part).ToString();
            var (status, answer) = await relay.CallAsync(target!, HttpMethod.Put, $"/api/v1/secrets/{Uri.EscapeDataString(name)}", new JsonObject { ["value"] = value }, ct);
            if (status != 200) return ConsoleCatalogRead.Passed(status, answer, $"{target!.Name} did not store the login.");
            stored[part] = name;
        }

        return new(200, new JsonObject
        {
            ["package"] = package, ["connection"] = body.Connection, ["slot"] = slot, ["channel"] = body.Channel, ["production"] = false,
            ["runner"] = target!.InstanceId, ["runner_name"] = target.Name, ["secrets"] = stored,
        });
    }

    /// <summary>The longest value a Runner stores (<c>RunnerSecretsApi.MaxValueChars</c>).</summary>
    private const int RunnerSecretLimit = DbDataKit.Runner.Api.RunnerSecretsApi.MaxValueChars;
}
