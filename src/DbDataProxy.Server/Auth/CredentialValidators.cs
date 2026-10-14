using DbDataProxy.Server.Configuration;
using Microsoft.Extensions.Options;

namespace DbDataProxy.Server.Auth;

/// <summary>
/// Outcome of one validator. <see cref="Principal"/> is set on success. A failure is <see cref="Definitive"/> when the
/// validator owns the identity (so later validators must not second-guess it), otherwise the chain moves on.
/// </summary>
public readonly record struct CredentialResult(ProxyPrincipal? Principal, bool Definitive)
{
    public static CredentialResult Accepted(ProxyPrincipal principal) => new(principal, true);
    public static CredentialResult Rejected => new(null, true);
    public static CredentialResult NotMine => new(null, false);
}

/// <summary>Validates a username/password pair (Flight "Basic" auth).</summary>
public interface IBasicCredentialValidator
{
    ValueTask<CredentialResult> ValidateAsync(string user, string password, CancellationToken cancellationToken);
}

/// <summary>Checks credentials against the hashed-password users in configuration.</summary>
public sealed class LocalUserValidator(IOptionsMonitor<ProxyOptions> options) : IBasicCredentialValidator
{
    public ValueTask<CredentialResult> ValidateAsync(string user, string password, CancellationToken cancellationToken)
    {
        var entry = options.CurrentValue.Users.FirstOrDefault(u => string.Equals(u.Name, user, StringComparison.OrdinalIgnoreCase));
        // Windows-account users are handled by WindowsAccountValidator.
        if (entry is null || entry.AuthType != UserAuthType.Password) return ValueTask.FromResult(CredentialResult.NotMine);

        // A configured user is always authoritative: a wrong password must not fall through to pass-through.
        if (entry.PasswordHash is null || !PasswordHasher.Verify(password, entry.PasswordHash))
            return ValueTask.FromResult(CredentialResult.Rejected);

        return ValueTask.FromResult(CredentialResult.Accepted(
            new ProxyPrincipal(entry.Name, "password", entry.DataSources, entry.DefaultDataSource)));
    }
}

/// <summary>
/// Accepts any credentials and carries them as database credentials. The database is the authority: pass-through
/// data sources connect as the caller, so a wrong password fails at query time.
/// </summary>
public sealed class PassThroughValidator(IOptionsMonitor<ProxyOptions> options) : IBasicCredentialValidator
{
    public ValueTask<CredentialResult> ValidateAsync(string user, string password, CancellationToken cancellationToken)
    {
        var o = options.CurrentValue;
        if (!o.Auth.AllowPassThrough || string.IsNullOrEmpty(user)) return ValueTask.FromResult(CredentialResult.NotMine);

        var passThroughSources = o.DataSources.Where(d => d.Value.PassThrough).Select(d => d.Key).ToList();
        if (passThroughSources.Count == 0) return ValueTask.FromResult(CredentialResult.NotMine);

        string? defaultSource = passThroughSources.Count == 1 ? passThroughSources[0] : null;
        return ValueTask.FromResult(CredentialResult.Accepted(
            new ProxyPrincipal(user, "passthrough", passThroughSources, defaultSource, new DbCredentials(user, password))));
    }
}

/// <summary>Tries each validator in order; the first definitive answer wins.</summary>
public sealed class ChainedCredentialValidator(IEnumerable<IBasicCredentialValidator> validators, LoginThrottle? throttle = null)
{
    private readonly IBasicCredentialValidator[] _validators = validators.ToArray();

    /// <param name="client">Identifies the caller (its network address) for brute-force throttling.</param>
    public async ValueTask<ProxyPrincipal?> ValidateAsync(string user, string password, CancellationToken cancellationToken, string client = "")
    {
        if (throttle?.IsLockedOut(client, user) == true) return null;

        foreach (var v in _validators)
        {
            var result = await v.ValidateAsync(user, password, cancellationToken).ConfigureAwait(false);
            if (!result.Definitive) continue;

            if (result.Principal is null) throttle?.RecordFailure(client, user);
            else throttle?.RecordSuccess(client, user);
            return result.Principal;
        }
        // Burn comparable time so a rejected unknown user is not distinguishable from a wrong password.
        PasswordHasher.BurnVerifyTime(password);
        throttle?.RecordFailure(client, user);
        return null;
    }
}
