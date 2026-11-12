using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Config.Git;
using DbDataKit.Config.Yaml;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Configuration;

namespace DbDataGateway.Server.Management;

/// <summary>
/// The Gateway's serving settings, <c>server.yaml</c>, through the management API (plan 33, UE17): read it, check a draft against the same validator the host runs at build, edit a field of the draft, and write it.
/// <list type="bullet">
/// <item><c>GET /api/v1/config/server.yaml</c>: <c>{text, version, masked, protected, restart_keys}</c>; the text has every secret value masked (<c>********</c>), the version is the hash of the real file (admin).</item>
/// <item><c>POST /api/v1/config/server.yaml/check</c> <c>{text}</c>: <c>{valid, diagnostics, fields, changed, restart_keys, problems}</c>: whether the draft would be valid (the shared validator; nothing is written), the typed values of the form's fields as the draft sets them, which top-level keys differ from the file and which of them a running Gateway reads at start only (admin).</item>
/// <item><c>POST /api/v1/config/server.yaml/edit</c> <c>{text, set: {path: value}, remove: [path], list: {path: [items]}}</c>: the draft with those fields changed by the surgical YAML editor (comments and order kept); nothing is written, so the form and the text are one document and no second editor can disagree (admin).</item>
/// <item><c>PUT /api/v1/config/server.yaml</c> <c>{text, expected_version, note?}</c>: written as a commit of the configuration repository as the caller, atomically (a temporary file moved over), when the file is still the version read (else 409 with the current one), the draft is valid (else 400 with the validator's diagnostics and nothing written), no masked secret is left without its value, and neither the instance id nor the paired clients are changed (pairing keeps those). Audited by name and outcome, never the text (admin).</item>
/// </list>
/// A masked value (quoted, <c>'********'</c>, since a bare run of stars is a YAML alias) that comes back as it is keeps the secret in the file; a secret typed over it replaces it. A running Gateway rereads the file at once, but the modules, listeners, TLS, state and the settings of what they host are read when it starts: the answer lists the changed keys among them (<c>restart_keys</c>), and the screen says a restart is needed.
/// </summary>
public sealed partial class GatewayServerSettings(string configDirectory, GatewayManagement validation, Action reload, IAuditLog audit, GitCommitService? git, TimeProvider? clock = null)
{
    public const string Mask = "********";
    public const string File_ = "server.yaml";
    private const int MaxBytes = 256 * 1024;

    /// <summary>Keys the pairing commands keep: a change here would lock a console out or unpair it, so it is made by <c>server pair</c>, not by this editor.</summary>
    public static readonly IReadOnlyList<string> Protected = ["instanceId", "pairedClients"];

    /// <summary>Top-level keys a running Gateway reads when it starts (listeners, certificates, the modules it mounts, the state it opens, the hosts it runs): a change to one takes effect at the next start.</summary>
    public static readonly IReadOnlyList<string> RestartKeys =
        ["dataDirectory", "modules", "listen", "management", "tls", "identity", "apps", "store", "views", "mcp", "rest", "state", "secrets", "compression", "worker", "hosting", "telemetry", "definitionPollSeconds"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly WriteGuard _guard = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private string FilePath => Path.Combine(configDirectory, File_);

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private string? CurrentText() => System.IO.File.Exists(FilePath) ? System.IO.File.ReadAllText(FilePath) : null;

    private static ManagementResult Denied() => ManagementResult.Problem(403, "This call needs the admin role.");

    public async Task<ManagementResult> HandleAsync(string method, string route, string? body, ManagementCaller caller, CancellationToken cancellationToken)
    {
        if (!caller.Holds(ServerRole.Admin)) return Denied();
        var tail = route[(GatewayManagement.Prefix + "/config/server.yaml").Length..];
        switch (method, tail)
        {
            case ("GET", ""): return Read();
            case ("POST", "/check"): return Check(body);
            case ("POST", "/edit"): return Edit(body);
            case ("PUT", ""): return await WriteAsync(body, caller, cancellationToken);
            default: return ManagementResult.Problem(405, "server.yaml is read with GET, checked with POST .../check, edited with POST .../edit and written with PUT.");
        }
    }

    private static JsonObject? Parse(string? body)
    {
        try { return string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body) as JsonObject; }
        catch (JsonException) { return null; }
    }

