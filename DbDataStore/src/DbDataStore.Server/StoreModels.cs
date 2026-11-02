using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;
using DbDataKit.DataModel;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataStore.Server;

/// <summary>
/// The models a DbDataStore server holds (plan 19, Z08): one <c>model/1</c> file per model in the <c>models</c> folder of the server's directory, named <c>&lt;model&gt;.yaml</c>, the model's own <c>name</c> the same.
/// A file is saved only if it loads without errors, so what is on disk is always a model the server can use; a rejected file answers with the loader's diagnostics, each naming the file and line.
/// Reading is for a viewer, changing is for an author or an administrator, removing is for an administrator. Nothing here touches a database: storage and rows come after.
/// </summary>
public static partial class StoreModels
{
    public const string Folder = "models";

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex NamePattern();

    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    public static string FileOf(string directory, string name) => Path.Combine(directory, Folder, name + ".yaml");

    public static IEnumerable<string> Names(string directory)
    {
        var folder = Path.Combine(directory, Folder);
        return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.yaml").Select(f => Path.GetFileNameWithoutExtension(f)).Order(StringComparer.Ordinal) : [];
    }

    /// <summary>Loads a model's text as the file <c>&lt;name&gt;.yaml</c>; the model's name must be the file's.</summary>
    public static DataModelLoadResult Check(string name, string text)
    {
        var file = name + ".yaml";
        var result = DataModelLoader.Load(text, file);
        if (result.Model is { } model && model.Name != name)
        {
            var diagnostics = result.Diagnostics.Append(new ConfigDiagnostic(DataModelLoader.BadName, ConfigSeverity.Error, file, 1, 1,
                $"The model is called '{model.Name}' but its file is {file}; a model's file is named for it.", $"Rename the file or set name: {name}.")).ToList();
            return new DataModelLoadResult(model, diagnostics);
        }
        return result;
    }

    private static JsonArray Diagnostics(IEnumerable<ConfigDiagnostic> diagnostics) => new(diagnostics.Select(d => (JsonNode)new JsonObject
    {
        ["code"] = d.Code,
        ["severity"] = d.Severity.ToString().ToLowerInvariant(),
        ["file"] = d.Path,
        ["line"] = d.Line,
        ["column"] = d.Column,
        ["message"] = d.Message,
        ["fix"] = d.Fix,
    }).ToArray());

    private static IResult Denied(AuthorizationDecision decision) =>
        Results.Json(new { error = $"This needs the {decision.Needed.ToString().ToLowerInvariant()} role." }, statusCode: StatusCodes.Status403Forbidden);

    private static JsonObject Schema(DataModel model) => new()
    {
        ["name"] = model.Name,
        ["title"] = model.Title,
        ["approval"] = model.Approval.ToString().ToLowerInvariant(),
        ["entities"] = new JsonArray(model.Entities.Select(e => (JsonNode)new JsonObject
        {
            ["name"] = e.Name,
            ["title"] = e.Title,
            ["key"] = new JsonArray(e.Key.Select(k => (JsonNode)JsonValue.Create(k)!).ToArray()),
            ["history"] = e.History.ToString().ToLowerInvariant(),
            ["role"] = e.Role.ToString().ToLowerInvariant(),
            ["attributes"] = new JsonArray(e.Attributes.Select(a => (JsonNode)new JsonObject
            {
                ["name"] = a.Name,
                ["title"] = a.Title,
                ["type"] = a.Type,
                ["kind"] = a.Canonical.Kind.ToString().ToLowerInvariant(),
                ["length"] = a.Canonical.Length,
                ["precision"] = a.Canonical.Precision,
                ["scale"] = a.Canonical.Scale,
                ["required"] = a.Required,
                ["unique"] = a.Unique,
                ["default"] = a.Default,
                ["pattern"] = a.Pattern,
                ["min"] = a.Min,
                ["max"] = a.Max,
                ["allowed"] = a.Allowed is null ? null : new JsonArray(a.Allowed.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
                ["references"] = a.References is null ? null : new JsonObject { ["model"] = a.References.Model, ["entity"] = a.References.Entity, ["attribute"] = a.References.Attribute },
            }).ToArray()),
            ["rules"] = new JsonArray((e.Rules ?? []).Select(r => (JsonNode)new JsonObject { ["kind"] = r.Kind.ToString(), ["attribute"] = r.Attribute, ["group_by"] = new JsonArray(r.GroupBy.Select(g => (JsonNode)JsonValue.Create(g)!).ToArray()) }).ToArray()),
        }).ToArray()),
    };

    public static void Map(IEndpointRouteBuilder app, string directory)
    {
        app.MapGet("/api/v1/models", (HttpContext context) =>
        {
            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Viewer);
            if (!decision.Allowed) return Denied(decision);
            var models = new JsonArray();
            foreach (var name in Names(directory))
            {
                var result = Check(name, File.ReadAllText(FileOf(directory, name)));
                models.Add(new JsonObject
                {
                    ["name"] = name,
                    ["title"] = result.Model?.Title,
                    ["valid"] = result.Ok,
                    ["entities"] = new JsonArray((result.Model?.Entities ?? []).Select(e => (JsonNode)JsonValue.Create(e.Name)!).ToArray()),
                });
            }
            return Results.Json(new JsonObject { ["models"] = models });
        });

        app.MapGet("/api/v1/models/{name}", (string name, HttpContext context) =>
        {
            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Viewer);
            if (!decision.Allowed) return Denied(decision);
            if (!IsValidName(name) || !File.Exists(FileOf(directory, name))) return Results.NotFound(new { error = $"There is no model '{name}'." });
            var text = File.ReadAllText(FileOf(directory, name));
            return Results.Json(new JsonObject { ["name"] = name, ["text"] = text, ["diagnostics"] = Diagnostics(Check(name, text).Diagnostics) });
        });

