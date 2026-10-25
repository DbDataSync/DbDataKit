using System.Security.Cryptography;
using System.Text;

namespace DbDataKit.Web.Trust;

/// <summary>
/// What proves a pairing exchange to both ends without the code crossing the wire (plan 13, W12). The client and the server both know a key made from the code. The client's proof covers its id, its public key, its
/// kind and the fingerprint of the certificate it saw; the server's covers its instance id, the certificate it holds and the client's identity. Someone in the middle who terminates TLS with their own
/// certificate cannot make either proof, so the client ends up pinning the server it meant to, and the server pairs the client that holds the code.
/// </summary>
public static class PairingProof
{
    public static string ForRequest(byte[] key, string clientId, byte[] publicKey, ClientKind kind, string serverFingerprint) =>
        Mac(key, "dbdata-pair-v1-client", clientId, Base64Url.Encode(publicKey), kind.ToString().ToLowerInvariant(), serverFingerprint);

    public static string ForResponse(byte[] key, string instanceId, string serverFingerprint, string clientId, byte[] publicKey) =>
        Mac(key, "dbdata-pair-v1-server", instanceId, serverFingerprint, clientId, Base64Url.Encode(publicKey));

    public static bool Matches(string? given, string expected) =>
        given is not null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(given), Encoding.ASCII.GetBytes(expected));

    /// <summary>The SHA-256 of a certificate's DER bytes, as 64 lower-case hex digits: what a client pins.</summary>
    public static string CertificateFingerprint(System.Security.Cryptography.X509Certificates.X509Certificate certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())).ToLowerInvariant();

    private static string Mac(byte[] key, params string[] parts) =>
        Base64Url.Encode(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(string.Join('\n', parts))));
}
