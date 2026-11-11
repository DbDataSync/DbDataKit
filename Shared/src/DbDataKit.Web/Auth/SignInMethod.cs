namespace DbDataKit.Web.Auth;

/// <summary>
/// A way to sign in that a host adds beside Windows and passkeys, registered as a singleton and listed in <c>/api/auth/status</c>'s <c>methods</c>, so the sign-in screen offers it. A console that signs
/// people in through an identity home registers <see cref="IdentityHome"/> (plan 20 O06).
/// </summary>
public sealed record SignInMethod(string Name)
{
    /// <summary>The identity home: the screen sends the person to <c>/api/identity/sign-in</c>.</summary>
    public static readonly SignInMethod IdentityHome = new("identity-home");
}
