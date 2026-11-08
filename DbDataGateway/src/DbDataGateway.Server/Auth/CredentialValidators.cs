using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Serving;
using Microsoft.Extensions.Options;

namespace DbDataGateway.Server.Auth;

/// <summary>
/// Outcome of one validator. <see cref="Principal"/> is set on success. A failure is <see cref="Definitive"/> when the
/// validator owns the identity (so later validators must not second-guess it), otherwise the chain moves on.
/// </summary>
public readonly record struct CredentialResult(GatewayPrincipal? Principal, bool Definitive)
{
    public static CredentialResult Accepted(GatewayPrincipal principal) => new(principal, true);
    public static CredentialResult Rejected => new(null, true);
    public static CredentialResult NotMine => new(null, false);
}

/// <summary>Validates a username/password pair (Flight "Basic" auth).</summary>
public interface IBasicCredentialValidator
{
    ValueTask<CredentialResult> ValidateAsync(string user, string password, CancellationToken cancellationToken);
}

/// <summary>
/// The identity home's people with a user name and a personal access token (plan 13a P13; plan 16 "Sign-in decisions" item 2): the user name is the person's address, a service account's name or the user id, the password the token (<c>ddk_...</c>).
/// A password that has the shape of a token and is not the right one for that user is refused for good, never handed on to pass-through; anything else is not this validator's.
/// </summary>
public sealed class HomeCredentialValidator(HomeCallers callers, IOptionsMonitor<GatewayOptions> options) : IBasicCredentialValidator
{
    public async ValueTask<CredentialResult> ValidateAsync(string user, string password, CancellationToken cancellationToken)
    {
        if (!HomePrincipals.LooksLikeToken(password)) return CredentialResult.NotMine;
        var home = await callers.GetAsync(cancellationToken).ConfigureAwait(false);
        return home.IdentifyPassword(user, password) is { } found
            ? CredentialResult.Accepted(HomePrincipals.Create(home, found, "home-user", options.CurrentValue))
            : CredentialResult.Rejected;
    }
}

/// <summary>
/// Accepts any credentials and carries them as database credentials. The database is the authority: pass-through
/// data sources connect as the caller, so a wrong password fails at query time.
/// </summary>
public sealed class PassThroughValidator(IOptionsMonitor<GatewayOptions> options) : IBasicCredentialValidator
{
    public ValueTask<CredentialResult> ValidateAsync(string user, string password, CancellationToken cancellationToken)
    {
        var o = options.CurrentValue;
        if (!o.Auth.AllowPassThrough || string.IsNullOrEmpty(user)) return ValueTask.FromResult(CredentialResult.NotMine);

        var passThroughSources = o.DataSources.Where(d => d.Value.PassThrough).Select(d => d.Key).ToList();
        if (passThroughSources.Count == 0) return ValueTask.FromResult(CredentialResult.NotMine);

        string? defaultSource = passThroughSources.Count == 1 ? passThroughSources[0] : null;
        return ValueTask.FromResult(CredentialResult.Accepted(
            new GatewayPrincipal(user, "passthrough", passThroughSources, defaultSource, new DbCredentials(user, password))));
    }
}

/// <summary>Tries each validator in order; the first definitive answer wins.</summary>
public sealed class ChainedCredentialValidator(IEnumerable<IBasicCredentialValidator> validators, LoginThrottle? throttle = null)
{
    private readonly IBasicCredentialValidator[] _validators = validators.ToArray();

    /// <param name="client">Identifies the caller (its network address) for brute-force throttling.</param>
    public async ValueTask<GatewayPrincipal?> ValidateAsync(string user, string password, CancellationToken cancellationToken, string client = "")
    {
        if (throttle?.IsLockedOut(client, user) == true) return null;

        foreach (var v in _validators)
        {
            var result = await v.ValidateAsync(user, password, cancellationToken).ConfigureAwait(false);
            if (!result.Definitive) continue;

            if (result.Principal is null) throttle?.RecordFailure(client, user);
            // A pass-through principal was not verified by anyone yet (the database checks it at query time), so accepting it proves nothing:
            // it must not clear the failures a rejected database login has recorded for this user and client (S-2).
            else if (result.Principal.Credentials is null) throttle?.RecordSuccess(client, user);
            return result.Principal;
        }
        // Burn comparable time so a rejected unknown user is not distinguishable from a wrong password.
        PasswordHasher.BurnVerifyTime(password);
        throttle?.RecordFailure(client, user);
        return null;
    }
}
