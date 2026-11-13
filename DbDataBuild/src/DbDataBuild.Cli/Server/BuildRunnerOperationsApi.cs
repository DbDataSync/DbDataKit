using DbDataBuild.Cli.Runner;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataBuild.Cli.Server;

/// <summary>
/// Starting a person's write on the runner (plan 28c E21, owner decision of 2026-11-12: no write login in a Build server, every person-driven write a manual runner task).
/// <c>POST /api/v1/projects/{project}/operations/{apply-plan|ack|seed}</c> with the task's inputs as a JSON object queues the manual-only task of the project's operations package
/// (<see cref="BuildOperations.PackageOf"/>, published by <c>project publish --operations</c>) on the runner this server is paired with (the <c>schedule.runner</c> link of <c>server.yaml</c>), for the person the console vouched
/// for (<see cref="BuildManagementApi.ActorOf"/>): the runner records them (with this server beside them) and the command in the task writes them into the tracking tables. The server writes nothing and holds no write login for it;
/// it needs the role the same command needs on the commands route, checks the inputs the task checks, and audits who started what. The answer is <c>202 {run_id}</c>; the run is the runner's (its runs list, its log).
/// A plan that lives in a file of the project (<c>plans/&lt;connection&gt;/&lt;id&gt;.plan.yml</c>) travels as the text of the run.
/// </summary>
internal static class BuildRunnerOperationsApi
{
    public static void Map(IEndpointRouteBuilder app, ProjectsFile projects, IReadOnlyDictionary<string, CommandSpec> specs, Func<BuildScheduleSettings> owners,
        Func<BuildScheduleSettings, DbDataBuild.Cli.Runner.IRunnerTaskControl?> control, IAuditLog? audit)
    {
        app.MapPost("/api/v1/projects/{project}/operations/{operation}", async (string project, string operation, HttpContext context) =>
        {
            if (projects.Find(project) is not { } hosted) return Results.NotFound();
            if (BuildOperations.TaskOf(operation) is not { } task) return Results.Json(new { error = $"`{operation}` is not an operation: apply-plan, ack or seed." }, statusCode: StatusCodes.Status404NotFound);

            // the role the same command needs when the server runs it itself (nothing new is allowed by going through the runner)
            var spec = specs[task == BuildOperations.Seed ? "connection seed" : "connection deploy"];
            var token = context.ManagementToken();
            var decision = ManagementAuthorization.Authorize(token, BuildManagementApi.RoleOf(spec), hosted.Id);
            if (!decision.Allowed)
            {
                Record(audit, token, hosted.Id, task, null, "refused: needs the " + decision.Needed.ToString().ToLowerInvariant() + " role");
                return Results.Json(new { error = $"`{operation}` needs the {decision.Needed.ToString().ToLowerInvariant()} role." }, statusCode: StatusCodes.Status403Forbidden);
            }

            JsonObject body;
            try
            {
                body = context.Request.ContentLength is 0 or null && !context.Request.HasJsonContentType() ? new JsonObject() : await JsonSerializer.DeserializeAsync<JsonObject>(context.Request.Body, cancellationToken: context.RequestAborted) ?? new JsonObject();
            }
            catch (JsonException)
            {
                return Results.Json(new { error = "The body is a JSON object of the task's inputs." }, statusCode: StatusCodes.Status400BadRequest);
            }

            var declared = BuildOperations.InputsOf(task).Select(i => i.Name).ToHashSet(StringComparer.Ordinal);
            var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, value) in body)
            {
                if (!declared.Contains(name)) return Results.Json(new { error = $"`{name}` is not an input of `{operation}` (its inputs: {string.Join(", ", declared.Order(StringComparer.Ordinal))})." }, statusCode: StatusCodes.Status400BadRequest);
                inputs[name] = value is JsonValue v && v.TryGetValue<string>(out var text) ? text : value?.ToJsonString() ?? "";
            }

            if (BuildOperations.Refusal(task, inputs) is { } refusal) return Results.Json(new { error = refusal }, statusCode: StatusCodes.Status400BadRequest);

            // a plan a person wrote to a file of the project (`plans/<connection>/<id>.plan.yml`) goes with the run; one kept in the tracking tables is found there by its id
            if (task == BuildOperations.ApplyPlan && !inputs.ContainsKey("plan_text"))
            {
                var file = Path.Combine(projects.RootOf(hosted), "plans", inputs["connection"], inputs["plan"] + ".plan.yml");
                if (File.Exists(file) && new FileInfo(file).Length <= 900_000) inputs["plan_text"] = await File.ReadAllTextAsync(file, context.RequestAborted);
            }

            BuildScheduleSettings settings;
            try { settings = owners(); }
            catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict); }
            if (control(settings) is not { } runner || settings.Runner is not { } link)
                return Results.Json(new { error = "This server is not paired with a runner for operations: `server.yaml` has no `schedule.runner` (the runner, the channel and the pairings), and a person's write runs there, never on this server's login." }, statusCode: StatusCodes.Status409Conflict);

            var package = BuildOperations.PackageOf(hosted.Id);
            var person = BuildManagementApi.ActorOf(token);
            var subject = $"{inputs["connection"]}/{task}";
            try
            {
                var runId = await runner.RunForAsync(package, task, inputs, person, context.RequestAborted);
                if (runId is null)
                {
                    Record(audit, token, hosted.Id, task, subject, "queued already");
                    return Results.Json(new { error = $"`{task}` is queued or running on the runner already." }, statusCode: StatusCodes.Status409Conflict);
                }

                Record(audit, token, hosted.Id, task, subject, "queued on the runner", $"run {runId}");
                return Results.Json(new JsonObject { ["task"] = task, ["package"] = package, ["channel"] = link.Channel, ["run_id"] = runId, ["on_behalf_of"] = person }, statusCode: StatusCodes.Status202Accepted);
            }
            catch (DbDataKit.Runner.Client.RunnerClientException ex)
            {
                Record(audit, token, hosted.Id, task, subject, "refused by the runner");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }

    /// <summary>The audit line of an operation started on the runner: <c>operation.run</c> on <c>project/connection/operation</c>, by the person the console vouched for.</summary>
    private static void Record(IAuditLog? audit, ValidatedToken token, string project, string task, string? subject, string outcome, string detail = "") =>
        audit?.Append(new AuditEntry(TimeProvider.System.GetUtcNow(), "command", "dbdatabuild server", token.SubjectId, token.SubjectName ?? token.SubjectId, false, "operation.run", $"{project}/{subject ?? task}", outcome, detail));
}
