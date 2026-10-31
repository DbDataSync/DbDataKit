using DbDataKit.State;

namespace DbDataKit.Identity;

/// <summary>Who an upstream sign-in says a person is: where they came from, the stable subject there, and the groups they carry.</summary>
/// <param name="Source">One of <see cref="ExternalSources"/>.</param>
/// <param name="Subject">The upstream's stable id for the person (Entra: the id token's <c>oid</c> or <c>sub</c>; Windows: the SID). Never the name or the address.</param>
/// <param name="Name">The display name the sign-in gave, or null.</param>
/// <param name="Email">The address the sign-in gave, or null.</param>
/// <param name="Groups">The group values the sign-in carried (Entra: the <c>groups</c> claim; Windows: the token's groups).</param>
/// <param name="EmailVerified">Whether the host trusts the address to say whose it is (plan 13a U16: only then it finds or is found by an account). An Entra id token carries no <c>email_verified</c>; the host decides for its tenant.</param>
public sealed record ExternalIdentity(string Source, string Subject, string? Name, string? Email, bool EmailVerified, IReadOnlyList<string> Groups);

/// <summary>
/// A person arriving at the home from an external provider (plan 13a U18): the home's user for them (one account per person, U16's rules: the credential finds the account, else a verified address does, else a new account with no roles of
/// its own), and the groups they carry, recorded so <see cref="HomeDirectory.MapExternalGroup"/> mappings give them roles. The host starts its session for the returned user and the provider's tokens carry the roles.
/// </summary>
public sealed class ExternalSignIns(UserStore users, HomeDirectory directory)
{
    /// <summary>The user for this sign-in. Throws <see cref="AccountConflictException"/> when the person's verified address belongs to another account that already has a credential from this source.</summary>
    public UserRecord SignIn(ExternalIdentity identity)
    {
        ExternalSources.Require(identity.Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Subject);
        var method = ExternalSources.CredentialMethod(identity.Source);
        var subject = identity.Subject.Trim();
        var name = string.IsNullOrWhiteSpace(identity.Name) ? subject : identity.Name.Trim();
        var key = identity.EmailVerified ? AccountKey.Normalize(identity.Email) : null;

        var user = users.FindByCredential(method, subject);
        if (user is null)
        {
            user = key is not null ? users.FindByEmail(key) : null;
            if (user is not null && users.CredentialsOf(user.Id).Any(c => c.Method == method))
                throw new AccountConflictException($"The address '{identity.Email!.Trim()}' belongs to an account ('{user.DisplayName}') that already signs in through {identity.Source} as someone else. An administrator has to remove one of the two.", user.Id);
            user ??= users.CreateUser(name, identity.Email, UserRole.Viewer, emailVerified: key is not null);
            users.AddCredential(user.Id, method, subject, label: name);
        }
        else if (key is not null && user.EmailKey != key)
            user = users.SetVerifiedEmail(user.Id, identity.Email!);

        directory.SetExternalGroups(user.Id, identity.Source, identity.Groups);
        return user;
    }

    /// <summary>For a sign-in through an OpenID Connect provider other than the home (Entra): the identity its id token gave, read by <see cref="OpenIdClient"/> (the <c>groups</c> claim is <see cref="HomeIdentity.Groups"/>).</summary>
    public static ExternalIdentity FromOpenId(string source, HomeIdentity identity, bool trustEmail = false) =>
        new(source, identity.UserId, identity.Name, identity.Email, trustEmail && identity.EmailVerified, identity.Groups);
}