    // ---- read ----

    private ManagementResult Read()
    {
        var text = CurrentText();
        return ManagementResult.Ok(new JsonObject
        {
            ["text"] = MaskSecrets(text ?? ""), ["version"] = text is null ? null : Sha(text), ["masked"] = text is not null && FindSecrets(text).Any(s => !IsReference(s.Value)),
            ["protected"] = new JsonArray([.. Protected.Select(p => (JsonNode)p)]), ["restart_keys"] = new JsonArray([.. RestartKeys.Select(p => (JsonNode)p)]),
        });
    }

    // ---- check ----

    private ManagementResult Check(string? body)
    {
        var json = Parse(body);
        if (json?["text"] is not JsonValue v || !v.TryGetValue<string>(out var draft)) return ManagementResult.Problem(400, "The body is a JSON object: {\"text\": \"<server.yaml>\"}.");
        var current = CurrentText() ?? "";
        var restored = Restore(draft, current, out var unresolved);
        var problems = new JsonArray();
        if (unresolved is not null) problems.Add(unresolved);
        var changed = ChangedKeys(current, restored ?? draft);
        foreach (var key in changed.Where(k => Protected.Contains(k, StringComparer.OrdinalIgnoreCase)))
            problems.Add($"`{key}` is kept by the pairing commands and is not changed here.");
        var answer = validation.WithCandidate(new Dictionary<string, string> { [File_] = restored ?? draft }, load =>
        {
            var o = load.Value;
            return ManagementResult.Ok(new JsonObject
            {
                ["valid"] = load.Ok && problems.Count == 0, ["diagnostics"] = GatewayManagement.DiagnosticsOf(load.Diagnostics),
                ["fields"] = o is null ? new JsonObject() : new JsonObject
                {
                    ["modules"] = new JsonArray([.. o.Modules.Select(m => (JsonNode)m)]),
                    ["views.cacheSeconds"] = o.Views.CacheSeconds,
                    ["views.export.mode"] = o.Views.Export.Mode,
                    ["views.export.path"] = o.Views.Export.Path,
                    ["logSql"] = o.LogSql,
                    // OpenTelemetry (plan 22a GP28): off unless enabled; read at start. The sampling ratio and the instance id stay in the text.
                    ["telemetry.enabled"] = o.Telemetry.Enabled,
                    ["telemetry.endpoint"] = o.Telemetry.Endpoint ?? "",
                    ["telemetry.protocol"] = o.Telemetry.Protocol,
                    // the scalar part of the semantic: section (plan 14a SL11); the hand-set models and the Analysis Services connections stay in the text
                    ["semantic.level"] = o.Semantic.Level,
                    ["semantic.combine.memoryMb"] = o.Semantic.Combine.MemoryMb,
                    ["semantic.combine.timeoutSeconds"] = o.Semantic.Combine.TimeoutSeconds,
                    ["semantic.combine.maxInputRows"] = o.Semantic.Combine.MaxInputRows,
                    // the feeds module's listener and limits (plan 14a SL31)
                    ["rest.port"] = o.Rest.Port,
                    ["rest.defaultLimit"] = o.Rest.DefaultLimit,
                    ["rest.maxRows"] = o.Rest.MaxRows,
                },
            });
        });
        var result = answer.Body.AsObject();
        result["changed"] = new JsonArray([.. changed.Select(k => (JsonNode)k)]);
        result["restart_keys"] = new JsonArray([.. changed.Where(k => RestartKeys.Contains(k, StringComparer.OrdinalIgnoreCase)).Select(k => (JsonNode)k)]);
        result["problems"] = problems;
        return new ManagementResult(answer.Status, result);
    }

    // ---- edit ----

