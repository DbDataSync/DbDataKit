using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Runner;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataKit.Server;

/// <summary>What a screen sends to test a connection of a draft: the Runner (a registered server's id or name), the channel, the project's connection, and optionally the role of its login (default, read or write).</summary>
public sealed record DraftBindingTestBody(string? Runner, string? Channel, string? Connection, string? Role = null);

/// <summary>
/// The Runner's test of a slot's binding for a draft, before anything of it is published: by the project's package, a channel and the connection's slot, never by a staged or served version, so <b>Test</b> works in any channel that binds
/// the slot. The console asks the Workspace server for the project as the signed-in person (whether they may read it, and which package it becomes on a Runner), then asks the Runner's binding test as that person
/// (<c>POST /api/v1/bindings/{channel}/integration/{package}/test</c>); the Runner decides with its own roles (administrator) and tries the binding, the connection, the login and the connect. The Workspace server is not in the path of the
/// test and calls no Runner. The answer is the Runner's with each step said; it never holds a value or a row.
/// Design: docs/authoring-metadata.md#testing-a-connection
/// </summary>
public sealed class ConsoleDraftBindingTest(ConsoleCatalogRead projects, ConsoleRelay relay, AuditLog audit)
{
    /// <summary>The steps of the Runner's binding test, in the order it tries them.</summary>
    public static readonly IReadOnlyList<string> Steps = ["binding", "connection", "login", "connect"];

    /// <summary>
    /// Checks the request, asks the Workspace server for the project's package as the person, then the Runner's test of the connection's slot in the channel as the person. 200 with the Runner's answer and its steps; a refusal of the
    /// console (400, 404, 409) before anything is sent; a refusal of the Workspace server or the Runner with its own words.
    /// </summary>
    public async Task<CatalogReadAnswer> TestAsync(string workspace, string project, DraftBindingTestBody body, string personId, CancellationToken ct)
    {
        if (ConsoleCatalogRead.SlotProblem(body.Connection) is { } bad) return CatalogReadAnswer.Error(400, bad.Replace("to read", "to test", StringComparison.Ordinal));
        if (!PackageManifestValidator.NameRule.IsMatch(body.Channel ?? "")) return CatalogReadAnswer.Error(400, "Name the channel to test in: `channel` (lower-case letters, digits, - and _).");
        var role = string.IsNullOrWhiteSpace(body.Role) ? "default" : body.Role.Trim();
        if (role is not ("default" or "read" or "write")) return CatalogReadAnswer.Error(400, "The role of the login to test is `default`, `read` or `write`.");
        if (string.IsNullOrWhiteSpace(body.Runner)) return CatalogReadAnswer.Error(400, "Name the Runner to test on: `runner`.");

        var (package, refused) = await projects.PackageAsync(workspace, project, ct);
        if (refused is not null) return refused;
        var (target, problem) = await projects.RunnerAsync(body.Runner, ct);
        if (problem is not null) return problem;

        var slot = ConnectionSlots.NameOf(body.Connection!);
        var (status, answer) = await relay.CallAsync(target!, HttpMethod.Post, $"/api/v1/bindings/{Uri.EscapeDataString(body.Channel!)}/integration/{Uri.EscapeDataString(package!)}/test", new JsonObject { ["slot"] = slot, ["role"] = role }, ct);
        if (status != 200 || answer is not JsonObject tested || tested["ok"] is not JsonValue okValue || !okValue.TryGetValue<bool>(out var ok))
        {
            var passed = ConsoleCatalogRead.Passed(status, answer, $"{target!.Name} did not test the slot '{slot}'.");
            Audit(personId, target!, workspace, project, package!, body.Channel!, slot, AuditEvent.Refused, new { error = (string?)passed.Body["error"] });
            return passed;
        }

        var step = (string?)tested["step"];
        Audit(personId, target!, workspace, project, package!, body.Channel!, slot, ok ? AuditEvent.Ok : AuditEvent.Failed, new { step });
        return new(200, new JsonObject
        {
            ["package"] = package, ["connection"] = body.Connection, ["slot"] = slot, ["channel"] = body.Channel, ["production"] = body.Channel == ConsoleCatalogRead.Production, ["role"] = role,
            ["runner"] = target!.InstanceId, ["runner_name"] = target.Name,
            ["ok"] = ok, ["step"] = step, ["message"] = (string?)tested["message"], ["connected"] = (bool?)tested["connected"] ?? false, ["bound_to"] = (string?)tested["bound_to"],
            ["steps"] = StepsOf(ok, step),
        });
    }

    /// <summary>
    /// Each step of the test with how it went: the steps before the one the Runner answered passed; that one passed or failed; those after it were not tried (<c>skipped</c>). A Runner that opens no connection answers <c>ok</c> at
    /// <c>login</c>, so <c>connect</c> is not tried.
    /// </summary>
    public static JsonArray StepsOf(bool ok, string? step)
    {
        var at = step is null ? -1 : Steps.ToList().IndexOf(step);
        return new JsonArray([.. Steps.Select((name, i) => (JsonNode?)new JsonObject
        {
            ["step"] = name,
            ["state"] = at < 0 ? "skipped" : i < at ? "passed" : i == at ? (ok ? "passed" : "failed") : "skipped",
        })]);
    }

    private void Audit(string personId, RegisteredInstance runner, string workspace, string project, string package, string channel, string slot, string outcome, object result) =>
        audit.Record(new AuditEvent("user", personId, ConsoleDeployRuns.BindingTestVerb, outcome, TargetKind: "server", TargetId: runner.InstanceId,
            Detail: JsonSerializer.Serialize(new { workspace_server = workspace, project, what = $"{package}/{channel}/{slot} on {runner.Name}", result })));
}
