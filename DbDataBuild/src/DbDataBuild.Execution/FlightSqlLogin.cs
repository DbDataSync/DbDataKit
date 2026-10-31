using DbDataKit.Connections.FlightSql;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace DbDataBuild.Execution;

/// <summary>
/// A Flight SQL login value that cannot be used, or a token that cannot be had. The message is ours and says what is wrong with the grammar or the source of the token; it never holds a part of the value (the value
/// carries a token or a password) and never what a command printed.
/// </summary>
public sealed class FlightSqlLoginException(string message) : Exception(message);

/// <summary>
/// The value of a Flight SQL connection's login, `DBDATA_BUILD_&lt;NAME&gt;_READ`: a URI and options separated by `;`, written like an ODBC connection string (a value in braces may hold `;` and `=`, and `}}` is a `}`).
/// <code>grpc+tls://proxy.example.com:31337;token=eyJ...
/// grpc://127.0.0.1:31337;user=reader;password={p;w}
/// grpc+tls://proxy.example.com:31337;token_command={az account get-access-token --resource api://proxy --query accessToken -o tsv}</code>
/// The schemes are `grpc` and `grpc+tcp` (no TLS) and `grpc+tls`. The options are `token` (a bearer token), `token_env` (the name of an environment variable that holds one), `token_command` (a program the tool runs when it
/// connects; it prints the token), `user` and `password` (the server's basic-authentication handshake, which trades them for a bearer token), `tls_skip_verify` (`true`: do not check the server's certificate),
/// `ca_file` (a PEM file of the certificate authority to trust instead of the machine's) and `header.&lt;name&gt;` (a header sent with every call). At most one way to authenticate; none means the server is called without credentials.
/// The tool only reads the value to connect and to say who the login is and where it goes; it never prints it.
/// </summary>
public sealed record FlightSqlLogin(string Scheme, string Host, int Port, string? Token, string? TokenEnv, string? TokenCommand, string? User, string? Password, bool TlsSkipVerify, string? CaFile, IReadOnlyList<(string Name, string Value)> Headers)
{
    public bool Tls => Scheme == "grpc+tls";

    /// <summary>`host:port`: what the command header and `connection inspect` show of the login.</summary>
    public string HostAndPort => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";

    /// <summary>The address Grpc.Net.Client takes: the scheme is http or https, which is how it is told to use TLS.</summary>
    public Uri Address => new($"{(Tls ? "https" : "http")}://{HostAndPort}");

    /// <summary>How the login authenticates, in words (no secret): `bearer token`, `user bob`, `no credentials`.</summary>
    public string AuthenticationDescription => Token != null ? "bearer token" : TokenEnv != null ? "bearer token from the environment" : TokenCommand != null ? "bearer token from a command"
        : User != null ? $"user {User}" : "no credentials";

    /// <summary>Reads the value (the grammar is the shared one, <see cref="FlightSqlLoginValue"/>). Throws <see cref="FlightSqlLoginException"/> (a sentence of ours, no part of the value in it) when it does not follow the grammar.</summary>
    public static FlightSqlLogin Parse(string value)
    {
        try
        {
            var v = FlightSqlLoginValue.Parse(value);
            return new(v.Scheme, v.Host, v.Port, v.Token, v.TokenEnv, v.TokenCommand, v.User, v.Password, v.TlsSkipVerify, v.CaFile, v.Headers);
        }
        catch (FlightSqlLoginValueException ex) { throw new FlightSqlLoginException(ex.Message); }
    }

    /// <summary>The user of a value, or null when it names none or cannot be read (the command header only).</summary>
    public static string? UserOf(string value)
    {
        try { return Parse(value).User; }
        catch (FlightSqlLoginException) { return null; }
    }

    /// <summary>
    /// The bearer token this login uses, or null when it has none (a user and password are traded for one by the handshake; no credentials means none). A `token_command` is run now, so a short-lived token
    /// (Entra ID, an OIDC provider) is fetched at each connection: the program is started directly, not through a shell, with its output read and discarded after the first line is taken.
    /// </summary>
    public async Task<string?> ResolveTokenAsync(CancellationToken ct, Func<string, string?>? env = null)
    {
        if (Token != null) return Token;
        if (TokenEnv != null)
        {
            var fromEnvironment = (env ?? Environment.GetEnvironmentVariable)(TokenEnv);
            if (string.IsNullOrWhiteSpace(fromEnvironment)) throw new FlightSqlLoginException("The environment variable named by token_env in the Flight SQL login is not set or is empty.");
            return fromEnvironment.Trim();
        }
        return TokenCommand == null ? null : await RunTokenCommandAsync(TokenCommand, ct);
    }

    /// <summary>`az account get-access-token --query accessToken -o tsv` into its program and arguments: spaces separate them, double quotes keep spaces together, and nothing else is special (no shell).</summary>
    public static IReadOnlyList<string> SplitCommand(string command)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var started = false;
        foreach (var c in command)
        {
            if (c == '"') { inQuotes = !inQuotes; started = true; }
            else if (char.IsWhiteSpace(c) && !inQuotes) { if (started) { parts.Add(current.ToString()); current.Clear(); started = false; } }
            else { current.Append(c); started = true; }
        }
        if (started) parts.Add(current.ToString());
        return parts;
    }

    private static async Task<string> RunTokenCommandAsync(string command, CancellationToken ct)
    {
        var parts = SplitCommand(command);
        if (parts.Count == 0) throw new FlightSqlLoginException("The token_command of the Flight SQL login is empty.");
        var start = new ProcessStartInfo(parts[0]) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in parts.Skip(1)) start.ArgumentList.Add(a);
        Process? process;
        try { process = Process.Start(start); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { throw new FlightSqlLoginException("The token_command of the Flight SQL login could not be started (is the program on the path?)."); }
        if (process == null) throw new FlightSqlLoginException("The token_command of the Flight SQL login could not be started.");
        using (process)
        {
            process.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);                                  // read so the program does not block on a full pipe; never shown
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                var text = (await output).Trim();
                _ = await errors;
                if (process.ExitCode != 0) throw new FlightSqlLoginException($"The token_command of the Flight SQL login exited with code {process.ExitCode.ToString(CultureInfo.InvariantCulture)}.");
                if (text.Length == 0) throw new FlightSqlLoginException("The token_command of the Flight SQL login printed no token.");
                if (text.Any(char.IsWhiteSpace)) throw new FlightSqlLoginException("The token_command of the Flight SQL login printed more than the token (it must print the token alone: a single word).");
                return text;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(true); } catch (Exception) { /* it ended */ }
                throw new FlightSqlLoginException("The token_command of the Flight SQL login did not finish in 60 seconds.");
            }
        }
    }
}