    private ManagementResult Edit(string? body)
    {
        var json = Parse(body);
        if (json?["text"] is not JsonValue v || !v.TryGetValue<string>(out var text)) return ManagementResult.Problem(400, "The body is a JSON object: {\"text\": \"<server.yaml>\", \"set\": {\"views.cacheSeconds\": 60}, \"remove\": [\"views.export.mode\"], \"list\": {\"modules\": [\"flightsql\"]}}.");
        string? problem = null;
        void Apply(EditResult edit) { if (edit.Text is not null) text = edit.Text; else problem ??= edit.Problem?.Message ?? "The edit could not be made."; }
        if (json["remove"] is JsonArray removes)
            foreach (var path in removes.Select(n => n?.GetValue<string>()).OfType<string>())
            {
                if (!ValidPath(path)) { problem ??= $"`{path}` is not a setting path."; continue; }
                var edit = YamlEditor.Remove(text, path, File_);
                if (edit.Text is not null) text = edit.Text;   // a key that is not there is already removed
            }
        if (json["list"] is JsonObject lists)
            foreach (var (path, items) in lists)
            {
                if (!ValidPath(path) || items is not JsonArray array) { problem ??= $"`{path}` is not a setting path with a list."; continue; }
                var values = array.Select(n => n?.GetValue<string>() ?? "").ToArray();
                if (values.Any(x => !Regex.IsMatch(x, @"^[A-Za-z0-9_.-]+$"))) { problem ??= $"The items of `{path}` are plain words."; continue; }
                var without = YamlEditor.Remove(text, path, File_);
                if (without.Text is not null) text = without.Text;
                Apply(YamlEditor.Set(text, path, YamlValue.Raw("[" + string.Join(", ", values) + "]"), File_));
            }
        if (json["set"] is JsonObject sets)
            foreach (var (path, value) in sets)
            {
                if (!ValidPath(path)) { problem ??= $"`{path}` is not a setting path."; continue; }
                var raw = value switch
                {
                    JsonValue j when j.TryGetValue<bool>(out var b) => YamlValue.Raw(b ? "true" : "false"),
                    JsonValue j when j.TryGetValue<int>(out var n) => YamlValue.Raw(n.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    JsonValue j when j.TryGetValue<string>(out var s) => new YamlValue(s),
                    _ => (YamlValue?)null,
                };
                if (raw is null) { problem ??= $"The value of `{path}` is a number, a switch or text."; continue; }
                Apply(YamlEditor.Set(text, path, raw.Value, File_));
            }
        return problem is null ? ManagementResult.Ok(new JsonObject { ["text"] = text }) : ManagementResult.Problem(400, problem);
    }

    private static bool ValidPath(string path) => Regex.IsMatch(path, @"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)*$");

    // ---- write ----

    private async Task<ManagementResult> WriteAsync(string? body, ManagementCaller caller, CancellationToken cancellationToken)
    {
        var json = Parse(body);
        if (json?["text"] is not JsonValue v || !v.TryGetValue<string>(out var draft) || string.IsNullOrWhiteSpace(draft))
            return ManagementResult.Problem(400, "The body is a JSON object: {\"text\": \"<server.yaml>\", \"expected_version\": \"<the version read>\", \"note\": \"<why>\"}. An empty file is not written.");
        if (Encoding.UTF8.GetByteCount(draft) > MaxBytes) return ManagementResult.Problem(400, $"server.yaml is at most {MaxBytes / 1024} KB.");
        var expected = json["expected_version"] is JsonValue e && e.TryGetValue<string>(out var ev) ? ev : null;
        var note = json["note"] is JsonValue n && n.TryGetValue<string>(out var nv) && !string.IsNullOrWhiteSpace(nv) ? nv.Trim() : null;

        var result = await _guard.RunAsync(File_, expected, () => Task.FromResult(CurrentText() is { } now ? Sha(now) : null), () =>
        {
            var current = CurrentText() ?? "";
            var text = Restore(draft, current, out var unresolved);
            if (text is null) return Task.FromResult<Written>(new(null, null, [unresolved!], []));
            var changed = ChangedKeys(current, text);
            var locked = changed.Where(k => Protected.Contains(k, StringComparer.OrdinalIgnoreCase)).Select(k => $"`{k}` is kept by the pairing commands and is not changed here.").ToList();
            if (locked.Count > 0) return Task.FromResult<Written>(new(null, null, locked, []));
            var checkedBody = validation.ValidateFiles(new Dictionary<string, string> { [File_] = text });
            if (checkedBody.Body["valid"]?.GetValue<bool>() != true) return Task.FromResult<Written>(new(null, checkedBody.Body, [], []));
            if (text == current) return Task.FromResult<Written>(new(text, checkedBody.Body, [], []));
            Directory.CreateDirectory(configDirectory);
            var temp = FilePath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            System.IO.File.WriteAllText(temp, text, new UTF8Encoding(false));
            System.IO.File.Move(temp, FilePath, overwrite: true);
            return Task.FromResult<Written>(new(text, checkedBody.Body, [], changed));
        }, cancellationToken);

        if (!result.Applied)
        {
            Record(caller, "server.yaml write", "stale");
            return new ManagementResult(409, new JsonObject { ["error"] = "server.yaml changed since it was read; read it again.", ["code"] = "version_conflict", ["current_version"] = result.CurrentVersion });
        }
        var written = result.Value!;
        if (written.Problems.Count > 0)
        {
            Record(caller, "server.yaml write", "refused");
            return new ManagementResult(400, new JsonObject { ["error"] = written.Problems[0], ["problems"] = new JsonArray([.. written.Problems.Select(p => (JsonNode)p)]) });
        }
        if (written.Text is null)
        {
            Record(caller, "server.yaml write", "refused");
            return new ManagementResult(400, new JsonObject { ["error"] = "The change is not valid, so nothing was written.", ["detail"] = written.Validation?.DeepClone() });
        }
        var restart = written.Changed.Where(k => RestartKeys.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        string? commit = null;
        if (written.Changed.Count > 0)
        {
            try
            {
                git?.CommitChanges([FilePath], $"Edit server.yaml ({string.Join(", ", written.Changed)}){(note is null ? "" : ": " + note)}", caller.Author);
                commit = git?.GetHistory("", 1).FirstOrDefault()?.Sha;
            }
            catch (Exception ex) when (ex is LibGit2Sharp.LibGit2SharpException or IOException) { /* written; the history missed this one */ }
            reload();
        }
        Record(caller, "server.yaml write", written.Changed.Count == 0 ? "unchanged" : "written: " + string.Join(", ", written.Changed) + (restart.Count > 0 ? " (restart needed)" : ""));
        return ManagementResult.Ok(new JsonObject
        {
            ["version"] = Sha(written.Text), ["changed"] = new JsonArray([.. written.Changed.Select(k => (JsonNode)k)]), ["restart_keys"] = new JsonArray([.. restart.Select(k => (JsonNode)k)]),
            ["restart_needed"] = restart.Count > 0, ["commit"] = commit, ["diagnostics"] = written.Validation?["diagnostics"]?.DeepClone(),
        });
    }

    private sealed record Written(string? Text, JsonNode? Validation, IReadOnlyList<string> Problems, IReadOnlyList<string> Changed);

    private void Record(ManagementCaller caller, string command, string outcome) => audit.Append(caller.Audit(command, File_, outcome, _clock.GetUtcNow()));

    // ---- top-level keys ----

    /// <summary>The top-level keys whose block differs between two texts (comments and blank lines do not count), in the order they appear.</summary>
    public static IReadOnlyList<string> ChangedKeys(string before, string after)
    {
        var a = Blocks(before);
        var b = Blocks(after);
        return [.. a.Keys.Concat(b.Keys.Where(k => !a.ContainsKey(k))).Where(k => !a.TryGetValue(k, out var x) || !b.TryGetValue(k, out var y) || x != y)];
    }

    private static Dictionary<string, string> Blocks(string text)
    {
        var blocks = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        StringBuilder? open = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r', ' ', '\t');
            if (line.Length == 0 || line.TrimStart().StartsWith('#')) continue;
            if (TopKey().Match(line) is { Success: true } m)
            {
                open = new StringBuilder();
                blocks[m.Groups["key"].Value] = open;
                open.Append(line[m.Groups["key"].Length..]).Append('\n');
            }
            else open?.Append(line).Append('\n');
        }
        return blocks.ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal);
    }

