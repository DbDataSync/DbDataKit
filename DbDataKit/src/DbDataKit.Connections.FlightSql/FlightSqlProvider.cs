using System.Data.Common;
using DbDataKit.Dialect;
using DbDataKit.Secrets;

namespace DbDataKit.Connections.FlightSql;

/// <summary>
/// The connection string of a Flight SQL data source is the login value of <see cref="FlightSqlLoginValue"/> (<c>grpc+tls://host:port;token=...</c>), which this composer checks and passes on whole. A server that serves other people's
/// data sources through this provider does not take a token from a program or from its own environment: <c>token_command</c> would run code a data source's author chose, and <c>token_env</c> would read a variable they named, so both
/// are refused here (a token is written as <c>token</c>, or as a user and password, usually as a secret reference). There is no pass-through of a caller's credentials: the upstream server's login is the data source's.
/// </summary>
public sealed class FlightSqlComposer : IConnectionStringComposer
{
    public EngineId Engine { get; } = FlightSqlProvider.EngineName;

    public string Compose(ConnectionContext context)
    {
        string? text = context.Login?.ConnectionString is { IsEmpty: false } whole ? whole.RevealString() : context.Definition.ConnectionString;
        if (string.IsNullOrWhiteSpace(text)) throw new ConnectionPolicyException("a Flight SQL connection needs its address and login in the connection string (grpc://host:port;...)");
        Check(text);
        return text.Trim();
    }

    /// <summary>The string with the token and the password masked, for logs and previews.</summary>
    public string Describe(ConnectionContext context)
    {
        var login = Check(Compose(context));
        var parts = new List<string> { $"{login.Scheme}://{login.HostAndPort}" };
        if (login.Token != null) parts.Add("token=" + SecretValue.Mask);
        if (login.User != null) parts.Add($"user={login.User}");
        if (login.Password != null) parts.Add("password=" + SecretValue.Mask);
        if (login.TlsSkipVerify) parts.Add("tls_skip_verify=true");
        if (login.CaFile != null) parts.Add("ca_file=" + login.CaFile);
        foreach (var (name, _) in login.Headers) parts.Add($"header.{name}=" + SecretValue.Mask);
        return string.Join(';', parts);
    }

    public string ApplyPassThroughCredentials(string baseConnectionString, string user, SecretValue password, ConnectionPolicy? policy = null) =>
        throw new ConnectionPolicyException("a Flight SQL data source takes no pass-through credentials: the upstream login is the data source's");

    /// <summary>Reads the string and refuses what a server may not do for a data source (see the type).</summary>
    public static FlightSqlLoginValue Check(string text)
    {
        FlightSqlLoginValue login;
        try { login = FlightSqlLoginValue.Parse(text); }
        catch (FlightSqlLoginValueException) { throw new ConnectionPolicyException("the Flight SQL connection string does not follow the grammar (grpc://host:port or grpc+tls://host:port, then ;option=value parts)"); }
        if (login.TokenFromOutside) throw new ConnectionPolicyException("token_env and token_command are not available for a Flight SQL data source (give the token as token, or a user and password, as a secret reference)");
        return login;
    }
}

/// <summary>
/// The Flight SQL provider: an upstream Arrow Flight SQL server as an ADO.NET <see cref="DbConnection"/> (<see cref="FlightSqlConnection"/>), read only. Parameters are bound in prepared statements (the connection string cannot choose inline literals: values never become SQL text here).
/// There is no session-level read-only switch to apply, so a statement check and the upstream server's own login are the controls.
/// </summary>
public sealed class FlightSqlProvider : DbProviderBase
{
    public static readonly EngineId EngineName = new("flightsql");

    /// <param name="dialects">Dialect catalog; null for the built-in one.</param>
    /// <param name="redactor">Scrubs the raw failure text kept when <paramref name="includeRawFailures"/> is on.</param>
    /// <param name="includeRawFailures">Keep the redacted failure text on <c>ConnectionFailure.Raw</c> (local diagnostics only).</param>
    public FlightSqlProvider(DialectCatalog? dialects = null, IRedactor? redactor = null, bool includeRawFailures = false)
        : base(EngineName, new FlightSqlComposer(),
            ReadOnlySession.NoneAvailable("flightsql", "Nothing at the session level: the upstream server's login and the statement check are the controls."),
            new FlightSqlFailureMapper(), dialects, redactor, includeRawFailures) { }

    protected override DbConnection NewConnection(string connectionString, ConnectionContext context)
    {
        var login = FlightSqlComposer.Check(connectionString);
        var timeouts = context.Definition.Timeouts;
        var settings = login.ToSettings(login.Token, TimeSpan.FromSeconds(timeouts.ConnectS), TimeSpan.FromSeconds(timeouts.CommandS));
        return new FlightSqlConnection(settings, _ => throw new NotSupportedException("The upstream server does not take bound parameters, and a statement's values are never written into its text here."));
    }
}
