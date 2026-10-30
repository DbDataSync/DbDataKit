namespace DbDataKit.State;

/// <summary>
/// One account per person across every way of signing in (plan 13a U16). A local user (a passkey), a Windows account and a person who signs in through the identity home are matched on the address (<see cref="AccountKey"/>): the
/// first sign-in by a method that no account has seen finds the account that holds the person's address and gets a credential on it, so the person has the same roles and the same user id (the audit's) whichever way they come in.
/// <para>
/// **What is trusted to say whose an address is.** (1) A local user's address is the one an administrator entered when making the invitation (or set afterwards); what the person types when redeeming is never used. (2) A Windows
/// account's is its user principal name as the domain issued it (the Kerberos ticket's claim), never the SAM name. (3) A person who signs in through the home has the address in the home's id token when the home says
/// <c>email_verified</c>; without that claim nothing is matched. Anything else (an address nobody checked, an account made before addresses were keyed) is stored for display and **never links**: it neither finds an account nor
/// is found by one. An address is held by one account; a sign-in that would take one another account holds is refused with a message, not merged and not renamed.
/// </para>
/// </summary>
public sealed class AccountLinker(UserStore users)
{
    /// <summary>
    /// The user for a Windows sign-in. A SID already known is that user (its UPN, when the domain gives one, becomes the account's address, or is refused if another account holds it). A new SID needs the UPN: it joins the account that
    /// holds that address (an invited local user, someone who signed in through the home), or makes a new one. A new SID with no UPN, only a SAM name (<c>DOMAIN\user</c>), is refused: a SAM name is not an address, and guessing one would
    /// make the same person two accounts, or two people one.
    /// </summary>
    public UserRecord SignInWindows(string sid, string name, string? upn, UserRole role)
    {
        // a claim that is not an address (a SAM name where a UPN should be) is no key; the SID alone identifies the account
        var key = AccountKey.Normalize(upn);
        var user = users.FindByCredential(CredentialMethods.Windows, sid);
        if (user is null)
        {
            if (key is null)
                throw new AccountConflictException(
                    $"'{name}' is a Windows account the domain gave no user principal name (user@domain), so it cannot be matched to a person's account here. " +
                    "Give the account a UPN in the directory, or sign in another way.");
            user = users.FindByEmail(key);
            if (user is null)
            {
                user = users.CreateUser(name, upn!.Trim(), role, emailVerified: true);
                users.AddCredential(user.Id, CredentialMethods.Windows, sid, label: name);
                return user;
            }
            users.AddCredential(user.Id, CredentialMethods.Windows, sid, label: name);
        }
        else if (key is not null && user.EmailKey != key)
            user = users.SetVerifiedEmail(user.Id, upn!);

        // the group is the authority for what a Windows sign-in is allowed, as it always was
        if (user.Role != role)
        {
            users.SetRole(user.Id, role);
            user = user with { Role = role };
        }
        return user;
    }

    /// <summary>
    /// The user for a sign-in through the home, under the home's id. A person the home names for the first time whose verified address a local or Windows account holds is that account, which takes the home's id (everything it had
    /// moves with it, so the same person has one user id in every console that signs people in through the home). An address held by a mirror of another home person (an account with no credential of its own), or any address the
    /// home did not verify, links nothing; a mirror's address that another account holds is refused, not merged.
    /// </summary>
    public UserRecord SignInHome(string homeUserId, string displayName, string? email, bool emailVerified, UserRole role)
    {
        if (users.Get(homeUserId) is null && emailVerified && AccountKey.Normalize(email) is not null && users.FindByEmail(email) is { } holder)
        {
            // an account with no credential is a mirror of a person the home already has under another id: not ours to take over
            if (users.CredentialsOf(holder.Id).Count == 0)
                throw new AccountConflictException(
                    $"The address '{email!.Trim()}' is already used by another account ('{holder.DisplayName}') that signs in through the identity home under a different id. An administrator has to remove one of the two.", holder.Id);
            users.Rekey(holder.Id, homeUserId);
        }

        return users.UpsertFederated(homeUserId, displayName, email, role, emailVerified);
    }
}
