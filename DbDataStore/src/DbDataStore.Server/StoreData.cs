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

    private static JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        DateTimeOffset d => JsonValue.Create(d.ToString("O")),
        DateTime d => JsonValue.Create(d.ToString("O")),
        decimal d => JsonValue.Create(d),
        byte[] b => JsonValue.Create(Convert.ToBase64String(b)),
        string or bool or int or long or short or double => JsonSerializer.SerializeToNode(value),
        _ => JsonValue.Create(value.ToString()),
    };

    private static JsonObject Row(StoredRow row)
    {
        var values = new JsonObject();
        foreach (var (key, value) in row.Values) values[key] = ToJson(value);
        return new JsonObject
        {
            ["values"] = values,
            ["version"] = row.Version,
            ["valid_from"] = row.ValidFrom?.ToString("O"),
            ["valid_to"] = row.ValidTo?.ToString("O"),
        };
    }

    private static JsonObject KeyJson(IReadOnlyDictionary<string, object?> key)
    {
        var node = new JsonObject();
        foreach (var (name, value) in key) node[name] = ToJson(value);
        return node;
    }

    private static Dictionary<string, object?> ObjectOf(JsonNode? node) =>
        node is JsonObject o ? o.ToDictionary(p => p.Key, p => p.Value is null ? null : (object?)JsonSerializer.Deserialize<JsonElement>(p.Value.ToJsonString())) : [];

    private static async Task<(DataModel? Model, IResult? Problem)> ModelAsync(string directory, string name)
    {
        if (!StoreModels.IsValidName(name) || !File.Exists(StoreModels.FileOf(directory, name))) return (null, Results.NotFound(new { error = $"There is no model '{name}'." }));
        var result = StoreModels.Check(name, await File.ReadAllTextAsync(StoreModels.FileOf(directory, name)));
        return result.Ok ? (result.Model, null) : (null, Results.Json(new { error = $"{name}.yaml is not a valid model." }, statusCode: StatusCodes.Status422UnprocessableEntity));
    }

    private static IResult Fail(Exception ex) => ex switch
    {
        StoreConnectionException => Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway),
        WriteRefusedException refused => Results.Json(new JsonObject
        {
            ["error"] = "The change breaks the model's rules, so nothing was written.",
            ["violations"] = new JsonArray(refused.Violations.Select(v => (JsonNode)new JsonObject
            {
                ["rule"] = v.Rule, ["entity"] = v.Entity, ["attribute"] = v.Attribute, ["key"] = KeyJson(v.Key), ["message"] = v.Message,
            }).ToArray()),
        }, statusCode: StatusCodes.Status422UnprocessableEntity),
        VersionConflictException conflict => Results.Json(new { error = conflict.Message, expected_version = conflict.Expected, current_version = conflict.Current }, statusCode: StatusCodes.Status409Conflict),
        RowNotFoundException => Results.NotFound(new { error = ex.Message }),
        StoreNotSupportedException or StorageException => Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status422UnprocessableEntity),
        _ => throw ex,
    };

    private static bool Known(Exception ex) => ex is StoreConnectionException or WriteRefusedException or VersionConflictException or RowNotFoundException or StoreNotSupportedException or StorageException;

    /// <summary>Where an instance of the model in the request is kept, or the 404 that says why not. The model's own copy (no instance in the path, or <c>default</c>) is always there.</summary>
    private static (string Connection, string Schema, string? Instance, IResult? Problem) Where(string directory, DataModel model, HttpContext context)
    {
        var (connection, schema, instance, problem) = StoreInstances.Resolve(directory, model, context.Request.RouteValues["instance"] as string);
        return problem is null ? (connection!, schema!, instance, null) : ("", "", null, Results.NotFound(new { error = problem }));
    }

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
            var (model, problem) = await ModelAsync(directory, name);
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
            var (model, problem) = await ModelAsync(directory, name);
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
                return Results.Json(new JsonObject { ["engine"] = plan.Engine, ["statements"] = Statements(plan) }, statusCode: StatusCodes.Status201Created);
            }
            catch (Exception ex) when (Known(ex)) { return Fail(ex); }
        });

        // one row by its key: ?key.<attribute>=<value>; with ?as_of=<time> the version that held then, with ?history=true every version
        app.MapGet(prefix + "/entities/{entity}/row", async (string name, string entity, HttpContext context) =>
        {
            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Viewer);
            if (!decision.Allowed) return Denied(decision);
            var (model, problem) = await ModelAsync(directory, name);
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
            var (model, problem) = await ModelAsync(directory, name);
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

        // { "changes": [ { "action": "insert|update|delete", "entity": "...", "key": {...}, "expected_version": 3, "values": {...}, "effective_from": "..." } ] }
        app.MapPost(prefix + "/changes", async (string name, HttpContext context) =>
        {
            var decision = Writer(context);
            if (!decision.Allowed) return Denied(decision);
            var (model, problem) = await ModelAsync(directory, name);
            if (problem is not null) return problem;
            var (connectionName, schema, instanceId, notThere) = Where(directory, model!, context);
            if (notThere is not null) return notThere;
            JsonNode? body;
            try { body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted); }
            catch (JsonException) { return Results.BadRequest(new { error = "The body is not JSON." }); }
            if (body?["changes"] is not JsonArray list || list.Count == 0) return Results.BadRequest(new { error = "The body needs a non-empty \"changes\" array." });
            var changes = new List<RowChange>();
            foreach (var item in list)
            {
                if (item is not JsonObject c || (string?)c["entity"] is not { } entity || !Enum.TryParse<RowAction>((string?)c["action"], true, out var action))
                    return Results.BadRequest(new { error = "Each change needs an \"entity\" and an \"action\" of insert, update or delete." });
                DateTimeOffset? from = c["effective_from"] is { } f && DateTimeOffset.TryParse((string?)f, out var parsed) ? parsed : null;
                var values = ObjectOf(c["values"]);
                var key = ObjectOf(c["key"]);
                long? version = c["expected_version"] is { } v ? (long)v : null;
                if (action != RowAction.Insert && version is null) return Results.BadRequest(new { error = "An update or delete needs \"expected_version\"." });
                changes.Add(new RowChange(action, entity, action == RowAction.Insert ? new Dictionary<string, object?>() : key, version, values) { EffectiveFrom = from });
            }
            var token = context.ManagementToken();
            try
            {
                await using var connection = await connections.OpenAsync(connectionName, context.RequestAborted);
                await new ModelWriter(model!, connection, connections.EngineOf(connectionName), schema: schema, instance: instanceId).ApplyAsync(changes, new WriteActor(token.SubjectId, token.SubjectName), context.RequestAborted);
                return Results.Json(new { applied = changes.Count });
            }
            catch (Exception ex) when (Known(ex)) { return Fail(ex); }
        });
    }

    private static JsonArray Statements(StoragePlan plan) => new(plan.Statements.Select(s => (JsonNode)new JsonObject { ["object"] = s.Object, ["description"] = s.Description, ["text"] = s.Text }).ToArray());
}
