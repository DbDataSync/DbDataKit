using DbDataKit.Connections;
using DbDataProxy.Data;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Data;
using DbDataProxy.Tests.Support;
using Microsoft.Data.SqlClient;

namespace DbDataProxy.Tests.Data;

/// <summary>How a configured data source becomes a shared <see cref="ConnectionDefinition"/> and what the shared composer makes of it.</summary>
public class ConnectionDefinitionTests
{
    private static string Compose(string provider, string connectionString, Action<DataSourceOptions>? edit = null)
    {
        var options = new DataSourceOptions { Provider = provider, ConnectionString = connectionString };
        edit?.Invoke(options);
        var registry = TestSources.Registry();
        var definition = DataSourceDefinitions.Build("src", options, connectionString);
        return SourceConnection.Create(registry.Get(provider), definition, connectionString).Compose();
    }

    [Fact]
    public void Proxy_only_settings_travel_in_the_proxy_extension()
    {
        var options = new DataSourceOptions
        {
            Provider = "odbc", ConnectionString = "Driver={X}", Bitness = "x86", PassThrough = true, AllowWrites = true, BatchSize = 1000,
            Metadata = "informationschema", Classifier = ClassifierMode.Strict,
        };
        var definition = DataSourceDefinitions.Build("legacy", options, options.ConnectionString);

        Assert.Equal("legacy", definition.Name);
        Assert.Equal("odbc", definition.Engine);
        Assert.Equal(AccessMode.Readwrite, definition.Access);
        Assert.Equal("x86", DataSourceDefinitions.ExtensionValue(definition, "bitness"));
        Assert.Equal("true", DataSourceDefinitions.ExtensionValue(definition, "pass_through"));
        Assert.Equal("true", DataSourceDefinitions.ExtensionValue(definition, "allow_writes"));
        Assert.Equal("1000", DataSourceDefinitions.ExtensionValue(definition, "batch_size"));
        Assert.Equal("informationschema", DataSourceDefinitions.ExtensionValue(definition, "metadata"));
        Assert.Equal("strict", DataSourceDefinitions.ExtensionValue(definition, "classifier"));
    }

    [Fact]
    public void A_source_without_writes_is_a_read_only_connection()
    {
        Assert.Equal(AccessMode.Readonly, DataSourceDefinitions.Build("s", new DataSourceOptions { Provider = "duckdb" }, "Data Source=x").Access);
        Assert.Equal(AccessMode.Readwrite, DataSourceDefinitions.Build("s", new DataSourceOptions { Provider = "duckdb", AllowWrites = true }, "Data Source=x").Access);
    }

    [Fact]
    public void Sql_server_without_tls_keys_gets_encrypt_and_verify()
    {
        var b = new SqlConnectionStringBuilder(Compose("sqlserver", "Server=db1;Database=d;User Id=u;Password=p"));
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, b.Encrypt);
        Assert.False(b.TrustServerCertificate);
    }

    [Fact]
    public void An_explicit_trust_server_certificate_in_the_string_is_kept()
    {
        var b = new SqlConnectionStringBuilder(Compose("sqlserver", "Server=db1;User Id=u;Password=p;Encrypt=True;TrustServerCertificate=True"));
        Assert.True(b.TrustServerCertificate);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, b.Encrypt);

        var plain = new SqlConnectionStringBuilder(Compose("sqlserver", "Server=db1;User Id=u;Password=p;Encrypt=False"));
        Assert.Equal(SqlConnectionEncryptOption.Optional, plain.Encrypt);
        Assert.False(plain.TrustServerCertificate);

        var strict = new SqlConnectionStringBuilder(Compose("sqlserver", "Server=db1;User Id=u;Password=p;Encrypt=Strict"));
        Assert.Equal(SqlConnectionEncryptOption.Strict, strict.Encrypt);
    }

    [Fact]
    public void Odbc_sql_server_drivers_keep_explicit_tls_keys_and_otherwise_get_the_secure_default()
    {
        var kept = DbDataKit.Odbc.Metadata.OdbcConnectionString.Parse(Compose("odbc", "Driver={ODBC Driver 18 for SQL Server};Server=s;Encrypt=no;TrustServerCertificate=yes"));
        Assert.Equal("no", kept.Single(p => p.Key.Equals("encrypt", StringComparison.OrdinalIgnoreCase)).Value);
        Assert.Equal("yes", kept.Single(p => p.Key.Equals("trustservercertificate", StringComparison.OrdinalIgnoreCase)).Value);

        var secure = DbDataKit.Odbc.Metadata.OdbcConnectionString.Parse(Compose("odbc", "Driver={ODBC Driver 18 for SQL Server};Server=s;UID=u;PWD=p"));
        Assert.Equal("yes", secure.Single(p => p.Key.Equals("encrypt", StringComparison.OrdinalIgnoreCase)).Value);
        Assert.Equal("no", secure.Single(p => p.Key.Equals("trustservercertificate", StringComparison.OrdinalIgnoreCase)).Value);
    }

    [Fact]
    public void The_legacy_odbc_sql_server_driver_and_other_drivers_are_left_alone()
    {
        var legacy = Compose("odbc", "Driver={SQL Server};Server=s;Trusted_Connection=Yes");
        Assert.DoesNotContain("Encrypt", legacy, StringComparison.OrdinalIgnoreCase);
        var text = Compose("odbc", "Driver={Microsoft Access Text Driver (*.txt, *.csv)};DefaultDir=C:\\data");
        Assert.DoesNotContain("Encrypt", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DefaultDir", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Timeouts_in_the_string_survive_and_the_source_timeout_wins_for_commands()
    {
        var b = new SqlConnectionStringBuilder(Compose("sqlserver", "Server=db1;User Id=u;Password=p;Connect Timeout=45"));
        Assert.Equal(45, b.ConnectTimeout);

        var withCommand = new SqlConnectionStringBuilder(Compose("sqlserver", "Server=db1;User Id=u;Password=p", o => o.CommandTimeoutSeconds = 77));
        Assert.Equal(77, withCommand.CommandTimeout);
        Assert.Equal(15, withCommand.ConnectTimeout);
        Assert.Equal(300, DataSourceDefinitions.Build("s", new DataSourceOptions { Provider = "sqlserver" }, "Server=x").Timeouts.CommandS);
    }

    [Fact]
    public void Hints_report_tls_opt_outs_for_the_startup_warning()
    {
        Assert.True(ConnectionHints.OptsOutOfTls("sqlserver", "Server=a;TrustServerCertificate=True"));
        Assert.True(ConnectionHints.OptsOutOfTls("sqlserver", "Server=a;Encrypt=False"));
        Assert.False(ConnectionHints.OptsOutOfTls("sqlserver", "Server=a;Encrypt=True"));
        Assert.False(ConnectionHints.OptsOutOfTls("sqlserver", "Server=a"));
        Assert.True(ConnectionHints.OptsOutOfTls("odbc", "Driver={ODBC Driver 18 for SQL Server};TrustServerCertificate=yes"));
        Assert.False(ConnectionHints.OptsOutOfTls("odbc", "not a connection string {"));
    }

    [Fact]
    public void Odbc_policy_refuses_file_dsn_and_save_file_in_configured_strings()
    {
        Assert.Throws<ConnectionPolicyException>(() => Compose("odbc", "FILEDSN=C:\\x.dsn"));
        Assert.Throws<ConnectionPolicyException>(() => Compose("odbc", "Driver={X};SAVEFILE=C:\\x.dsn"));
    }
}
