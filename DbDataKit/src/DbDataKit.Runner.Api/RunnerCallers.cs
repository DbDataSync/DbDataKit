using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Http;

namespace DbDataKit.Runner.Api;

/// <summary>Who is calling and what they may do: the roles the call holds (already limited by whatever ceiling applies) and the actor the audit names.</summary>
public sealed record RunnerCaller(RunnerActor Actor, ServerRole Roles)
{
    /// <summary>Whether the call holds <paramref name="needed"/>, with the reason for the log when it does not.</summary>
    public AuthorizationDecision Authorize(ServerRole needed)
    {
        if (needed == ServerRole.None) return AuthorizationDecision.Deny(needed, Roles, "the call names no role, so nothing allows it");
        return ServerRoles.Allows(Roles, needed)
            ? AuthorizationDecision.Allow(needed, Roles)
            : AuthorizationDecision.Deny(needed, Roles, $"{string.Join('+', ServerRoles.Names(needed))} is needed and the call holds {(Roles == ServerRole.None ? "nothing" : string.Join('+', ServerRoles.Names(Roles)))}");
    }
}

/// <summary>How a host tells who is calling: <c>dbdatarunner</c> reads the console's or client's signed token, the console reads the signed-in session.</summary>
public delegate RunnerCaller RunnerCallerResolver(HttpContext context);

/// <summary>What differs between the hosts of the runner's management API.</summary>
/// <param name="Prefix">The route prefix of the calls: <c>/api/v1</c> on <c>dbdatarunner</c>, <c>/api/embedded-runner</c> and <c>/relay/console-runner/api/v1</c> on the console.</param>
/// <param name="Caller">Who is calling.</param>
/// <param name="Product">What the status says the server is (<c>dbdatarunner</c>, <c>dbdatakit</c>).</param>
/// <param name="Version">The program's version.</param>
public sealed record RunnerApiOptions(string Prefix, RunnerCallerResolver Caller, string Product, string Version)
{
    /// <summary>Adds what the host knows to <c>GET status</c> (the console's embedded runner adds its posture and the warning that production schedules stop with the console).</summary>
    public Action<System.Text.Json.Nodes.JsonObject>? DecorateStatus { get; init; }
}

public static class RunnerCallers
{
    /// <summary>The caller of a management call that carries a token (<c>dbdatarunner</c>): the roles are the token's, limited by the client's ceiling.</summary>
    public static RunnerCaller FromToken(ValidatedToken token) =>
        new(new RunnerActor(token.SubjectId, token.SubjectName, token.Client.Kind == ClientKind.Console ? "console" : "api", token.Client.Id), ManagementAuthorization.Effective(token));

    public static RunnerCallerResolver TokenResolver { get; } = context => FromToken(context.ManagementToken());
}
