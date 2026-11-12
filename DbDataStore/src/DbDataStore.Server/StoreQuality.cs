using System.Text.Json.Nodes;
using DbDataKit.Dialect;
using DbDataKit.Lineage;
using DbDataKit.Quality;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataStore.Server;

/// <summary>
/// The Store's datasets with checks (plan 23a FQ11): for each model that stores a <c>checks:</c> section (model format 2, FQ01), every entity a check names is a dataset under the identity its table has on the model's connection (the one lineage gives it,
/// plan 18 G12), so a result, a block and a node in the graph line up. The checks run over the model's connection through the provider's read-only recipe, with the model's <b>read login</b> when its connection file names one and its write login otherwise
/// (the evaluator's own policy refuses any statement that is not one SELECT, and the engine's session is read-only); an entity's key columns are the sample keys. This is <b>beyond the write rules</b>, which stay validation at write time: it finds rows made
/// invalid in other ways (a direct insert, a changed model). A model that keeps one copy per instance is left out (the checks are about the model's own tables) and named in <see cref="Notes"/>.
/// </summary>
public sealed class StoreQualitySource(string directory, StoreConnections connections) : IQualitySource
{
    /// <summary>What was left out, and why, by the last <see cref="DatasetsAsync"/>.</summary>
    public IReadOnlyList<string> Notes { get; private set; } = [];

    public async Task<IReadOnlyList<QualityDataset>> DatasetsAsync(CancellationToken ct = default)
    {
        var notes = new List<string>();
        var datasets = new List<QualityDataset>();
        foreach (var name in StoreModels.Names(directory).Order(StringComparer.Ordinal))
        {
            var loaded = StoreModels.Check(name, File.ReadAllText(StoreModels.FileOf(directory, name)));
            if (loaded.Model is not { } model || loaded.Diagnostics.Any(d => d.Severity == DbDataKit.Config.Yaml.ConfigSeverity.Error) || model.Checks is not { Count: > 0 } checks) continue;
            if (model.Instances != DbDataKit.DataModel.InstanceMode.None) { notes.Add($"Model '{name}': its checks were not evaluated, because it keeps a copy per instance."); continue; }
            var (address, problem) = await connections.AddressOnlyAsync(model.Connection, ct).ConfigureAwait(false);
            if (address is null) { notes.Add($"Model '{name}': its checks were not evaluated: {problem}"); continue; }
            string engine;
            try { engine = connections.EngineOf(model.Connection); }
            catch (StoreConnectionException ex) { notes.Add($"Model '{name}': its checks were not evaluated: {ex.Message}"); continue; }

            string Identity(string entity) => LineageIdentity.Dataset(address, model.Name, entity).ToString();
            IReadOnlyList<string>? Resolve(string dataset)
            {
                var entity = dataset.Contains("://", StringComparison.Ordinal) ? model.Entities.FirstOrDefault(e => Identity(e.Name) == dataset) : model.Entities.FirstOrDefault(e => e.Name == dataset);
                return entity is null ? null : [model.Name, entity.Name];
            }

            var dialect = DialectCatalog.Builtin.Get(engine);
            var connection = model.Connection;
            Func<CancellationToken, Task<System.Data.Common.DbConnection>> open = token => connections.OpenReadOnlyAsync(connection, token);
            foreach (var group in checks.GroupBy(c => c.Attachment.Dataset, StringComparer.Ordinal))
            {
                var entity = model.Entity(group.Key) ?? model.Entities.FirstOrDefault(e => Identity(e.Name) == group.Key);
                if (entity is null) { notes.Add($"Model '{name}': a check names '{group.Key}', which is not an entity of the model."); continue; }
                var one = group.Select(c => c with { Attachment = c.Attachment with { Dataset = Identity(entity.Name) } }).ToList();
                datasets.Add(new QualityDataset(Identity(entity.Name), one, dialect, open, Resolve, Aliases: [entity.Name, $"{model.Name}.{entity.Name}"], KeyColumns: entity.Key));
            }
        }

        Notes = notes;
        return datasets;
    }
}

/// <summary>The results of the entities' checks and their evaluation on request (plan 23a FQ11, FQ17), in the form every server sends a console (<see cref="QualityDocuments"/>).</summary>
public static class StoreQuality
{
    private static IResult Denied(AuthorizationDecision decision) =>
        Results.Json(new { error = $"This needs the {decision.Needed.ToString().ToLowerInvariant()} role." }, statusCode: StatusCodes.Status403Forbidden);

    public static void Map(IEndpointRouteBuilder app, string directory, StoreConnections connections, StateDatabase state)
    {
        var store = new QualityStore(state);
        app.MapGet("/api/v1/quality", (HttpContext context) =>
        {
            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Viewer);
            return !decision.Allowed ? Denied(decision) : Results.Json(QualityDocuments.Overview(store, canRun: true));
        });
        app.MapGet("/api/v1/quality/history", (HttpContext context, string? dataset, string? check, int? limit) =>
        {
            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Viewer);
            if (!decision.Allowed) return Denied(decision);
            if (string.IsNullOrWhiteSpace(dataset) || string.IsNullOrWhiteSpace(check)) return Results.Json(new { error = "Say which check: `dataset` and `check`." }, statusCode: StatusCodes.Status400BadRequest);
            return Results.Json(QualityDocuments.History(store, dataset, check, limit ?? QualityDocuments.HistoryLimit));
        });
        // an evaluation on request changes no data but writes results and may set a block: an administrator's call
        app.MapPost("/api/v1/quality/run", async (HttpContext context, QualityRunRequest request) =>
        {
            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Admin);
            if (!decision.Allowed) return Denied(decision);
            var source = new StoreQualitySource(directory, connections);
            try
            {
                var report = await new QualityRunService(source, store).RunAsync(string.IsNullOrWhiteSpace(request.Dataset) ? null : request.Dataset, runId: null, context.RequestAborted);
                return Results.Json(new JsonObject
                {
                    ["dataset"] = request.Dataset, ["evaluated"] = report.Evaluated, ["passed"] = report.Evaluated - report.Failed - report.Errors, ["failed"] = report.Failed, ["errors"] = report.Errors, ["blocking"] = report.Blocking,
                    ["notes"] = new JsonArray(source.Notes.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
                });
            }
            catch (UnknownDatasetException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status404NotFound); }
        });
    }
}

/// <summary>The body of an evaluation on request: a dataset's identity or entity name; empty is every dataset with checks.</summary>
public sealed record QualityRunRequest(string? Dataset);
