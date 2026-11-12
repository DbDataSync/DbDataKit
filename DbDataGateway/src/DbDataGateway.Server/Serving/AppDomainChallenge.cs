using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace DbDataGateway.Server.Serving;

/// <summary>
/// The DNS challenge that proves an administrator controls a custom domain before it is bound to an instance (plan 26 AP08): a TXT record at <c>_dbdatakit-challenge.&lt;name&gt;</c> holding a value only this host and this instance
/// can name. The value is a keyed hash of the host's own instance id, the instance and the name, so it is the same every time it is asked for (the editor can show it before the record exists), differs per instance and per name, and
/// cannot be worked out by someone who has not been shown it. A record that holds it is proof of ownership; it is accepted instead of the name having to resolve to this host already (a name may be moved over after it is bound).
/// </summary>
public static class AppDomainChallenge
{
    public const string RecordPrefix = "_dbdatakit-challenge.";
    public const string ValuePrefix = "dbdatakit-domain-verification=";

    /// <summary>The name the TXT record is created at.</summary>
    public static string RecordName(string host) => RecordPrefix + host;

    /// <summary>The value the TXT record must hold.</summary>
    public static string Value(string? serverId, string instance, string host)
    {
        var key = Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(serverId) ? "dbdatagateway" : serverId);
        var hash = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"app-domain|{instance}|{host}"));
        return ValuePrefix + Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    /// <summary>Whether any of the TXT records found holds <paramref name="value"/> (a record may carry other text beside it, as an SPF record does).</summary>
    public static bool Holds(IEnumerable<string> records, string value) =>
        records.Any(r => r.Split([' ', ';', ','], StringSplitOptions.RemoveEmptyEntries).Any(part => string.Equals(part, value, StringComparison.Ordinal)) || string.Equals(r.Trim(), value, StringComparison.Ordinal));
}

/// <summary>
/// A small DNS client for TXT records (.NET has none): one UDP question to the machine's own resolver, whose addresses the network interfaces or <c>/etc/resolv.conf</c> give. An unknown name is an empty answer; a resolver that cannot be
/// found, does not answer in three seconds or answers with a failure is an exception whose message says so. The answer is read by <see cref="ParseTxt"/> (public for tests).
/// </summary>
public static class DnsTxtResolver
{
    public static async Task<IReadOnlyList<string>> ResolveAsync(string name, CancellationToken ct)
    {
        var servers = Resolvers();
        if (servers.Count == 0) throw new InvalidOperationException("this machine has no DNS resolver to ask (no name server in its network settings or /etc/resolv.conf)");
        var id = (ushort)RandomNumberGenerator.GetInt32(0, 65536);
        var query = Query(id, name);
        Exception? last = null;
        foreach (var server in servers)
        {
            try
            {
                using var socket = new UdpClient(server.AddressFamily);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                await socket.SendAsync(query, new IPEndPoint(server, 53), timeout.Token);
                var answer = await socket.ReceiveAsync(timeout.Token);
                return ParseTxt(answer.Buffer, id);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or InvalidOperationException && !ct.IsCancellationRequested) { last = ex; }
        }
        throw new InvalidOperationException($"the DNS resolver did not answer for {name}: {last?.Message}");
    }

    private static List<IPAddress> Resolvers()
    {
        var found = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
                foreach (var dns in nic.GetIPProperties().DnsAddresses.Where(a => !a.IsIPv6LinkLocal && !a.IsIPv6SiteLocal))
                    if (!found.Contains(dns)) found.Add(dns);
        }
        catch (NetworkInformationException) { }
        if (found.Count == 0 && File.Exists("/etc/resolv.conf"))
            foreach (var line in File.ReadLines("/etc/resolv.conf"))
            {
                var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts is ["nameserver", var address] && IPAddress.TryParse(address, out var ip) && !found.Contains(ip)) found.Add(ip);
            }
        return found;
    }

    private static byte[] Query(ushort id, string name)
    {
        var bytes = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var text = Encoding.ASCII.GetBytes(label);
            if (text.Length is 0 or > 63) throw new ArgumentException($"'{name}' is not a DNS name.");
            bytes.Add((byte)text.Length);
            bytes.AddRange(text);
        }
        bytes.AddRange([0, 0, 16, 0, 1]);   // end of name, TXT, IN
        return [.. bytes];
    }

    /// <summary>The TXT strings of a DNS answer to the question with <paramref name="id"/> (a record's strings are joined, as a resolver's clients do); an unknown name gives none.</summary>
    public static IReadOnlyList<string> ParseTxt(byte[] answer, ushort id)
    {
        if (answer.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(answer) != id) throw new InvalidOperationException("the answer is not for this question");
        var flags = BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(2));
        if ((flags & 0x8000) == 0) throw new InvalidOperationException("the answer is not a response");
        if ((flags & 0x0200) != 0) throw new InvalidOperationException("the answer is too long for one datagram (truncated)");
        var code = flags & 0xF;
        if (code == 3) return [];
        if (code != 0) throw new InvalidOperationException($"the resolver answered with the failure code {code}");
        int questions = BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(4)), answers = BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(6));
        var at = 12;
        for (var i = 0; i < questions; i++) { at = SkipName(answer, at) + 4; }
        var found = new List<string>();
        for (var i = 0; i < answers; i++)
        {
            at = SkipName(answer, at);
            if (at + 10 > answer.Length) throw new InvalidOperationException("the answer is cut short");
            var type = BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(at));
            var length = BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(at + 8));
            at += 10;
            if (at + length > answer.Length) throw new InvalidOperationException("the answer is cut short");
            if (type == 16)
            {
                var text = new StringBuilder();
                for (var p = at; p < at + length;)
                {
                    var part = answer[p++];
                    if (p + part > at + length) throw new InvalidOperationException("the answer is cut short");
                    text.Append(Encoding.UTF8.GetString(answer, p, part));
                    p += part;
                }
                found.Add(text.ToString());
            }
            at += length;
        }
        return found;
    }

    private static int SkipName(byte[] data, int at)
    {
        while (true)
        {
            if (at >= data.Length) throw new InvalidOperationException("the answer is cut short");
            var length = data[at];
            if (length == 0) return at + 1;
            if ((length & 0xC0) == 0xC0) return at + 2;   // a pointer ends the name
            at += 1 + length;
        }
    }
}
