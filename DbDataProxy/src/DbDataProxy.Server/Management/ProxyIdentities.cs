using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Config.Git;
using DbDataKit.Config.Yaml;
using DbDataKit.Web.Trust;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Configuration;

namespace DbDataProxy.Server.Management;

/// <summary>
/// Who may use the proxy, through the management API (plan 13, P05): the Flight users (a password), the Windows accounts (checked by the operating system), the JWT claim mappings and pass-through. The users and
/// accounts are the <c>users:</c> list of <c>proxy/users.yaml</c>, edited one entry at a time without touching the rest of the file; a write is held to the same rules as a data source's (validated as the configuration would be,
/// refused with the diagnostics if invalid, checked against the version the caller read, committed as the person, audited, applied at once).
/// <para>
/// <b>A password is accepted and never returned.</b> It arrives over the pinned, signed channel, is hashed here with the proxy's own PBKDF2 and only the hash is written; no answer carries a hash, a password or the JWT
/// signing key, only whether there is one. The JWT mappings and the pass-through switch are shown as they are in effect; the mappings are changed in <c>proxy/auth.yaml</c> for now.
/// </para>
/// </summary>
public sealed partial class ProxyIdentities(
    string configDirectory, ProxyManagement validation, Action reload, Func<ProxyOptions> options, IAuditLog audit, GitCommitService? git, TimeProvider? clock = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly WriteGuard _guard = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private string UsersFile => Path.Combine(configDirectory, "proxy", "users.yaml");
    private string AuthFile => Path.Combine(configDirectory, "proxy", "auth.yaml");

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public async Task<ManagementResult> HandleAsync(string method, string[] rest, string? body, string? expectedVersion, ManagementCaller caller, CancellationToken cancellationToken)
    {
        // rest[0] is "identities", "users" or "auth"
        switch (rest)
        {
            case ["identities"] when method == "GET": return ManagementResult.Ok(View());
            case ["users", var name] when method == "PUT": return await WriteUserAsync(Uri.UnescapeDataString(name), body, caller, cancellationToken);
            case ["users", var name] when method == "DELETE": return await DeleteUserAsync(Uri.UnescapeDataString(name), expectedVersion, caller, cancellationToken);
            case ["auth"] when method == "PUT": return await WriteAuthAsync(body, caller, cancellationToken);
            default: return ManagementResult.Problem(404, "There is no such call.");
        }
    }

    // ---- the view ----

    private JsonObject View()
    {
        var o = options();
        var users = new JsonArray();
        var windows = new JsonArray();
        foreach (var entry in UserEntries())
        {
            var node = new JsonObject
            {
                ["name"] = Field(entry, "name"), ["auth_type"] = (Field(entry, "authType") ?? "password").ToLowerInvariant(),
                ["has_password"] = !string.IsNullOrEmpty(Field(entry, "passwordHash")),
                ["data_sources"] = new JsonArray([.. List(entry, "dataSources").Select(d => (JsonNode)d)]), ["default_data_source"] = Field(entry, "defaultDataSource"),
                ["version"] = VersionOf(entry),
            };
            (node["auth_type"]!.GetValue<string>() == "windows" ? windows : users).Add(node);
        }
        var jwt = o.Auth.Jwt;
        return new JsonObject
        {
            ["users"] = users,
            ["windows_accounts"] = windows,
            ["pass_through"] = o.Auth.AllowPassThrough,
            ["jwt"] = new JsonObject
            {
                ["enabled"] = jwt.Enabled, ["authority"] = jwt.Authority, ["issuer"] = jwt.Issuer, ["audience"] = jwt.Audience, ["name_claim"] = jwt.NameClaim,
                ["clock_skew_seconds"] = jwt.ClockSkewSeconds, ["has_signing_key"] = !string.IsNullOrWhiteSpace(jwt.SigningKey),
                ["mappings"] = new JsonArray([.. jwt.Mappings.Select(m => (JsonNode)new JsonObject
                {
                    ["claim"] = m.Claim, ["value"] = m.Value, ["data_sources"] = new JsonArray([.. m.DataSources.Select(d => (JsonNode)d)]), ["default_data_source"] = m.DefaultDataSource,
                })]),
            },
            ["sessions"] = new JsonObject
            {
                ["idle_minutes"] = o.Auth.SessionIdleMinutes, ["max_hours"] = o.Auth.SessionMaxHours, ["max_failed_logins"] = o.Auth.MaxFailedLogins, ["lockout_minutes"] = o.Auth.LockoutMinutes,
            },
            ["auth_version"] = File.Exists(AuthFile) ? Sha(File.ReadAllText(AuthFile)) : null,
        };
    }

    private IReadOnlyList<YamlMapping> UserEntries() => new YamlListFile(UsersFile, "users", "name").Entries();

    private static string? Field(YamlMapping entry, string key) => entry.Get(key) is YamlScalar { Value.Length: > 0 } s ? s.Value : null;

    private static IEnumerable<string> List(YamlMapping entry, string key) => entry.Get(key) is YamlSequence list ? list.Items.OfType<YamlScalar>().Select(i => i.Value) : [];

    /// <summary>The version of one user's entry: a hash of what it says, so a caller names it in its next write and a change by anyone else is seen.</summary>
    private static string VersionOf(YamlMapping entry) =>
        Sha(string.Join('\n', Field(entry, "name"), (Field(entry, "authType") ?? "password").ToLowerInvariant(), Field(entry, "passwordHash") ?? "", string.Join(',', List(entry, "dataSources")), Field(entry, "defaultDataSource") ?? ""));

    // ---- users ----

    private sealed record UserRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("auth_type")] string? AuthType,
        string? Password,
        [property: System.Text.Json.Serialization.JsonPropertyName("data_sources")] List<string>? DataSources,
        [property: System.Text.Json.Serialization.JsonPropertyName("default_data_source")] string? DefaultDataSource,
        [property: System.Text.Json.Serialization.JsonPropertyName("expected_version")] string? ExpectedVersion);

    private async Task<ManagementResult> WriteUserAsync(string name, string? body, ManagementCaller caller, CancellationToken cancellationToken)
    {
        if (!NamePattern().IsMatch(name)) return ManagementResult.Problem(400, "A user name is letters, digits and . _ - @ \\ (up to 128).");
        UserRequest? request;
        try { request = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<UserRequest>(body, Json); }
        catch (JsonException) { request = null; }
        if (request is null) return ManagementResult.Problem(400, "The body is a JSON object: {\"auth_type\": \"password\" or \"windows\", \"password\": \"...\", \"data_sources\": [\"erp\"], \"default_data_source\": \"erp\", \"expected_version\": ...}.");

        var authType = (request.AuthType ?? "password").ToLowerInvariant();
        if (authType is not ("password" or "windows")) return ManagementResult.Problem(400, "`auth_type` is `password` or `windows`.");
        if (authType == "windows" && !string.IsNullOrEmpty(request.Password)) return ManagementResult.Problem(400, "A Windows account is checked by the operating system and has no password here.");

        var existing = UserEntries().FirstOrDefault(e => string.Equals(Field(e, "name"), name, StringComparison.OrdinalIgnoreCase));
        string? hash = null;
        if (authType == "password")
        {
            if (!string.IsNullOrEmpty(request.Password)) hash = PasswordHasher.Hash(request.Password);
            else if (existing is not null && Field(existing, "passwordHash") is { } kept && (Field(existing, "authType") ?? "password").Equals("password", StringComparison.OrdinalIgnoreCase)) hash = kept;
            else return ManagementResult.Problem(400, "A password user needs a `password`.");
        }

        var sources = (request.DataSources ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var flow = Render(name, authType, hash, sources, string.IsNullOrWhiteSpace(request.DefaultDataSource) ? null : request.DefaultDataSource.Trim());

        var gateKey = "users";
        var result = await _guard.RunAsync(gateKey, request.ExpectedVersion, () => Task.FromResult(CurrentUserVersion(name)), () =>
        {
            var text = File.Exists(UsersFile) ? File.ReadAllText(UsersFile) : "";
            var changed = YamlListFile.UpsertText(text, "users", "name", existing is null ? name : Field(existing, "name")!, flow, UsersFile);
            var candidate = validation.ValidateFiles(new Dictionary<string, string> { ["proxy/users.yaml"] = changed });
            if (candidate.Body["valid"]?.GetValue<bool>() != true) return Task.FromResult<(string?, JsonNode?)>((null, candidate.Body));
            Write(UsersFile, changed);
            return Task.FromResult<(string?, JsonNode?)>((changed, candidate.Body));
        }, cancellationToken);

        if (!result.Applied)
        {
            Record(caller, "user write", name, "stale");
            return new ManagementResult(409, new JsonObject { ["error"] = "The user changed since it was read; read it again and make the change on that.", ["current_version"] = result.CurrentVersion });
        }
        var (written, validated) = ((string? Text, JsonNode? Body))result.Value;
        if (written is null)
            return new ManagementResult(400, new JsonObject { ["error"] = "The user is not valid, so nothing was written.", ["valid"] = false, ["diagnostics"] = validated!["diagnostics"]!.DeepClone() });

        Committed(caller, $"{(existing is null ? "Add" : "Change")} {(authType == "windows" ? "Windows account" : "user")} {name}", [UsersFile]);
        Record(caller, "user write", name, existing is null ? "created" : "changed");
        reload();
        var entry = UserEntries().First(e => string.Equals(Field(e, "name"), name, StringComparison.OrdinalIgnoreCase));
        return ManagementResult.Ok(new JsonObject { ["name"] = name, ["auth_type"] = authType, ["has_password"] = hash is not null, ["version"] = VersionOf(entry), ["created"] = existing is null });
    }

    private async Task<ManagementResult> DeleteUserAsync(string name, string? expectedVersion, ManagementCaller caller, CancellationToken cancellationToken)
    {
        if (UserEntries().All(e => !string.Equals(Field(e, "name"), name, StringComparison.OrdinalIgnoreCase))) return ManagementResult.Problem(404, $"There is no user '{name}'.");
        var result = await _guard.RunAsync("users", expectedVersion, () => Task.FromResult(CurrentUserVersion(name)), () =>
        {
            var text = File.ReadAllText(UsersFile);
            var stored = Field(UserEntries().First(e => string.Equals(Field(e, "name"), name, StringComparison.OrdinalIgnoreCase)), "name")!;
            Write(UsersFile, YamlListFile.RemoveText(text, "users", "name", stored, UsersFile));
            return Task.FromResult(true);
        }, cancellationToken);
        if (!result.Applied)
        {
            Record(caller, "user delete", name, "stale");
            return new ManagementResult(409, new JsonObject { ["error"] = "The user changed since it was read; read it again.", ["current_version"] = result.CurrentVersion });
        }
        Committed(caller, $"Remove user {name}", [UsersFile]);
        Record(caller, "user delete", name, "removed");
        reload();
        return ManagementResult.Ok(new JsonObject { ["name"] = name, ["removed"] = true });
    }

    private string? CurrentUserVersion(string name) =>
        UserEntries().FirstOrDefault(e => string.Equals(Field(e, "name"), name, StringComparison.OrdinalIgnoreCase)) is { } e ? VersionOf(e) : null;

    private static string Render(string name, string authType, string? hash, IReadOnlyList<string> sources, string? defaultSource) =>
        "{" + string.Join(", ", new[]
        {
            $"name: {YamlText.FlowScalar(name)}",
            $"authType: {authType}",
            hash is null ? null : $"passwordHash: {YamlText.FlowScalar(hash)}",
            $"dataSources: {YamlText.FlowList(sources)}",
            defaultSource is null ? null : $"defaultDataSource: {YamlText.FlowScalar(defaultSource)}",
        }.Where(p => p is not null)) + "}";

    // ---- pass-through ----

    private sealed record AuthRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("allow_pass_through")] bool? AllowPassThrough,
        [property: System.Text.Json.Serialization.JsonPropertyName("expected_version")] string? ExpectedVersion);

    private async Task<ManagementResult> WriteAuthAsync(string? body, ManagementCaller caller, CancellationToken cancellationToken)
    {
        AuthRequest? request;
        try { request = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<AuthRequest>(body, Json); }
        catch (JsonException) { request = null; }
        if (request?.AllowPassThrough is not { } allow) return ManagementResult.Problem(400, "The body is a JSON object: {\"allow_pass_through\": true or false, \"expected_version\": ...}.");

        var result = await _guard.RunAsync("auth", request.ExpectedVersion, () => Task.FromResult(File.Exists(AuthFile) ? Sha(File.ReadAllText(AuthFile)) : null), () =>
        {
            var text = File.Exists(AuthFile) ? File.ReadAllText(AuthFile) : "";
            var edit = text.Length == 0 ? new EditResult("allowPassThrough: " + (allow ? "true" : "false") + "\n", null) : YamlEditor.Set(text, "allowPassThrough", YamlValue.Raw(allow ? "true" : "false"), AuthFile);
            if (edit.Text is null) return Task.FromResult<(string?, JsonNode?)>((null, new JsonObject { ["error"] = edit.Problem?.Message }));
            var candidate = validation.ValidateFiles(new Dictionary<string, string> { ["proxy/auth.yaml"] = edit.Text });
            if (candidate.Body["valid"]?.GetValue<bool>() != true) return Task.FromResult<(string?, JsonNode?)>((null, candidate.Body));
            Write(AuthFile, edit.Text);
            return Task.FromResult<(string?, JsonNode?)>((edit.Text, candidate.Body));
        }, cancellationToken);

        if (!result.Applied)
        {
            Record(caller, "pass-through write", "auth", "stale");
            return new ManagementResult(409, new JsonObject { ["error"] = "proxy/auth.yaml changed since it was read; read it again.", ["current_version"] = result.CurrentVersion });
        }
        var (written, validated) = ((string? Text, JsonNode? Body))result.Value;
        if (written is null) return new ManagementResult(400, new JsonObject { ["error"] = "The change is not valid, so nothing was written.", ["detail"] = validated?.DeepClone() });
        Committed(caller, $"Set pass-through {(allow ? "on" : "off")}", [AuthFile]);
        Record(caller, "pass-through write", "auth", allow ? "on" : "off");
        reload();
        return ManagementResult.Ok(new JsonObject { ["allow_pass_through"] = allow, ["auth_version"] = Sha(written) });
    }

    // ---- shared ----

    private void Write(string file, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, file, overwrite: true);
    }

    private void Committed(ManagementCaller caller, string message, IReadOnlyCollection<string> files)
    {
        try { git?.CommitChanges(files, message, caller.Author); }
        catch (Exception ex) when (ex is LibGit2Sharp.LibGit2SharpException or IOException) { /* written and applied; the history missed this one */ }
    }

    private void Record(ManagementCaller caller, string command, string target, string outcome) =>
        audit.Append(caller.Audit(command, target, outcome, _clock.GetUtcNow()));

    [GeneratedRegex(@"^[A-Za-z0-9._@\\-]{1,128}$")]
    private static partial Regex NamePattern();
}
