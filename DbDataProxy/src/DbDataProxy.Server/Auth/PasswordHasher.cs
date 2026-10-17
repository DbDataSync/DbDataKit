namespace DbDataProxy.Server.Auth;

/// <summary>
/// Thin adapter over the shared PBKDF2 hasher (<c>DbDataKit.Secrets.PasswordHasher</c>). The shared implementation writes and
/// reads the same <c>PBKDF2-SHA256$iterations$salt$hash</c> format; <c>PasswordHasherCompatibilityTests</c> pins that with
/// hashes produced by the previous Proxy implementation.
/// </summary>
public static class PasswordHasher
{
    public const int DefaultIterations = DbDataKit.Secrets.PasswordHasher.DefaultIterations;

    public static string Hash(string password, int iterations = DefaultIterations) =>
        DbDataKit.Secrets.PasswordHasher.Hash(password, iterations);

    public static bool Verify(string password, string stored) =>
        DbDataKit.Secrets.PasswordHasher.Verify(password, stored);

    /// <summary>Burns roughly the same CPU as a real verification so unknown users are not distinguishable by timing.</summary>
    public static void BurnVerifyTime(string password) =>
        DbDataKit.Secrets.PasswordHasher.BurnVerifyTime(password);
}
