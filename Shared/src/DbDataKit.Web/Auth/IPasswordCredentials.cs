using DbDataKit.State;

namespace DbDataKit.Web.Auth;

/// <summary>
/// A user name and password a host accepts in place of a session (HTTP Basic), for clients that can send nothing else: in the identity home, a user name and a personal access token (plan 13a U20). A host that registers one
/// has <see cref="SessionAuthenticationHandler"/> try it for a request that carries <c>Authorization: Basic</c>; the person is the same user, with the same id, as by any other way in.
/// </summary>
public interface IPasswordCredentials
{
    /// <summary>The user these credentials sign in as, or null when they do not.</summary>
    UserRecord? Verify(string userName, string password);
}

public static class PasswordCredentials
{
    /// <summary>The claim a principal signed in with a user name and password carries (value <see cref="Method"/>): what may not be done that way (making more tokens) checks it.</summary>
    public const string MethodClaim = "dbdata_auth_method";

    public const string Method = "password";
}
