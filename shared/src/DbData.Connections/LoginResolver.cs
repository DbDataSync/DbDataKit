using DbData.Secrets;

namespace DbData.Connections;

/// <summary>
/// Turns a definition and a role into a <see cref="ResolvedLogin"/> using the Plan 02 resolver.
/// Order for <see cref="LoginRole.Read"/>: <c>read_login</c>, then the env var <c>DBDATA_&lt;APP&gt;_&lt;CONN&gt;_READ</c>, then the
/// legacy Build reference, then <c>auth</c>. Write is the same with <c>write_login</c>/<c>_WRITE</c>. A read request never looks at
/// anything write: no fallback from read to write. A write request on a read-only connection is refused.
/// The env var and legacy forms hold a whole connection string (the Build convention).
/// </summary>
public sealed class LoginResolver(ISecretResolver secrets, string app, bool legacyBuildRefs = true)
{
    /// <summary>The env variable name for a connection and role: <c>DBDATA_&lt;APP&gt;_&lt;CONN&gt;[_READ|_WRITE]</c>, upper case, other characters become underscores.</summary>
    public static string EnvName(string app, string connection, LoginRole role) =>
        $"DBDATA_{Sanitize(app)}_{Sanitize(connection)}" + role switch { LoginRole.Read => "_READ", LoginRole.Write => "_WRITE", _ => "" };

    public static string Sanitize(string text)
    {
        var chars = text.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_').ToArray();
        return new string(chars);
    }

    /// <summary>The Build reference <c>secret:dbdata:build:connection:&lt;conn&gt;:&lt;read|write&gt;</c> that the Secrets legacy provider maps to <c>DBDATABUILD_*</c>.</summary>
    public static SecretRef BuildLegacyRef(string connection, LoginRole role) =>
        new(SecretRefKind.Secret, $"dbdata:build:connection:{connection}:{(role == LoginRole.Read ? "read" : "write")}");

    public async ValueTask<ResolvedLogin?> ResolveAsync(ConnectionDefinition definition, LoginRole role, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (role == LoginRole.Write && definition.Access == AccessMode.Readonly)
            throw new ConnectionPolicyException("a write login was requested for a read-only connection");

        var explicitSpec = role switch { LoginRole.Read => definition.ReadLogin, LoginRole.Write => definition.WriteLogin, _ => null };
        if (explicitSpec is not null)
            return await FromSpecAsync(explicitSpec, role, role == LoginRole.Read ? "read_login" : "write_login", ct).ConfigureAwait(false);

        var envName = EnvName(app, definition.Name, role);
        if (await TryAsync(new SecretRef(SecretRefKind.Env, envName), ct).ConfigureAwait(false) is { } fromEnv)
            return new ResolvedLogin(role, AuthMode.Sql, "env:" + envName) { ConnectionString = fromEnv };

        if (legacyBuildRefs && role != LoginRole.Default && string.Equals(app, "build", StringComparison.OrdinalIgnoreCase))
        {
            var legacy = BuildLegacyRef(definition.Name, role);
            if (await TryAsync(legacy, ct).ConfigureAwait(false) is { } fromLegacy)
                return new ResolvedLogin(role, AuthMode.Sql, legacy.ToAuditString()) { ConnectionString = fromLegacy };
        }

        if (definition.Auth is { } auth) return await FromSpecAsync(auth, role, "auth", ct).ConfigureAwait(false);

        if (definition.ConnectionStringSecret is { } whole)
        {
            var secret = await secrets.ResolveAsync(SecretRef.Parse(whole), ct).ConfigureAwait(false);
            return new ResolvedLogin(role, AuthMode.Sql, SecretRef.Parse(whole).ToAuditString()) { ConnectionString = secret };
        }
        return null;
    }

    private async ValueTask<SecretValue?> TryAsync(SecretRef reference, CancellationToken ct)
    {
        try { return await secrets.ResolveAsync(reference, ct).ConfigureAwait(false); }
        catch (SecretNotFoundException) { return null; }
    }

    private async ValueTask<ResolvedLogin> FromSpecAsync(LoginSpec spec, LoginRole role, string source, CancellationToken ct)
    {
        SecretValue? password = null, whole = null, token = null;
        try
        {
            if (spec.PasswordSecret is not null) password = await secrets.ResolveAsync(SecretRef.Parse(spec.PasswordSecret), ct).ConfigureAwait(false);
            if (spec.ConnectionStringSecret is not null) whole = await secrets.ResolveAsync(SecretRef.Parse(spec.ConnectionStringSecret), ct).ConfigureAwait(false);
            if (spec.TokenSecret is not null) token = await secrets.ResolveAsync(SecretRef.Parse(spec.TokenSecret), ct).ConfigureAwait(false);
            return new ResolvedLogin(role, spec.Mode, "definition:" + source) { UserName = spec.User, Password = password, ConnectionString = whole, Token = token };
        }
        catch
        {
            password?.Dispose(); whole?.Dispose(); token?.Dispose();
            throw;
        }
    }
}
