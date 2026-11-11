using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;
using DbDataKit.DataModel;
using DbDataKit.Store;
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

    /// <summary>The version of a model's text: its SHA-256. Every text that is saved is kept under it, so an instance that has not been migrated yet keeps running the version it was made or last migrated at (plan 26, Z13).</summary>
    public static string VersionOf(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string VersionFileOf(string directory, string name, string version) => Path.Combine(directory, Folder, "versions", name, version + ".yaml");

    /// <summary>Keeps a text under its version if it is not kept yet, and returns the version.</summary>
    public static string Snapshot(string directory, string name, string text)
    {
        var version = VersionOf(text);
        var file = VersionFileOf(directory, name, version);
        if (!File.Exists(file))
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temporary = file + ".tmp";
            File.WriteAllText(temporary, text, new System.Text.UTF8Encoding(false));
            File.Move(temporary, file, true);
        }
        return version;
    }

    /// <summary>The version of the model as it is saved now (kept, if it was saved before versions were).</summary>
    public static string CurrentVersion(string directory, string name) => Snapshot(directory, name, File.ReadAllText(FileOf(directory, name)));

    /// <summary>A kept text, or null when there is none under that version.</summary>
    public static string? TextOfVersion(string directory, string name, string version) =>
        Regex.IsMatch(version, "^[0-9a-f]{64}$") && File.Exists(VersionFileOf(directory, name, version)) ? File.ReadAllText(VersionFileOf(directory, name, version)) : null;

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

    private static bool IsAdmin(HttpContext context) => ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Admin).Allowed;

    private static bool CanWrite(HttpContext context) =>
        ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Author).Allowed || ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Admin).Allowed;

    private static IResult Denied(AuthorizationDecision decision) =>
        Results.Json(new { error = $"This needs the {decision.Needed.ToString().ToLowerInvariant()} role." }, statusCode: StatusCodes.Status403Forbidden);

    internal static JsonObject Schema(DataModel model) => ModelSchemaJson.Of(model);

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
            // `version` is what a save quotes (`?expected_version=`); `can` is what this caller's roles allow (`admin` also covers the storage and migration routes), so a screen offers only that (every route checks again)
            return Results.Json(new JsonObject
            {
                ["name"] = name, ["text"] = text, ["version"] = VersionOf(text), ["diagnostics"] = Diagnostics(Check(name, text).Diagnostics),
                ["can"] = new JsonObject { ["save"] = CanWrite(context), ["remove"] = IsAdmin(context), ["admin"] = IsAdmin(context) },
            });
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
            // a save may quote the version it read: a model changed since (or removed) is refused with the current version, so two people's edits do not overwrite each other
            if (context.Request.Query["expected_version"].ToString() is { Length: > 0 } expected)
            {
                var held = File.Exists(FileOf(directory, name)) ? VersionOf(File.ReadAllText(FileOf(directory, name))) : null;
                if (held != expected)
                    return Results.Json(new JsonObject { ["error"] = held is null ? $"The model '{name}' was removed since you read it." : $"The model '{name}' was changed by someone else since you read it.", ["current_version"] = held }, statusCode: StatusCodes.Status409Conflict);
            }
            var result = Check(name, text);
            if (!result.Ok) return Results.Json(new JsonObject { ["error"] = $"{name}.yaml is not a valid model, so nothing was saved.", ["diagnostics"] = Diagnostics(result.Diagnostics) }, statusCode: StatusCodes.Status422UnprocessableEntity);
            var path = FileOf(directory, name);
            var existed = File.Exists(path);
            text = DbDataKit.DataModel.DataModelLoader.WithCurrentVersion(text);   // a save writes the current format, never the one that was read (plan 33 6d)
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, text.ReplaceLineEndings("\n"), new System.Text.UTF8Encoding(false), context.RequestAborted);
            File.Move(temporary, path, true);
            var version = Snapshot(directory, name, text.ReplaceLineEndings("\n"));
            return Results.Json(new JsonObject { ["name"] = name, ["created"] = !existed, ["version"] = version, ["diagnostics"] = Diagnostics(result.Diagnostics) }, statusCode: existed ? StatusCodes.Status200OK : StatusCodes.Status201Created);
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

    public static string NameProblem(string name) => $"'{name}' is not a model name: lower-case letters, digits, '-' and '_', starting with a letter or digit, at most 64.";
}
