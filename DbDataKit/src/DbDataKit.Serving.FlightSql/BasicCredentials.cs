using System.Text;
using Google.Protobuf;

namespace DbDataKit.Serving.FlightSql;

/// <summary>The two places a Flight client puts a user name and a password, and the header that carries a token or credentials on every other call.</summary>
public static class BasicCredentials
{
    public static bool TryParseBasic(string? authorization, out string user, out string password)
    {
        user = password = "";
        if (authorization is null || !authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            string encoded = authorization["Basic ".Length..].Trim();
            // Some clients (the Go-based ADBC driver) send base64 without the trailing padding.
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            int colon = decoded.IndexOf(':');
            if (colon <= 0) return false;
            user = decoded[..colon];
            password = decoded[(colon + 1)..];
            return true;
        }
        catch (FormatException) { return false; }
    }

    /// <summary>Parses the Flight <c>BasicAuth</c> protobuf message (username = field 2, password = field 3), which the Go clients, including the ADBC Flight SQL driver, send in the handshake payload.</summary>
    public static bool TryParseBasicAuthPayload(byte[] payload, out string user, out string password)
    {
        user = password = "";
        if (payload.Length == 0) return false;
        try
        {
            var input = new CodedInputStream(payload);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (WireFormat.GetTagFieldNumber(tag))
                {
                    case 2: user = input.ReadString(); break;
                    case 3: password = input.ReadString(); break;
                    default: input.SkipLastField(); break;
                }
            }
        }
        catch (InvalidProtocolBufferException)
        {
            return false;
        }
        return user.Length > 0;
    }
}