    [GeneratedRegex(@"^(?<key>[A-Za-z_][A-Za-z0-9_-]*)\s*:")]
    private static partial Regex TopKey();

    // ---- secrets ----

    private sealed record Found(int Start, int Length, string Key, string Value, bool Quoted, string Raw, bool Keyed);

    [GeneratedRegex(@"(?<key>\b[A-Za-z_]*(?:[Pp]assword|[Ss]ecret|[Ss]igningKey|[Aa]piKey|[Tt]oken)[A-Za-z_]*)(?<sep>[ \t]*:[ \t]*)(?<value>'[^'\r\n]*'|""[^""\r\n]*""|[^,}\r\n#]+)")]
    private static partial Regex KeyedSecret();

    [GeneratedRegex(@"(?<key>\b(?:Password|Pwd))(?<sep>\s*=\s*)(?<value>[^;'""\r\n,}]*)", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionSecret();

    private static bool IsReference(string value) => value.StartsWith("secret:", StringComparison.Ordinal) || value.StartsWith("env:", StringComparison.Ordinal) || value.StartsWith("${", StringComparison.Ordinal) || value.Length == 0;

    private static List<Found> FindSecrets(string text)
    {
        var found = new List<Found>();
        foreach (Match m in KeyedSecret().Matches(text))
        {
            var group = m.Groups["value"];
            var quoted = group.Value.Length >= 2 && group.Value[0] is '\'' or '"';
            var inner = (quoted ? group.Value[1..^1] : group.Value).TrimEnd();
            var length = quoted ? group.Length : group.Value.TrimEnd().Length;
            found.Add(new Found(group.Index, length, m.Groups["key"].Value, inner, quoted, text.Substring(group.Index, length), Keyed: true));
        }
        foreach (Match m in ConnectionSecret().Matches(text))
        {
            var group = m.Groups["value"];
            if (found.Any(f => group.Index >= f.Start && group.Index < f.Start + f.Length)) continue;
            found.Add(new Found(group.Index, group.Length, m.Groups["key"].Value.ToLowerInvariant() + "=", group.Value, false, group.Value, Keyed: false));
        }
        return [.. found.OrderBy(f => f.Start)];
    }

    /// <summary>The text with every secret value (not a reference to one) replaced by <see cref="Mask"/>.</summary>
    public static string MaskSecrets(string text)
    {
        var builder = new StringBuilder(text);
        foreach (var f in FindSecrets(text).Where(f => !IsReference(f.Value) && f.Value != Mask).OrderByDescending(f => f.Start))
            builder.Remove(f.Start, f.Length).Insert(f.Start, f.Keyed ? $"'{Mask}'" : Mask);   // quoted: a bare run of stars is a YAML alias
        return builder.ToString();
    }

    /// <summary>
    /// The draft with each masked value put back as the file has it (the n-th masked value of a key in the draft is the file's n-th secret of that key); null, with the reason, when a mask has nothing to restore:
    /// the person wrote <c>********</c> where the file keeps no secret, which would store the mask as a password.
    /// </summary>
    private static string? Restore(string draft, string current, out string? unresolved)
    {
        unresolved = null;
        var inFile = FindSecrets(current).Where(f => !IsReference(f.Value)).GroupBy(f => f.Key).ToDictionary(g => g.Key, g => g.ToList());
        var seen = new Dictionary<string, int>();
        var builder = new StringBuilder(draft);
        var edits = new List<(int Start, int Length, string With)>();
        foreach (var f in FindSecrets(draft))
        {
            var index = seen.GetValueOrDefault(f.Key);
            if (f.Value != Mask) { if (!IsReference(f.Value)) seen[f.Key] = index + 1; continue; }
            if (!inFile.TryGetValue(f.Key, out var list) || index >= list.Count)
            {
                unresolved = $"`{f.Key}` is shown as {Mask} because the file keeps a secret there; this one has none to keep. Type the value, or remove the line.";
                return null;
            }
            seen[f.Key] = index + 1;
            var original = list[index];
            edits.Add((f.Start, f.Length, original.Raw));   // exactly as the file has it, quotes and all, so a roundtrip is no change
        }
        foreach (var (start, length, with) in edits.OrderByDescending(x => x.Start)) builder.Remove(start, length).Insert(start, with);
        return builder.ToString();
    }
}
