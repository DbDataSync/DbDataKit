using System.Security.Cryptography;

namespace DbDataProxy.Server.Auth;

/// <summary>
/// PBKDF2-HMAC-SHA256 password hashing. Stored form: <c>PBKDF2-SHA256$iterations$salt$hash</c> (base64 parts).
/// The iteration count is stored with each hash so it can be raised later without invalidating existing users.
/// </summary>
public static class PasswordHasher
{
    private const string Prefix = "PBKDF2-SHA256";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    public const int DefaultIterations = 210_000;

    public static string Hash(string password, int iterations = DefaultIterations)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Prefix}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        if (!TryParse(stored, out int iterations, out byte[] salt, out byte[] expected)) return false;
        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Burns roughly the same CPU as a real verification so unknown users are not distinguishable by timing.</summary>
    public static void BurnVerifyTime(string password) =>
        Rfc2898DeriveBytes.Pbkdf2(password, new byte[SaltBytes], DefaultIterations, HashAlgorithmName.SHA256, HashBytes);

    private static bool TryParse(string stored, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0; salt = []; hash = [];
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Prefix) return false;
        if (!int.TryParse(parts[1], out iterations) || iterations < 1) return false;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            hash = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException) { return false; }
        return salt.Length > 0 && hash.Length > 0;
    }
}
