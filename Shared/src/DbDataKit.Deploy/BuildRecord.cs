using System.Text.Json.Nodes;

namespace DbDataKit.Deploy;

/// <summary>
/// The record of the build a deployed version comes from (<c>build-record/1</c>): the Workspace server (its instance id), the project, its kind, the workspace and the build, the commit the build was made from and whether the draft had
/// changes not yet committed, the item of the build that was sent and the SHA-256 of its list of files (<see cref="BuildItem.ManifestSha256"/>), and what the kind's validators found (<c>checks</c>: whether the build passed them, and each
/// finding with its severity, code, message, file and line). The Workspace server describes a build (<see cref="Describe"/>); the control service writes the record of the item it deploys (<see cref="Text"/>), signs it with the
/// version, and the Runner or Gateway keeps the text as it was sent.
/// Design: docs/publishing.md#the-build-record
/// </summary>
public static class BuildRecord
{
    public const string Schema = "build-record/1";

    /// <summary>
    /// The Workspace server's description of a build: <paramref name="result"/> is the build's result file (its <c>status</c>, <c>kind</c>, <c>commit</c>, <c>uncommitted_changes</c> and <c>diagnostics</c>); the server, project, workspace
    /// and build are named by the caller.
    /// </summary>
    public static JsonObject Describe(string server, string project, string kind, string workspace, Guid build, string status, string? commit, JsonObject? result)
    {
        var findings = new JsonArray();
        foreach (var finding in (result?["diagnostics"] as JsonArray ?? []).OfType<JsonObject>()) findings.Add(Finding(finding));
        return new JsonObject
        {
            ["server"] = server, ["project"] = project, ["kind"] = kind, ["workspace"] = workspace, ["build"] = build.ToString("D"), ["status"] = status,
            ["commit"] = commit ?? (string?)result?["commit"], ["uncommitted_changes"] = result?["uncommitted_changes"]?.GetValue<bool>() ?? false,
            ["checks"] = new JsonObject { ["passed"] = status == "succeeded", ["findings"] = findings },
        };
    }

    /// <summary>
    /// The record of <paramref name="item"/> of the build <paramref name="build"/> describes (as <see cref="Describe"/> wrote it), as one line of JSON in a fixed order. Only the fields of the record are read from the description, each of
    /// its type, so what the Workspace server answers cannot add anything else to what is signed. A description without one of them is a <see cref="DeployException"/> naming it.
    /// </summary>
    public static string Text(JsonObject build, BuildItem item)
    {
        static string Need(JsonObject o, string field) => o[field] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : throw new DeployException($"The Workspace server's record of the build has no \"{field}\".", 502);
        var checks = build["checks"] as JsonObject ?? throw new DeployException("The Workspace server's record of the build has no \"checks\".", 502);
        var findings = new JsonArray();
        foreach (var finding in (checks["findings"] as JsonArray ?? []).OfType<JsonObject>()) findings.Add(Finding(finding));
        var record = new JsonObject
        {
            ["schema"] = Schema,
            ["server"] = Need(build, "server"), ["project"] = Need(build, "project"), ["kind"] = Need(build, "kind"), ["workspace"] = Need(build, "workspace"), ["build"] = Need(build, "build"),
            ["commit"] = build["commit"] is JsonValue c && c.TryGetValue<string>(out var commit) ? commit : null,
            ["uncommitted_changes"] = build["uncommitted_changes"] is JsonValue u && u.TryGetValue<bool>(out var dirty) && dirty,
            ["item"] = item.Id, ["manifest_sha256"] = item.ManifestSha256,
            ["checks"] = new JsonObject { ["passed"] = checks["passed"] is JsonValue p && p.TryGetValue<bool>(out var passed) && passed, ["findings"] = findings },
        };
        return record.ToJsonString();
    }

    /// <summary>A record's text read back; null when it is not a build record of this schema.</summary>
    public static JsonObject? Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text) is JsonObject o && (string?)o["schema"] == Schema ? o : null; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static JsonObject Finding(JsonObject finding)
    {
        static string? Str(JsonObject o, string field) => o[field] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        return new JsonObject
        {
            ["severity"] = Str(finding, "severity") ?? "error", ["code"] = Str(finding, "code"), ["message"] = Str(finding, "message") ?? "",
            ["file"] = Str(finding, "file"), ["line"] = finding["line"] is JsonValue l && l.TryGetValue<int>(out var line) ? line : null,
        };
    }
}
