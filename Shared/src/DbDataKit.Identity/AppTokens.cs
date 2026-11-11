using System.Text.Json;
using DbDataKit.Web.Trust;

namespace DbDataKit.Identity;

/// <summary>What an app instance's access token names as its audience: <c>app:&lt;instance&gt;</c>, the same string the home keeps the person's roles on the instance under (<c>dbdata_roles</c>).</summary>
public static class AppAudience
{
    public const string Prefix = "app:";

    public static string Of(string instance) => Prefix + instance;
}

/// <summary>
/// What the approved manifest of an app instance allows, as the home needs it to make a token (plan 13a U19; plan 16, "Sign-in decisions" item 1). The apps host supplies it (it holds the instances and their approvals);
/// the home does not read manifests itself.
/// </summary>
public interface IAppGrants
{
    /// <summary>
    /// The scope of the instance's approved manifest: <see cref="TokenScope.Entry"/> strings (<c>model/entity:read</c> or <c>:write</c>, write including read). Null when the instance does not exist or runs
    /// no approved version: the home then issues nothing for it.
    /// </summary>
    IReadOnlyList<string>? ScopeOf(string instance);
}

/// <summary>
/// The rule that makes an app's token never wider than the person: the manifest's scope narrowed by the roles the person holds on the instance. Reading needs any role on the instance; writing needs
/// <see cref="ServerRole.Author"/> or <see cref="ServerRole.Admin"/> (what the Store asks of a write and of the administrator's decision on a change request). A write entry the person may not use becomes a read entry,
/// and a person with no role at all gets no entry.
/// </summary>
public static class AppScope
{
    private const string WriteSuffix = ":write";

    public static IReadOnlyList<string> Narrow(IReadOnlyList<string> manifestScope, ServerRole held)
    {
        var closed = ServerRoles.Close(held);
        if (closed == ServerRole.None) return [];
        var mayWrite = closed.HasFlag(ServerRole.Author) || closed.HasFlag(ServerRole.Admin);
        // write includes read, so an entity the manifest lets the app write stays readable for a person who may not write
        return [.. manifestScope.Select(entry => mayWrite || !entry.EndsWith(WriteSuffix, StringComparison.Ordinal) ? entry : entry[..^WriteSuffix.Length] + ":read").Distinct(StringComparer.Ordinal)];
    }
}

public enum AppTokenFailure
{
    None,
    /// <summary>Not a token this home signed, or expired.</summary>
    Invalid,
    /// <summary>A token for another audience: another instance's, or one of the home's own.</summary>
    WrongAudience,
}

/// <summary>A token that passed: the person, the instance, the roles the home gave the person on it and the scope that is left after both the manifest and the person's roles.</summary>
public sealed record AppAccessToken(string Instance, string SubjectId, string ClientId, IReadOnlyList<string> Roles, IReadOnlyList<string> Scope, DateTimeOffset ExpiresAtUtc)
{
    /// <summary>Whether the token lets the app read (or, with <paramref name="write"/>, write) the entity; write includes read. A model or entity the scope does not name is refused.</summary>
    public bool Allows(string model, string entity, bool write) =>
        Scope.Contains(TokenScope.Entry(model, entity, write: true), StringComparer.Ordinal)
        || (!write && Scope.Contains(TokenScope.Entry(model, entity, write: false), StringComparer.Ordinal));
}

public sealed record AppTokenResult(AppAccessToken? Token, AppTokenFailure Failure)
{
    public bool Ok => Token is not null;
}

/// <summary>
/// What a module of the serving layer uses to accept an app's token (plan 13a U19): the signature, the issuer and the expiry are the home's to check (<paramref name="verify"/>, which is
/// <see cref="OpenIdProvider.Verify"/> in the process that hosts the home), then the audience must be <b>this instance</b>, so instance B's validator refuses instance A's token, and so does a module that is handed a
/// token the home made for a console or any other client. The scope in the token is already narrowed to the person; it is read as it is and never widened here.
/// </summary>
public sealed class AppTokenValidator(Func<string, IReadOnlyDictionary<string, object?>?> verify)
{
    public AppTokenValidator(OpenIdProvider provider) : this(jwt => provider.Verify(jwt, "at+jwt")) { }

    /// <summary>For a host that does not run the home: the keys are the ones the home publishes (<see cref="JwksTokenVerifier.PrepareAsync"/> reads them before a token is checked).</summary>
    public AppTokenValidator(JwksTokenVerifier verifier) : this(jwt => verifier.Verify(jwt, "at+jwt")) { }

    /// <summary>Checks a bearer token (without the word <c>Bearer</c>) for <paramref name="instance"/>.</summary>
    public AppTokenResult Validate(string? bearer, string instance)
    {
        if (string.IsNullOrEmpty(bearer) || verify(bearer) is not { } claims) return new(null, AppTokenFailure.Invalid);
        if (claims.GetValueOrDefault("aud") as string != AppAudience.Of(instance)) return new(null, AppTokenFailure.WrongAudience);
        if (claims.GetValueOrDefault("sub") as string is not { Length: > 0 } subject || claims.GetValueOrDefault("client_id") as string is not { } client) return new(null, AppTokenFailure.Invalid);
        var expires = claims.GetValueOrDefault("exp") is JsonElement { ValueKind: JsonValueKind.Number } e ? DateTimeOffset.FromUnixTimeSeconds(e.GetInt64()) : DateTimeOffset.MinValue;
        return new(new AppAccessToken(instance, subject, client, Strings(claims.GetValueOrDefault("dbdata_roles")), Strings(claims.GetValueOrDefault("dbdata_scope")), expires), AppTokenFailure.None);
    }

    private static IReadOnlyList<string> Strings(object? value) =>
        value is JsonElement { ValueKind: JsonValueKind.Array } array ? [.. array.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!)] : [];
}
