using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DbDataKit.Server;

/// <summary>
/// The single-machine bundle's description (plan 23a FQ18, D8): <c>bundle.json</c> beside the console's data, <c>schema: bundle/1</c>. It says which products are the bundle's members and where the <b>bundle token</b> is; nothing secret is in it.
/// </summary>
/// <param name="TokenFile">The file that holds the token, relative to the console's directory (or absolute).</param>
/// <param name="Members">The products (<c>dbdatasync</c>, <c>dbdatabuild</c>, ...) whose announcements the console pairs with by itself; empty: any product the console manages.</param>
public sealed record BundleFile(string TokenFile, IReadOnlyList<string> Members)
{
    public const string Schema = "bundle/1";
    public const string FileName = "bundle.json";
    public const string DefaultTokenFile = "bundle.token";
}

/// <summary>
/// The bundle token and the rule that uses it (plan 23a D8, QF6): servers installed on the <b>same machine</b> as the console are paired with it with no code typed and nobody confirming, and a server on another machine still announces and waits.
/// A server proves it is one of the bundle's by the token, a file only the install's service accounts can read (mode 0600 on Linux; the console refuses a token file others can read); it puts the token in its announcement and the console accepts it only from
/// <b>loopback</b>. So the announce port is no more a way in than it was: a process on the machine that can read the file could read the console's own secrets too, and anything on the network (no token, or not loopback) is where it was, a line an administrator confirms.
/// </summary>
public sealed class ConsoleBundle
{
    private readonly byte[] _token;
    private readonly HashSet<string> _members;

    private ConsoleBundle(string tokenFile, byte[] token, IReadOnlyList<string> members)
    {
        TokenFile = tokenFile;
        _token = token;
        _members = new HashSet<string>(members, StringComparer.Ordinal);
        Members = members;
    }

    public string TokenFile { get; }

    public IReadOnlyList<string> Members { get; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>The bundle of a console directory: null with no problem when there is no <c>bundle.json</c>, null with the reason when it cannot be used (the console still starts, without automatic pairing).</summary>
    public static (ConsoleBundle? Bundle, string? Problem) Load(string directory)
    {
        var path = Path.Combine(directory, BundleFile.FileName);
        if (!File.Exists(path)) return (null, null);
        BundleDocument? document;
        try { document = JsonSerializer.Deserialize<BundleDocument>(File.ReadAllText(path), Json); }
        catch (JsonException ex) { return (null, $"{path}: the file is not valid JSON ({ex.Message})."); }
        if (document is null || document.Schema != BundleFile.Schema) return (null, $"{path}: the file is not a {BundleFile.Schema} document (its schema is '{document?.Schema}'); this version of the console reads {BundleFile.Schema}.");
        var tokenFile = Path.GetFullPath(Path.Combine(directory, document.TokenFile ?? BundleFile.DefaultTokenFile));
        if (!File.Exists(tokenFile)) return (null, $"{path}: the token file {tokenFile} does not exist; make it with `dbdatakit bundle init`.");
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(tokenFile) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            return (null, $"{tokenFile}: other accounts can read the bundle token, so it is not used; run `chmod 600 {tokenFile}` (or `dbdatakit bundle init --rotate`).");
        var text = File.ReadAllText(tokenFile).Trim();
        if (text.Length < 32) return (null, $"{tokenFile}: the bundle token is missing or too short; make a new one with `dbdatakit bundle init --rotate`.");
        return (new ConsoleBundle(tokenFile, Encoding.UTF8.GetBytes(text), document.Members ?? []), null);
    }

    /// <summary>Makes <c>bundle.json</c> and a token file (readable by the account only). An existing token is kept unless <paramref name="rotate"/>; the members are written as given.</summary>
    public static string Init(string directory, IReadOnlyList<string> members, bool rotate)
    {
        Directory.CreateDirectory(directory);
        var tokenPath = Path.Combine(directory, BundleFile.DefaultTokenFile);
        if (rotate || !File.Exists(tokenPath))
        {
            var temp = tokenPath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            File.WriteAllText(temp, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=') + "\n", new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, tokenPath, overwrite: true);
        }

        var path = Path.Combine(directory, BundleFile.FileName);
        File.WriteAllText(path, JsonSerializer.Serialize(new BundleDocument { Schema = BundleFile.Schema, TokenFile = BundleFile.DefaultTokenFile, Members = [.. members] }, Json) + "\n", new UTF8Encoding(false));
        return tokenPath;
    }

    /// <summary>Whether an announcement is one of the bundle's: the token matches (compared in constant time), it came from this machine, and its product is a member (every product when the file lists none).</summary>
    public bool Accepts(string? token, string product, IPAddress? from)
    {
        if (token is not { Length: > 0 } || from is null) return false;
        var address = from.IsIPv4MappedToIPv6 ? from.MapToIPv4() : from;
        if (!IPAddress.IsLoopback(address)) return false;
        if (_members.Count > 0 && !_members.Contains(product)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), _token);
    }

    private sealed class BundleDocument
    {
        public string? Schema { get; set; }
        public string? TokenFile { get; set; }
        public List<string>? Members { get; set; }
    }
}
