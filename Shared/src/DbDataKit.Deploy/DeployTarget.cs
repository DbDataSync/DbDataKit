using System.Text.Json.Nodes;
using DbDataKit.Authoring;

namespace DbDataKit.Deploy;

/// <summary>A deploy, or a call around one, was refused or could not be done. <see cref="Status"/> is the HTTP status a route answers with, and the message is a sentence a person can act on (the target's own words when it refused).</summary>
public sealed class DeployException(string message, int status, JsonObject? adopt = null, Exception? inner = null) : Exception(message, inner)
{
    public int Status { get; } = status;

    /// <summary>Set when a Gateway refused for want of an adoption: the data source a model would go on and who made it (<c>data_source</c>, <c>made_by</c>). A screen asks the person and deploys again with <c>adopt</c>.</summary>
    public JsonObject? Adopt { get; } = adopt;
}

/// <summary>The products a deploy goes to, and how a server's own name (<c>GET /version</c>) says which.</summary>
public static class DeployProducts
{
    public const string Gateway = "gateway";
    public const string Runner = "runner";

    public static IReadOnlyList<string> All { get; } = [Gateway, Runner];

    /// <summary>The product a server name stands for, or null when it is neither (<c>dbdatagateway</c>, <c>dbdatarunner</c>).</summary>
    public static string? OfServer(string? serverName) => serverName switch { "dbdatagateway" => Gateway, "dbdatarunner" => Runner, _ => null };
}

/// <summary>
/// The Runner or Gateway a deploy goes to, as the caller knows it: the name it calls it by, its address, its instance id and its product (<see cref="DeployProducts"/>). The answers of a deploy and its messages name it; the calls themselves go
/// through the link the caller gives, which decides who is calling.
/// </summary>
public sealed record DeployTarget(string Name, string Url, string InstanceId, string Product);

/// <summary>
/// What a person says when they deploy what a kind made: for a Runner the <see cref="Channel"/>, whether the version is served at once (<see cref="Serve"/>) or staged (the default), the <see cref="ExpectedVersion"/> and the connections
/// <see cref="Bind"/> fills (slot to connection name); for a Gateway the <see cref="DataSource"/> its tables are on (the models' own storage connection when none is named) and <see cref="Adopt"/>, the data sources made by hand that the person
/// confirmed the models may go on (the Gateway records the adoption). <see cref="Model"/> names one model of a Store build, and <see cref="Note"/> is kept with the version. <see cref="Hold"/>, for a staged Runner package, asks the Runner
/// to keep the version from activating by itself until an administrator activates it, and says what it waits for.
/// </summary>
public sealed record DeployOptions(string? Channel = null, bool Serve = false, int? ExpectedVersion = null, IReadOnlyDictionary<string, string>? Bind = null, string? DataSource = null, IReadOnlyList<string>? Adopt = null, string? Model = null, string? Note = null,
    string? Hold = null);

/// <summary>Whether a kind of project may be deployed to a target: the kind's routes (<see cref="IProjectKind.Publishes"/>) must list the target's product.</summary>
public static class DeployRoutes
{
    /// <summary>
    /// The route a project of the kind <paramref name="kindName"/> takes to <paramref name="target"/> (what arrives there; the first of <paramref name="publishes"/> for the target's product, of <paramref name="what"/> when it is named). A kind that
    /// does not go to that product is a <see cref="DeployException"/> naming the kind, the target, its product and the products the kind goes to.
    /// </summary>
    public static ProjectPublishRoute RouteFor(string kindName, IReadOnlyList<ProjectPublishRoute> publishes, DeployTarget target, string? what = null)
    {
        var routes = publishes.Where(r => r.Product == target.Product && (what is null || r.What == what)).ToList();
        if (routes.Count > 0) return routes[0];
        var goes = publishes.Count == 0 ? "nowhere" : string.Join(" and ", publishes.Select(r => $"a {r.Product} ({r.What})").Distinct());
        throw new DeployException($"The {kindName} kind of project is published to {goes}, and '{target.Name}' is a {target.Product} ({target.Url}). Publish it to a target of the right product.", 409);
    }
}

/// <summary>
/// Where a deployed version came from, in a form the Runner's screens make a link of: the token <c>[workspace:&lt;instance&gt;/&lt;project&gt;/&lt;workspace&gt;]</c> at the end of the version's note, each part escaped as a URL path segment.
/// The console's address of the workspace is <c>/servers/&lt;instance&gt;/&lt;project&gt;/&lt;workspace&gt;</c>; <c>Shared/web/runner/src/origin.ts</c> reads the token.
/// </summary>
public static class DeployOrigin
{
    public static string Token(string instanceId, string project, string workspace) =>
        $"[workspace:{Uri.EscapeDataString(instanceId)}/{Uri.EscapeDataString(project)}/{Uri.EscapeDataString(workspace)}]";
}
