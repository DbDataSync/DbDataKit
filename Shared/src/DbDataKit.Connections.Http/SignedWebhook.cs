using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DbDataKit.Connections.Http;

/// <summary>
/// The signature of <c>sign_in.method: signed_webhook</c> (plan 22a GP09 and the GP18 fold): the header value <c>t=&lt;unix seconds&gt;,v1=&lt;hex&gt;</c>, where <c>v1</c> is HMAC-SHA256 with the shared secret of the text <c>&lt;t&gt;.&lt;body&gt;</c>, the body exactly as sent.
/// The receiver checks it in constant time and that <c>t</c> is within a window of its own clock (the replay window is the receiver's, and the delivery library's <c>WebhookSignature.Verify</c> is one such receiver).
/// </summary>
public static class SignedWebhook
{
    /// <summary>The header the signature goes in when <c>sign_in.name</c> does not say another.</summary>
    public const string DefaultHeader = "X-DbData-Signature";

    public static string Sign(string secret, string body, DateTimeOffset at)
    {
        var t = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"t={t},v1={Convert.ToHexStringLower(Mac(secret, t, body))}";
    }

    /// <summary>The HMAC of <c>&lt;t&gt;.&lt;body&gt;</c>, for a receiver that compares it.</summary>
    public static byte[] Mac(string secret, string t, string body) => HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(t + "." + body));
}
