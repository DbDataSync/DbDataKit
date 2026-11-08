using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Config.Git;
using DbDataKit.Config.Yaml;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Auth;
using DbDataGateway.Server.Configuration;

namespace DbDataGateway.Server.Management;

/// <summary>
/// How people reach the gateway, through the management API (plan 13, P05): the JWT claim mappings and pass-through. Gateway's own users and Windows accounts are retired (plan 13a P13): the people are the identity home's, and what they may use is named by the groups in
/// the data source files. A write to <c>gateway/auth.yaml</c> is held to the same rules as a data source's (validated as the configuration would be, refused with the diagnostics if invalid, checked against the version the caller read, committed as the person, audited, applied at once).
/// <para>No answer carries the JWT signing key, only whether there is one. The JWT mappings and the pass-through switch are shown as they are in effect, and written through <c>PUT /api/v1/auth</c>.</para>
/// </summary>
public sealed partial class GatewayIdentities(
    string configDirectory, GatewayManagement validation, Action reload, Func<GatewayOptions> options, IAuditLog audit, GitCommitService? git, TimeProvider? clock = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly WriteGuard _guard = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private string AuthFile => Path.Combine(configDirectory, "gateway", "auth.yaml");

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public async Task<ManagementResult> HandleAsync(string method, string[] rest, string? body, string? expectedVersion, ManagementCaller caller, CancellationToken cancellationToken)
    {
        // rest[0] is "identities", "users" or "auth"
        switch (rest)
        {
            case ["identities"] when method == "GET": return ManagementResult.Ok(View());
            // Gateway's own users are retired (plan 13a P13): people are the identity home's, and the home's groups named in the data source files decide the data sources
            case ["users", ..]: return ManagementResult.Problem(410, "Gateway's own users are retired: people sign in with the identity home (a home token, their user name with a personal access token, or Windows), and the data sources they may use are the ones whose files name their home groups (extensions.gateway.groups).");
            case ["auth"] when method == "PUT": return await WriteAuthAsync(body, caller, cancellationToken);
            default: return ManagementResult.Problem(404, "There is no such call.");
        }
    }

    // ---- the view ----

    private JsonObject View()
    {
        var o = options();
        var jwt = o.Auth.Jwt;
        return new JsonObject
        {
            // retired (P13): the lists stay in the answer, empty, so a console of the release before reads it
            ["users"] = new JsonArray(),
            ["windows_accounts"] = new JsonArray(),
            ["pass_through"] = o.Auth.AllowPassThrough,
            ["windows_sign_in"] = o.Auth.Windows,
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

    // ---- pass-through ----

    private sealed record JwtMappingRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("claim")] string? Claim,
        [property: System.Text.Json.Serialization.JsonPropertyName("value")] string? Value,
        [property: System.Text.Json.Serialization.JsonPropertyName("data_sources")] string[]? DataSources,
        [property: System.Text.Json.Serialization.JsonPropertyName("default_data_source")] string? DefaultDataSource);

    private sealed record AuthRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("allow_pass_through")] bool? AllowPassThrough,
        [property: System.Text.Json.Serialization.JsonPropertyName("jwt_mappings")] JwtMappingRequest[]? JwtMappings,
        [property: System.Text.Json.Serialization.JsonPropertyName("expected_version")] string? ExpectedVersion);

    /// <summary>Which JWT claim values may use which data sources, as the flow list <c>jwt.mappings</c> holds.</summary>
    private static string MappingsYaml(IEnumerable<JwtMappingRequest> mappings) => "[" + string.Join(", ", mappings.Select(m =>
        "{" + string.Join(", ", new[]
        {
            $"claim: {YamlText.FlowScalar(m.Claim!)}",
            $"value: {YamlText.FlowScalar(m.Value!)}",
            $"dataSources: {YamlText.FlowList(m.DataSources ?? [])}",
            string.IsNullOrEmpty(m.DefaultDataSource) ? null : $"defaultDataSource: {YamlText.FlowScalar(m.DefaultDataSource)}",
        }.Where(p => p is not null)) + "}")) + "]";

    private async Task<ManagementResult> WriteAuthAsync(string? body, ManagementCaller caller, CancellationToken cancellationToken)
    {
        AuthRequest? request;
        try { request = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<AuthRequest>(body, Json); }
        catch (JsonException) { request = null; }
        if (request is null || (request.AllowPassThrough is null && request.JwtMappings is null))
            return ManagementResult.Problem(400, "The body is a JSON object: {\"allow_pass_through\": true or false and/or \"jwt_mappings\": [{\"claim\", \"value\", \"data_sources\": [...], \"default_data_source\"}], \"expected_version\": ...}.");
        if (request.JwtMappings is { } given && given.FirstOrDefault(m => string.IsNullOrWhiteSpace(m.Claim) || string.IsNullOrWhiteSpace(m.Value)) is not null)
            return ManagementResult.Problem(400, "Every mapping names a claim and a value.");

        var result = await _guard.RunAsync("auth", request.ExpectedVersion, () => Task.FromResult(File.Exists(AuthFile) ? Sha(File.ReadAllText(AuthFile)) : null), () =>
        {
            var text = File.Exists(AuthFile) ? File.ReadAllText(AuthFile) : "";
            EditResult edit = new(text, null);
            if (request.AllowPassThrough is { } allowed)
            {
                var flag = YamlValue.Raw(allowed ? "true" : "false");
                edit = text.Length == 0 ? new EditResult("allowPassThrough: " + (allowed ? "true" : "false") + "\n", null) : YamlEditor.Set(text, "allowPassThrough", flag, AuthFile);
            }
            if (edit.Text is not null && request.JwtMappings is { } mappings)
            {
                // the editor sets scalars, so the list is taken out (when there is one) and the new one put in its place
                var without = edit.Text.Length == 0 ? edit : YamlEditor.Remove(edit.Text, "jwt.mappings", AuthFile);
                if (without.Text is null && without.Problem?.Message.Contains("was not found", StringComparison.Ordinal) == true) without = edit;
                edit = without.Text is null ? without
                    : without.Text.Length == 0 ? new EditResult("jwt:\n  mappings: " + MappingsYaml(mappings) + "\n", null)
                    : YamlEditor.Set(without.Text, "jwt.mappings", YamlValue.Raw(MappingsYaml(mappings)), AuthFile);
            }
            if (edit.Text is null) return Task.FromResult<(string?, JsonNode?)>((null, new JsonObject { ["error"] = edit.Problem?.Message }));
            var candidate = validation.ValidateFiles(new Dictionary<string, string> { ["gateway/auth.yaml"] = edit.Text });
            if (candidate.Body["valid"]?.GetValue<bool>() != true) return Task.FromResult<(string?, JsonNode?)>((null, candidate.Body));
            Write(AuthFile, edit.Text);
            return Task.FromResult<(string?, JsonNode?)>((edit.Text, candidate.Body));
        }, cancellationToken);

        var what = request.JwtMappings is null ? "pass-through write" : request.AllowPassThrough is null ? "jwt mappings write" : "auth write";
        if (!result.Applied)
        {
            Record(caller, what, "auth", "stale");
            return new ManagementResult(409, new JsonObject { ["error"] = "gateway/auth.yaml changed since it was read; read it again.", ["current_version"] = result.CurrentVersion });
        }
        var (written, validated) = ((string? Text, JsonNode? Body))result.Value;
        if (written is null) return new ManagementResult(400, new JsonObject { ["error"] = "The change is not valid, so nothing was written.", ["detail"] = validated?.DeepClone() });
        var message = request.JwtMappings is { } changed
            ? $"Set {changed.Length} JWT mapping(s)" + (request.AllowPassThrough is { } both ? $" and pass-through {(both ? "on" : "off")}" : "")
            : $"Set pass-through {(request.AllowPassThrough!.Value ? "on" : "off")}";
        Committed(caller, message, [AuthFile]);
        Record(caller, what, "auth", request.JwtMappings is { } n ? $"{n.Length} mapping(s)" : request.AllowPassThrough!.Value ? "on" : "off");
        reload();
        var answer = new JsonObject { ["auth_version"] = Sha(written) };
        if (request.AllowPassThrough is { } pt) answer["allow_pass_through"] = pt;
        if (request.JwtMappings is { } jm) answer["jwt_mappings"] = jm.Length;
        return ManagementResult.Ok(answer);
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