        // the model as data for a form or a grid (and a typed client): entities, keys, attributes with their types and rules, the history each entity keeps
        app.MapGet("/api/v1/models/{name}/schema", (string name, HttpContext context) =>
        {
            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Viewer);
            if (!decision.Allowed) return Denied(decision);
            if (!IsValidName(name) || !File.Exists(FileOf(directory, name))) return Results.NotFound(new { error = $"There is no model '{name}'." });
            var result = Check(name, File.ReadAllText(FileOf(directory, name)));
            return result.Model is { } model && result.Ok ? Results.Json(Schema(model)) : Results.Json(new { error = $"{name}.yaml is not a valid model." }, statusCode: StatusCodes.Status422UnprocessableEntity);
        });

        // a dry run: the diagnostics of a text as it would be saved under this name, nothing written
        app.MapPost("/api/v1/models/{name}/validate", async (string name, HttpContext context) =>
        {
            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Viewer);
            if (!decision.Allowed) return Denied(decision);
            if (!IsValidName(name)) return Results.BadRequest(new { error = NameProblem(name) });
            using var reader = new StreamReader(context.Request.Body);
            var result = Check(name, await reader.ReadToEndAsync(context.RequestAborted));
            return Results.Json(new JsonObject { ["valid"] = result.Ok, ["diagnostics"] = Diagnostics(result.Diagnostics) });
        });

        app.MapPut("/api/v1/models/{name}", async (string name, HttpContext context) =>
        {
            // roles are sets and Admin does not contain Author, but whoever may remove a model may certainly save one
            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Author);
            if (!decision.Allowed && ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Admin).Allowed) decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Admin);
            if (!decision.Allowed) return Denied(decision);
            if (!IsValidName(name)) return Results.BadRequest(new { error = NameProblem(name) });
            using var reader = new StreamReader(context.Request.Body);
            var text = await reader.ReadToEndAsync(context.RequestAborted);
            var result = Check(name, text);
            if (!result.Ok) return Results.Json(new JsonObject { ["error"] = $"{name}.yaml is not a valid model, so nothing was saved.", ["diagnostics"] = Diagnostics(result.Diagnostics) }, statusCode: StatusCodes.Status422UnprocessableEntity);
            var path = FileOf(directory, name);
            var existed = File.Exists(path);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, text.ReplaceLineEndings("\n"), new System.Text.UTF8Encoding(false), context.RequestAborted);
            File.Move(temporary, path, true);
            return Results.Json(new JsonObject { ["name"] = name, ["created"] = !existed, ["diagnostics"] = Diagnostics(result.Diagnostics) }, statusCode: existed ? StatusCodes.Status200OK : StatusCodes.Status201Created);
        });

        app.MapDelete("/api/v1/models/{name}", (string name, HttpContext context) =>
        {
            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Admin);
            if (!decision.Allowed) return Denied(decision);
            if (!IsValidName(name) || !File.Exists(FileOf(directory, name))) return Results.NotFound(new { error = $"There is no model '{name}'." });
            File.Delete(FileOf(directory, name));
            return Results.NoContent();
        });
    }

    private static string NameProblem(string name) => $"'{name}' is not a model name: lower-case letters, digits, '-' and '_', starting with a letter or digit, at most 64.";
}
