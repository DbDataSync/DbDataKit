using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.DataModel;
using DbDataKit.DataModel.Storage;
using DbDataKit.Store;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataStore.Server;

/// <summary>
/// A model's tables and rows over the management API (plan 19, Z09). Storage is planned for a viewer and created by an administrator; a row is read by its key (and its history, for an entity that keeps one) by a viewer;
/// rows change in batches through <see cref="ModelWriter"/>, so every rule, version check and audit row of the library applies, for an author or an administrator. The caller named by the token is the audit's actor.
/// A refused write answers 422 with each broken rule, a stale version 409, a missing row 404.
/// </summary>
public static class StoreData
{
    private static IResult Denied(AuthorizationDecision decision) =>
        Results.Json(new { error = $"This needs the {decision.Needed.ToString().ToLowerInvariant()} role." }, statusCode: StatusCodes.Status403Forbidden);

    private static AuthorizationDecision Writer(HttpContext context)
    {
        var author = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Author);
        return author.Allowed ? author : ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Admin) is { Allowed: true } admin ? admin : author;
    }

    // the wire format is the shared one (DbDataKit.Store.RowJson), so the serving layer's store module answers the same (plan 20 O08)
    private static JsonObject Row(StoredRow row) => RowJson.Row(row);

    /// <summary>
    /// The model a request is about. A copy that has been made or migrated runs the version of the model it was made or migrated at, so a copy that is behind keeps working while the model moves on (plan 26, Z13);
    /// <paramref name="current"/> asks for the model as it is saved now (to create a copy, or to migrate one).
    /// </summary>
    private static async Task<(DataModel? Model, IResult? Problem)> ModelAsync(string directory, string name, string? instance = null, bool current = false)
    {
        if (!StoreModels.IsValidName(name) || !File.Exists(StoreModels.FileOf(directory, name))) return (null, Results.NotFound(new { error = $"There is no model '{name}'." }));
        var text = await File.ReadAllTextAsync(StoreModels.FileOf(directory, name));
        if (!current && StoreInstances.Find(directory, name, instance is { Length: > 0 } ? instance : StoreInstances.Default)?.ModelVersion is { } pinned && StoreModels.TextOfVersion(directory, name, pinned) is { } kept) text = kept;
        var result = StoreModels.Check(name, text);
        return result.Ok ? (result.Model, null) : (null, Results.Json(new { error = $"{name}.yaml is not a valid model." }, statusCode: StatusCodes.Status422UnprocessableEntity));
    }

    private static IResult Fail(Exception ex) => ex switch
    {
        StoreConnectionException => Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway),
        WriteRefusedException refused => Results.Json(RowJson.Refusal(refused), statusCode: StatusCodes.Status422UnprocessableEntity),
        VersionConflictException conflict => Results.Json(new { error = conflict.Message, expected_version = conflict.Expected, current_version = conflict.Current }, statusCode: StatusCodes.Status409Conflict),
        RowNotFoundException => Results.NotFound(new { error = ex.Message }),
        StoreNotSupportedException or StorageException => Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status422UnprocessableEntity),
        _ => throw ex,
    };

    private static bool Known(Exception ex) => ex is StoreConnectionException or WriteRefusedException or VersionConflictException or RowNotFoundException or StoreNotSupportedException or StorageException;

    private static string InstanceName(HttpContext context) => context.Request.RouteValues["instance"] as string ?? StoreInstances.Default;

    /// <summary>Where an instance of the model in the request is kept, or the 404 that says why not. The model's own copy (no instance in the path, or <c>default</c>) is always there.</summary>
    private static (string Connection, string Schema, string? Instance, IResult? Problem) Where(string directory, DataModel model, HttpContext context)
    {
        var (connection, schema, instance, problem) = StoreInstances.Resolve(directory, model, context.Request.RouteValues["instance"] as string);
        return problem is null ? (connection!, schema!, instance, null) : ("", "", null, Results.NotFound(new { error = problem }));
    }

    private static readonly System.Text.RegularExpressions.Regex DataPath = new(
        @"^/api/v1/models/(?<model>[^/]+)(/instances/[^/]+)?/(entities/(?<entity>[^/]+)/rows?|changes)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex ModelDataPath = new(
        @"^/api/v1/models/(?<model>[^/]+)(/instances/[^/]+)?/(entities/[^/]+/rows?|changes|requests(/.*)?|migrate|storage/create)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// One data path per model (plan 20 O10b, owner decision 2026-11-04): a model published to a serving group is read and written there, by its published version, and this server refuses its rows, changes and change
    /// requests (409, naming the groups), so one definition is ever enforced on its tables. Its structure moves only by publishing (which migrates the tables as its first step), so applying a migration or creating its tables
    /// here is refused too; a dry-run migration, which changes nothing, still answers. Unpublishing it from every group gives the data path back.
    /// </summary>
    public static IApplicationBuilder UsePublishedModels(this IApplicationBuilder app, string directory) => app.Use(async (context, next) =>
    {
        var match = ModelDataPath.Match(context.Request.Path.Value ?? "");
        var dryRun = context.Request.Path.Value!.EndsWith("/migrate", StringComparison.Ordinal) && !string.Equals(context.Request.Query["apply"], "true", StringComparison.OrdinalIgnoreCase);
        if (match.Success && !dryRun && new StorePublications(directory).Of(match.Groups["model"].Value) is { Count: > 0 } groups)
        {
            var model = match.Groups["model"].Value;
            await Results.Json(new
            {
                error = $"The model '{model}' is published to {string.Join(", ", groups)}, where it is read and written by its published version; publish it to change it (dbdatastore model publish {model}), unpublish it to read and write it here.",
                groups,
            }, statusCode: StatusCodes.Status409Conflict).ExecuteAsync(context);
            return;
        }
        await next();
    });

    /// <summary>
    /// A token with a scope (an app's, plan 26 section 3) reaches the row reads and the changes endpoint and nothing else: not models, instances, storage, migration or change requests. A read must be in the scope;
    /// the changes endpoint checks each change's entity itself (<see cref="OutsideScope"/>), since the entities are in the body.
    /// </summary>
    public static IApplicationBuilder UseScopedTokens(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (context.ManagementTokenOrNull() is not { Scope: not null } token) { await next(); return; }
        var match = DataPath.Match(context.Request.Path.Value ?? "");
        var allowed = match.Success && (match.Groups["entity"].Success
            ? HttpMethods.IsGet(context.Request.Method) && TokenScope.Allows(token, Uri.UnescapeDataString(match.Groups["model"].Value), Uri.UnescapeDataString(match.Groups["entity"].Value), write: false)
            : HttpMethods.IsPost(context.Request.Method));
        if (!allowed)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "This token is for an app and its scope does not include this." });
            return;
        }
        await next();
    });

    /// <summary>The first change whose entity the token's scope does not allow writing, or null.</summary>
    private static string? OutsideScope(ValidatedToken token, string model, IEnumerable<RowChange> changes) =>
        changes.Select(c => c.Entity).FirstOrDefault(entity => !TokenScope.Allows(token, model, entity, write: true));

    public static void Map(IEndpointRouteBuilder app, string directory, StoreConnections connections)
    {
        // the model's own copy, and each instance of it: the same routes under two prefixes
        MapUnder(app, directory, connections, "/api/v1/models/{name}");
        MapUnder(app, directory, connections, "/api/v1/models/{name}/instances/{instance}");

        // which instances a model has: a viewer lists them, an administrator registers and forgets them (forgetting never touches the data)
        app.MapGet("/api/v1/models/{name}/instances", async (string name, HttpContext context) =>
        {
            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Viewer);
            if (!decision.Allowed) return Denied(decision);
            var (model, problem) = await ModelAsync(directory, name);
            if (problem is not null) return problem;
            return Results.Json(new JsonObject
            {
                ["mode"] = model!.Instances.ToString().ToLowerInvariant(),
                ["instances"] = new JsonArray(StoreInstances.List(directory, name).Select(i => (JsonNode)new JsonObject { ["name"] = i.Name, ["connection"] = i.Connection, ["schema"] = model.SchemaOf(i.Name, out _) }).ToArray()),
            });
        });

        // where each copy of a model stands against the model's current structure
        app.MapGet("/api/v1/models/{name}/migrations", async (string name, HttpContext context) =>
        {
            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Viewer);
            if (!decision.Allowed) return Denied(decision);
            var (model, problem) = await ModelAsync(directory, name);
            if (problem is not null) return problem;
            string current;
            try { current = ModelStorage.DefinitionHash(model!, connections.EngineOf(model!.Connection)); }
            catch (Exception ex) when (Known(ex)) { return Fail(ex); }
            var copies = new List<StoreInstance> { StoreInstances.Find(directory, name, StoreInstances.Default) ?? new StoreInstance(StoreInstances.Default, null) };
            // a model that shares its tables has one set of them: its instances are as current as the model's own copy
            copies.AddRange(StoreInstances.List(directory, name));
            return Results.Json(new JsonObject
            {
                ["definition"] = current,
                ["instances"] = new JsonArray(copies.Select(i =>
                {
                    var effective = model.Instances == InstanceMode.Column && i.Name != StoreInstances.Default ? copies[0] : i;
                    return (JsonNode)new JsonObject { ["name"] = i.Name, ["definition"] = effective.Definition, ["applied_at"] = effective.AppliedAt?.ToString("O"), ["status"] = StoreInstances.StatusOf(effective, current) };
                }).ToArray()),
            });
        });

        // { "connection": "name" } for a model that keeps each instance on a connection of its own
        app.MapPut("/api/v1/models/{name}/instances/{instance}", async (string name, string instance, HttpContext context) =>
        {
            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Admin);
            if (!decision.Allowed) return Denied(decision);
            var (model, problem) = await ModelAsync(directory, name);
            if (problem is not null) return problem;
            if (!StoreModels.IsValidName(instance) || instance == StoreInstances.Default) return Results.BadRequest(new { error = $"'{instance}' is not an instance name: lower-case letters, digits, '-' and '_', starting with a letter or digit, at most 64, and not 'default'." });
            if (model!.Instances == InstanceMode.None) return Results.Json(new { error = $"The model '{name}' keeps one copy only. Set `storage.instances` to `schema` or `connection` in the model to keep more." }, statusCode: StatusCodes.Status422UnprocessableEntity);
            if (model.SchemaOf(instance, out var tooLong) is null) return Results.Json(new { error = tooLong }, statusCode: StatusCodes.Status422UnprocessableEntity);
            string? connection = null;
            if (model.Instances == InstanceMode.Connection)
            {
                JsonNode? body;
                try { body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted); }
                catch (JsonException) { return Results.BadRequest(new { error = "The body is not JSON." }); }
                connection = (string?)body?["connection"];
                if (connection is null || !StoreModels.IsValidName(connection) || !File.Exists(StoreConnections.FileOf(directory, connection)))
                    return Results.Json(new { error = "This model keeps each instance on its own connection: the body needs {\"connection\": \"name\"} naming a file of connections/." }, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            var existed = StoreInstances.Find(directory, name, instance) is not null;
            StoreInstances.Save(directory, name, new StoreInstance(instance, connection));
            return Results.Json(new JsonObject { ["name"] = instance, ["connection"] = connection ?? model.Connection, ["schema"] = model.SchemaOf(instance, out _) }, statusCode: existed ? StatusCodes.Status200OK : StatusCodes.Status201Created);
        });

        app.MapDelete("/api/v1/models/{name}/instances/{instance}", (string name, string instance, HttpContext context) =>
        {
            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Admin);
            if (!decision.Allowed) return Denied(decision);
            if (!StoreModels.IsValidName(name) || !StoreModels.IsValidName(instance)) return Results.NotFound(new { error = "There is no such instance." });
            return StoreInstances.Delete(directory, name, instance) ? Results.NoContent() : Results.NotFound(new { error = $"The model '{name}' has no instance '{instance}'." });
        });
    }

    private static void MapUnder(IEndpointRouteBuilder app, string directory, StoreConnections connections, string prefix)
    {
        app.MapGet(prefix + "/storage", async (string name, HttpContext context) =>
        {
            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Viewer);
            if (!decision.Allowed) return Denied(decision);
            var (model, problem) = await ModelAsync(directory, name, context.Request.RouteValues["instance"] as string);
            if (problem is not null) return problem;
            var (connectionName, schema, instanceId, notThere) = Where(directory, model!, context);
            if (notThere is not null) return notThere;
            try
            {
                var plan = ModelStorage.Plan(model!, connections.EngineOf(connectionName), schema);
                var existing = new JsonArray();
                try
                {
                    await using var connection = await connections.OpenAsync(connectionName, context.RequestAborted);
                    foreach (var table in await ModelStorage.ExistingAsync(connection, plan, context.RequestAborted)) existing.Add(table);
                }
                catch (StoreConnectionException ex) { return Results.Json(new JsonObject { ["engine"] = plan.Engine, ["statements"] = Statements(plan), ["existing"] = null, ["connection_problem"] = ex.Message }); }
                return Results.Json(new JsonObject { ["engine"] = plan.Engine, ["statements"] = Statements(plan), ["existing"] = existing });
            }
            catch (Exception ex) when (Known(ex)) { return Fail(ex); }
        });

        app.MapPost(prefix + "/storage/create", async (string name, HttpContext context) =>
        {
            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Admin);
            if (!decision.Allowed) return Denied(decision);
            var (model, problem) = await ModelAsync(directory, name, context.Request.RouteValues["instance"] as string, current: true);
            if (problem is not null) return problem;
            var (connectionName, schema, instanceId, notThere) = Where(directory, model!, context);
            if (notThere is not null) return notThere;
            if (model!.Instances == InstanceMode.Column && instanceId != StoreInstances.Default)
                return Results.Json(new { error = "This model's instances share their tables. Create the storage once, through the model's own path." }, statusCode: StatusCodes.Status422UnprocessableEntity);
            try
            {
                var plan = ModelStorage.Plan(model!, connections.EngineOf(connectionName), schema);
                await using var connection = await connections.OpenAsync(connectionName, context.RequestAborted);
                await ModelStorage.CreateAsync(connection, plan, context.RequestAborted);
                StoreInstances.Record(directory, name, InstanceName(context), ModelStorage.DefinitionHash(model!, plan.Engine), StoreModels.CurrentVersion(directory, name), DateTimeOffset.UtcNow);
                return Results.Json(new JsonObject { ["engine"] = plan.Engine, ["statements"] = Statements(plan) }, statusCode: StatusCodes.Status201Created);
            }
            catch (Exception ex) when (Known(ex)) { return Fail(ex); }
        });

        // the model as data for a form or a grid (and a typed client): entities, keys, attributes with their types and rules, the history each entity keeps; a copy that is behind shows the version it runs
        app.MapGet(prefix + "/schema", async (string name, HttpContext context) =>
        {
            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Viewer);
            if (!decision.Allowed) return Denied(decision);
            var (model, problem) = await ModelAsync(directory, name, context.Request.RouteValues["instance"] as string);
            if (problem is not null) return problem;
            var (_, _, _, notThere) = Where(directory, model!, context);
            return notThere ?? Results.Json(StoreModels.Schema(model!));
        });

        // bring this copy's tables up to the model by adding only (plan 26, Z13): a dry run unless ?apply=true, and the instance is noted as current once it has been applied
        app.MapPost(prefix + "/migrate", async (string name, HttpContext context) =>
        {
            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Admin);
            if (!decision.Allowed) return Denied(decision);
            var (model, problem) = await ModelAsync(directory, name, context.Request.RouteValues["instance"] as string, current: true);
            if (problem is not null) return problem;
            var (connectionName, schema, instanceId, notThere) = Where(directory, model!, context);
            if (notThere is not null) return notThere;
            if (model!.Instances == InstanceMode.Column && instanceId != StoreInstances.Default)
                return Results.Json(new { error = "This model's instances share their tables. Migrate them once, through the model's own path." }, statusCode: StatusCodes.Status422UnprocessableEntity);
            var apply = string.Equals(context.Request.Query["apply"], "true", StringComparison.OrdinalIgnoreCase);
            try
            {
                var engine = connections.EngineOf(connectionName);
                await using var connection = await connections.OpenAsync(connectionName, context.RequestAborted);
                var plan = ModelStorage.PlanExpand(model, engine, schema, await ModelStorage.ReadLiveAsync(connection, schema, context.RequestAborted));
                var applied = false;
                if (apply && plan.Blocked.Count == 0)
                {
                    await ModelStorage.ApplyExpandAsync(connection, plan, context.RequestAborted);
                    StoreInstances.Record(directory, name, InstanceName(context), ModelStorage.DefinitionHash(model, engine), StoreModels.CurrentVersion(directory, name), DateTimeOffset.UtcNow);
                    applied = true;
                }
                return Results.Json(new JsonObject
                {
                    ["up_to_date"] = plan.UpToDate,
                    ["applied"] = applied,
                    ["statements"] = Statements(plan.Statements),
                    ["blocked"] = new JsonArray(plan.Blocked.Select(b => (JsonNode)JsonValue.Create(b)!).ToArray()),
                    ["notes"] = new JsonArray(plan.Notes.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
                }, statusCode: plan.Blocked.Count > 0 && apply ? StatusCodes.Status422UnprocessableEntity : StatusCodes.Status200OK);
            }
            catch (Exception ex) when (Known(ex)) { return Fail(ex); }
        });

        // one row by its key: ?key.<attribute>=<value>; with ?as_of=<time> the version that held then, with ?history=true every version
        app.MapGet(prefix + "/entities/{entity}/row", async (string name, string entity, HttpContext context) =>
        {
            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Viewer);
            if (!decision.Allowed) return Denied(decision);
            var (model, problem) = await ModelAsync(directory, name, context.Request.RouteValues["instance"] as string);
            if (problem is not null) return problem;
            var (connectionName, schema, instanceId, notThere) = Where(directory, model!, context);
            if (notThere is not null) return notThere;
            if (model!.Entity(entity) is null) return Results.NotFound(new { error = $"The model '{name}' has no entity '{entity}'." });
            var key = context.Request.Query.Where(q => q.Key.StartsWith("key.", StringComparison.Ordinal)).ToDictionary(q => q.Key[4..], q => (object?)q.Value.ToString());
            try
            {
                await using var connection = await connections.OpenAsync(connectionName, context.RequestAborted);
                var writer = new ModelWriter(model, connection, connections.EngineOf(connectionName), schema: schema, instance: instanceId);
                if (context.Request.Query["history"] == "true")
                    return Results.Json(new JsonObject { ["versions"] = new JsonArray((await writer.HistoryAsync(entity, key, context.RequestAborted)).Select(r => (JsonNode)Row(r)).ToArray()) });
                StoredRow? row;
                if (context.Request.Query["as_of"].ToString() is { Length: > 0 } asOf)
                {
                    if (!DateTimeOffset.TryParse(asOf, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var moment)) return Results.BadRequest(new { error = $"'{asOf}' is not a time." });
                    row = await writer.GetAsOfAsync(entity, key, moment, context.RequestAborted);
                }
                else row = await writer.GetAsync(entity, key, context.RequestAborted);
                return row is null ? Results.NotFound(new { error = $"`{entity}` has no such row." }) : Results.Json(Row(row));
            }
            catch (Exception ex) when (Known(ex)) { return Fail(ex); }
        });

        // a page of rows in key order: ?limit=100&offset=0 (limit at most 1000); an entity with history lists its open versions
        app.MapGet(prefix + "/entities/{entity}/rows", async (string name, string entity, HttpContext context) =>
        {
            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Viewer);
            if (!decision.Allowed) return Denied(decision);
            var (model, problem) = await ModelAsync(directory, name, context.Request.RouteValues["instance"] as string);
            if (problem is not null) return problem;
            var (connectionName, schema, instanceId, notThere) = Where(directory, model!, context);
            if (notThere is not null) return notThere;
            if (model!.Entity(entity) is null) return Results.NotFound(new { error = $"The model '{name}' has no entity '{entity}'." });
            if (!int.TryParse(context.Request.Query["limit"].ToString() is { Length: > 0 } l ? l : "100", out var limit) || !int.TryParse(context.Request.Query["offset"].ToString() is { Length: > 0 } o ? o : "0", out var offset))
                return Results.BadRequest(new { error = "limit and offset are whole numbers." });
            try
            {
                await using var connection = await connections.OpenAsync(connectionName, context.RequestAborted);
                var rows = await new ModelWriter(model, connection, connections.EngineOf(connectionName), schema: schema, instance: instanceId).ListAsync(entity, limit, offset, context.RequestAborted);
                return Results.Json(new JsonObject { ["rows"] = new JsonArray(rows.Select(r => (JsonNode)Row(r)).ToArray()), ["limit"] = limit, ["offset"] = offset });
            }
            catch (Exception ex) when (Known(ex)) { return Fail(ex); }
        });

        // { "changes": [ { "action": "insert|update|delete", "entity": "...", "key": {...}, "expected_version": 3, "values": {...}, "effective_from": "..." } ], "description": "..." }
        // Written at once, or kept as a change request (202) when the model asks for approval of this caller's writes (Z07).
        app.MapPost(prefix + "/changes", async (string name, HttpContext context) =>
        {
            var decision = Writer(context);
            if (!decision.Allowed) return Denied(decision);
            var (model, problem) = await ModelAsync(directory, name, context.Request.RouteValues["instance"] as string);
            if (problem is not null) return problem;
            var (connectionName, schema, instanceId, notThere) = Where(directory, model!, context);
            if (notThere is not null) return notThere;
            JsonNode? body;
            try { body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted); }
            catch (JsonException) { return Results.BadRequest(new { error = "The body is not JSON." }); }
            if (body?["changes"] is not JsonArray list || list.Count == 0) return Results.BadRequest(new { error = "The body needs a non-empty \"changes\" array." });
            var (changes, bad) = ParseChanges(list);
            if (bad is not null) return bad;
            var token = context.ManagementToken();
            if (OutsideScope(token, name, changes!) is { } outside) return Results.Json(new { error = $"This token's scope does not include writing '{outside}'." }, statusCode: StatusCodes.Status403Forbidden);
            if (StoreChangeRequests.Needs(model!, token))
            {
                var request = StoreChangeRequests.Propose(directory, name, InstanceName(context), token, (string?)body["description"], (JsonArray)JsonNode.Parse(list.ToJsonString())!, DateTimeOffset.UtcNow);
                return Results.Json(request.ToJson(withChanges: false), statusCode: StatusCodes.Status202Accepted);
            }
            try
            {
                await using var connection = await connections.OpenAsync(connectionName, context.RequestAborted);
                await new ModelWriter(model!, connection, connections.EngineOf(connectionName), schema: schema, instance: instanceId).ApplyAsync(changes!, new WriteActor(token.SubjectId, token.SubjectName), context.RequestAborted);
                return Results.Json(new { applied = changes!.Count });
            }
            catch (Exception ex) when (Known(ex)) { return Fail(ex); }
        });

        // the change requests of the model's copy: a viewer lists and reads them, whoever approves for the model decides (an agent never does)
        app.MapGet(prefix + "/requests", async (string name, HttpContext context) =>
        {
            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Viewer);
            if (!decision.Allowed) return Denied(decision);
            var (model, problem) = await ModelAsync(directory, name, context.Request.RouteValues["instance"] as string);
            if (problem is not null) return problem;
            var state = context.Request.Query["state"].ToString();
            if (state.Length > 0 && state is not (ChangeRequest.Pending or ChangeRequest.Approved or ChangeRequest.Rejected)) return Results.BadRequest(new { error = "state is pending, approved or rejected." });
            // `can_decide` is whether this caller may approve or reject on this model (the role the model's approval asks for, and never an agent), `decision_needs` the role that is, so a screen can say why its buttons are off
            var needed = StoreChangeRequests.Approver(model!);
            var token = context.ManagementToken();
            var canDecide = !StoreChangeRequests.IsAgent(token) && (ManagementAuthorization.Authorize(token, needed).Allowed || (needed == ServerRole.Author && ManagementAuthorization.Authorize(token, ServerRole.Admin).Allowed));
            return Results.Json(new JsonObject
            {
                ["requests"] = new JsonArray(StoreChangeRequests.List(directory, name, InstanceName(context), state.Length > 0 ? state : null).Select(r => (JsonNode)r.ToJson(withChanges: false)).ToArray()),
                ["can_decide"] = canDecide,
                ["decision_needs"] = needed.ToString().ToLowerInvariant(),
                ["approval"] = model!.Approval.ToString().ToLowerInvariant(),
            });
        });

        app.MapGet(prefix + "/requests/{id}", (string name, string id, HttpContext context) =>
        {
            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Viewer);
            if (!decision.Allowed) return Denied(decision);
            return StoreModels.IsValidName(name) && StoreChangeRequests.Find(directory, name, InstanceName(context), id) is { } request
                ? Results.Json(request.ToJson()) : Results.NotFound(new { error = "There is no such change request." });
        });

        app.MapPost(prefix + "/requests/{id}/approve", (string name, string id, HttpContext context) => DecideAsync(directory, connections, name, id, context, approve: true));
        app.MapPost(prefix + "/requests/{id}/reject", (string name, string id, HttpContext context) => DecideAsync(directory, connections, name, id, context, approve: false));
    }

    private static (List<RowChange>? Changes, IResult? Problem) ParseChanges(JsonArray list)
    {
        var (changes, problem) = RowJson.Changes(list);
        return (changes, problem is null ? null : Results.BadRequest(new { error = problem }));
    }

    /// <summary>
    /// Approving applies the proposed changes as they were, in one transaction, through the same writer (every rule, version check and audit row), with the approver as the audit's actor and the proposer named beside them;
    /// a request the rules or a newer version refuse stays pending and says why. Rejecting only records the decision. A decided request is not decided again.
    /// </summary>
    private static async Task<IResult> DecideAsync(string directory, StoreConnections connections, string name, string id, HttpContext context, bool approve)
    {
        var (model, problem) = await ModelAsync(directory, name, context.Request.RouteValues["instance"] as string);
        if (problem is not null) return problem;
        var needed = StoreChangeRequests.Approver(model!);
        var decision = ManagementAuthorization.Authorize(context.ManagementToken(), needed);
        if (!decision.Allowed && needed == ServerRole.Author) decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Admin);
        if (!decision.Allowed) return Denied(decision);
        var token = context.ManagementToken();
        if (StoreChangeRequests.IsAgent(token)) return Results.Json(new { error = "An agent cannot decide a change request." }, statusCode: StatusCodes.Status403Forbidden);
        var instance = InstanceName(context);
        if (StoreChangeRequests.Find(directory, name, instance, id) is not { } request) return Results.NotFound(new { error = "There is no such change request." });
        if (request.State != ChangeRequest.Pending) return Results.Json(new { error = $"This change request was already {request.State}." }, statusCode: StatusCodes.Status409Conflict);
        JsonNode? body = null;
        if (context.Request.ContentLength is > 0)
        {
            try { body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted); }
            catch (JsonException) { return Results.BadRequest(new { error = "The body is not JSON." }); }
        }
        var note = (string?)body?["note"];
        if (!approve) return Results.Json(StoreChangeRequests.Decide(directory, request, ChangeRequest.Rejected, token, note, DateTimeOffset.UtcNow).ToJson(withChanges: false));

        var (connectionName, schema, instanceId, notThere) = Where(directory, model!, context);
        if (notThere is not null) return notThere;
        var (changes, bad) = ParseChanges(request.Changes);
        if (bad is not null) return bad;
        try
        {
            await using var connection = await connections.OpenAsync(connectionName, context.RequestAborted);
            var actor = new WriteActor(token.SubjectId, $"{token.SubjectName ?? token.SubjectId}, approving {request.ProposedByName ?? request.ProposedBy}'s change request {request.Id}");
            await new ModelWriter(model!, connection, connections.EngineOf(connectionName), schema: schema, instance: instanceId).ApplyAsync(changes!, actor, context.RequestAborted);
        }
        catch (Exception ex) when (Known(ex)) { return Fail(ex); }
        return Results.Json(StoreChangeRequests.Decide(directory, request, ChangeRequest.Approved, token, note, DateTimeOffset.UtcNow).ToJson(withChanges: false));
    }

    private static JsonArray Statements(StoragePlan plan) => Statements(plan.Statements);

    private static JsonArray Statements(IReadOnlyList<StorageStatement> statements) => new(statements.Select(s => (JsonNode)new JsonObject { ["object"] = s.Object, ["description"] = s.Description, ["text"] = s.Text }).ToArray());
}
