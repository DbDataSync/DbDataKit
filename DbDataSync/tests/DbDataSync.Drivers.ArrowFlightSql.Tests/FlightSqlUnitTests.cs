using System.Data;
using System.Reflection;
using Apache.Arrow;
using Apache.Arrow.Types;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Drivers.ArrowFlightSql.Tests;

public sealed class ParameterMarkerTests
{
    private static string Positional(string sql, params string[] names) =>
        ParameterMarkers.Replace(sql, names, _ => "?");

    [Fact]
    public void Replaces_A_Known_Marker_Once_Per_Use()
    {
        Assert.Equal("a = ? OR b = ? OR c = ?", Positional("a = @x OR b = @y OR c = @x", "x", "y"));
    }

    [Theory]
    [InlineData("a = :x", "a = ?")]
    [InlineData("a = $x", "a = ?")]
    [InlineData("a = @X", "a = ?")] // names are case-insensitive
    public void Understands_Every_Dialect_Spelling(string sql, string expected) =>
        Assert.Equal(expected, Positional(sql, "x"));

    [Theory]
    [InlineData("note = 'mail @x'")]
    [InlineData("note = 'it''s @x'")]
    [InlineData("\"@x\" = 1")]
    [InlineData("`@x` = 1")]
    [InlineData("-- @x\nSELECT 1")]
    [InlineData("/* @x */ SELECT 1")]
    [InlineData("SELECT @@version")]
    [InlineData("SELECT a::int, b::text")]
    [InlineData("SELECT $$@x$$")]
    [InlineData("SELECT $q$ @x $q$, 1")]
    [InlineData("SELECT $1")]
    [InlineData("SELECT email@x")]
    public void Leaves_Look_Alikes_Alone(string sql) => Assert.Equal(sql, Positional(sql, "x"));

    [Fact]
    public void An_Unknown_Name_Is_Not_Touched() =>
        Assert.Equal("a = @other", Positional("a = @other", "x"));

    [Fact]
    public void Reports_The_Order_Of_Use()
    {
        Assert.Equal(["b", "a", "b"], ParameterMarkers.Used("x = @b AND y = @a AND z = @b", ["a", "b"]));
    }
}

public sealed class SettingsTests
{
    private static ConnectionConfig Config(
        AuthMode auth = AuthMode.None, string? host = "flight.example", int? port = 443, string? connectionString = null,
        string? user = null, params (string Key, string Value)[] properties) => new()
    {
        Name = "c",
        DriverType = DriverIds.ArrowFlightSql,
        AuthMode = auth,
        Host = connectionString is null ? host : null,
        Port = connectionString is null ? port : null,
        ConnectionString = connectionString,
        UserId = user,
        Properties = properties.ToDictionary(p => p.Key, p => p.Value),
    };

    [Theory]
    [InlineData("flight.example:443", "https://flight.example/")] // Uri drops a scheme's default port
    [InlineData("flight.example:8443", "https://flight.example:8443/")]
    [InlineData("grpc://flight.example:32010", "http://flight.example:32010/")]
    [InlineData("grpc+tcp://flight.example:32010", "http://flight.example:32010/")]
    [InlineData("grpc+tls://flight.example:8443", "https://flight.example:8443/")]
    [InlineData("https://flight.example", "https://flight.example/")]
    [InlineData("http://localhost:8080", "http://localhost:8080/")]
    public void Reads_Every_Address_Spelling(string address, string expected) =>
        Assert.Equal(expected, FlightSqlSettings.From(Config(connectionString: address), null).Address.ToString());

    [Fact]
    public void Data_Source_And_Compression_Are_Optional_Text_Settings()
    {
        var none = FlightSqlSettings.From(Config(), null);
        Assert.Null(none.DataSource);
        Assert.Null(none.Compression);

        var set = FlightSqlSettings.From(Config(properties:
            (FlightSqlSettings.DataSourceKey, " erp ")), null);
        Assert.Equal("erp", set.DataSource);
        Assert.Equal("zstd", FlightSqlSettings.From(Config(properties: (FlightSqlSettings.CompressionKey, "zstd")), null).Compression);

        Assert.Null(FlightSqlSettings.From(Config(properties: (FlightSqlSettings.DataSourceKey, "  ")), null).DataSource);
    }

    [Fact]
    public void Host_And_Port_Use_Tls_By_Default_And_Plain_Text_When_Told_To()
    {
        Assert.Equal("https://flight.example/", FlightSqlSettings.From(Config(), null).Address.ToString());
        Assert.Equal("http://flight.example:443/",
            FlightSqlSettings.From(Config(properties: (FlightSqlSettings.UseTlsKey, "false")), null).Address.ToString());
    }

    [Fact]
    public void An_Unknown_Scheme_Is_Rejected_With_The_Ones_That_Work() =>
        Assert.Contains("grpc+tls://",
            Assert.Throws<InvalidOperationException>(() => FlightSqlSettings.From(Config(connectionString: "ftp://x"), null)).Message);

    [Fact]
    public void An_Address_Is_Required() =>
        Assert.Throws<InvalidOperationException>(() => FlightSqlSettings.From(Config(host: null, connectionString: null), null));

    [Fact]
    public void Sql_Auth_Defaults_To_Basic()
    {
        var s = FlightSqlSettings.From(Config(AuthMode.SqlAuth, user: "svc"), "pw");
        Assert.Equal(FlightSqlAuthMethod.Basic, s.Auth);
        Assert.Equal("svc", s.UserId);
        Assert.Equal("pw", s.Credential);
    }

    [Fact]
    public void Bearer_Carries_The_Token_And_No_User()
    {
        var s = FlightSqlSettings.From(
            Config(AuthMode.SqlAuth, user: "ignored", properties: (FlightSqlSettings.AuthMethodKey, "bearer")), "tok");
        Assert.Equal(FlightSqlAuthMethod.Bearer, s.Auth);
        Assert.Null(s.UserId);
        Assert.Equal("tok", s.Credential);
    }

    [Fact]
    public void No_Auth_Drops_Any_Credential()
    {
        var s = FlightSqlSettings.From(Config(AuthMode.None), "left-over");
        Assert.Equal(FlightSqlAuthMethod.None, s.Auth);
        Assert.Null(s.Credential);
    }

    [Fact]
    public void Basic_Needs_A_User_And_A_Password()
    {
        Assert.Contains("user id", Assert.Throws<InvalidOperationException>(
            () => FlightSqlSettings.From(Config(AuthMode.SqlAuth), "pw")).Message);
        Assert.Contains("password", Assert.Throws<InvalidOperationException>(
            () => FlightSqlSettings.From(Config(AuthMode.SqlAuth, user: "svc"), null)).Message);
    }

    [Fact]
    public void Bearer_Needs_A_Token() =>
        Assert.Contains("token", Assert.Throws<InvalidOperationException>(() => FlightSqlSettings.From(
            Config(AuthMode.SqlAuth, properties: (FlightSqlSettings.AuthMethodKey, "Bearer")), null)).Message);

    [Fact]
    public void Integrated_Auth_Is_Refused() =>
        Assert.Throws<NotSupportedException>(() => FlightSqlSettings.From(Config(AuthMode.IntegratedAuth), null));

    [Fact]
    public void Certificate_Validation_Is_Checked_Unless_Skipped_Explicitly()
    {
        Assert.False(FlightSqlSettings.From(Config(), null).SkipCertificateValidation);
        Assert.True(FlightSqlSettings.From(
            Config(properties: (FlightSqlSettings.SkipCertificateValidationKey, "true")), null).SkipCertificateValidation);
    }

    [Fact]
    public void Parameters_Are_Prepared_Unless_Inline_Is_Chosen()
    {
        Assert.Equal(FlightSqlParameterMode.Prepared, FlightSqlSettings.From(Config(), null).ParameterMode);
        Assert.Equal(FlightSqlParameterMode.Inline, FlightSqlSettings.From(
            Config(properties: (FlightSqlSettings.ParameterModeKey, "inline")), null).ParameterMode);
    }

    [Fact]
    public void Timeouts_Default_And_Zero_Means_Unlimited()
    {
        var defaults = FlightSqlSettings.From(Config(), null);
        Assert.Equal(TimeSpan.FromSeconds(ConnectionTimeouts.DefaultConnectSeconds), defaults.ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(ConnectionTimeouts.DefaultCommandSeconds), defaults.CommandTimeout);

        var config = Config();
        config.ConnectTimeoutSeconds = 0;
        config.CommandTimeoutSeconds = 0;
        var unlimited = FlightSqlSettings.From(config, null);
        Assert.Equal(Timeout.InfiniteTimeSpan, unlimited.ConnectTimeout);
        Assert.Equal(Timeout.InfiniteTimeSpan, unlimited.CommandTimeout);
    }
}

public sealed class ConnectionFormTests
{
    private static IReadOnlyList<ParameterDescriptor> Form(params (string Key, string Value)[] values) =>
        ((IDriver)new ArrowFlightSqlDriver()).ConnectionParameters(values.ToDictionary(v => v.Key, v => v.Value));

    private static ParameterDescriptor Find(IReadOnlyList<ParameterDescriptor> form, string name) =>
        form.Single(p => p.Name == name);

    [Fact]
    public void Basic_Shows_User_And_Password()
    {
        var form = Form();
        Assert.True(Find(form, DriverParameters.UserId).Visible);
        Assert.Equal("Password", Find(form, DriverParameters.Password).Label);
    }

    [Fact]
    public void Bearer_Hides_The_User_And_Relabels_The_Secret_As_A_Token()
    {
        var form = Form(("properties.authMethod", "Bearer"));
        Assert.False(Find(form, DriverParameters.UserId).Visible);
        Assert.Equal("Bearer token", Find(form, DriverParameters.Password).Label);
        Assert.Equal(ParameterType.Secret, Find(form, DriverParameters.Password).Type);
    }

    [Fact]
    public void No_Auth_Hides_Every_Credential_Field()
    {
        var form = Form((DriverParameters.AuthMode, nameof(AuthMode.None)));
        Assert.False(Find(form, DriverParameters.UserId).Visible);
        Assert.False(Find(form, DriverParameters.Password).Visible);
        Assert.False(Find(form, "properties.authMethod").Visible);
    }

    [Fact]
    public void Integrated_Auth_Is_Not_Offered() =>
        Assert.DoesNotContain(nameof(AuthMode.IntegratedAuth), Find(Form(), DriverParameters.AuthMode).DropdownOptions!);

    [Fact]
    public void Offers_Tls_And_Certificate_Skipping_Which_Only_Applies_To_Tls()
    {
        Assert.True(Find(Form(), "properties.skipCertificateValidation").Visible);
        Assert.False(Find(Form(("properties.useTls", "false")), "properties.skipCertificateValidation").Visible);
        Assert.Contains("identity is not checked", Find(Form(), "properties.skipCertificateValidation").Description);
    }

    [Fact]
    public void Connection_String_Addressing_Hides_Host_And_Port()
    {
        var form = Form((DriverParameters.AddressMode, DriverParameters.ConnectionStringAddressing));
        Assert.False(Find(form, DriverParameters.Host).Visible);
        Assert.False(Find(form, DriverParameters.Port).Visible);
        Assert.True(Find(form, DriverParameters.ConnectionString).Visible);
    }

    private static IReadOnlyList<string> SaveProblems(ConnectionInput input)
    {
        // What ParameterCheck.ThrowIfInvalid does when a connection is saved.
        IDriver driver = new ArrowFlightSqlDriver();
        var values = DriverParameters.ValuesOf(input);
        return ParameterValidation.Validate(driver.ConnectionParameters(values), values, "test");
    }

    [Fact]
    public void Saved_Basic_Bearer_And_Open_Connections_Pass_The_Same_Validation_The_Api_Applies()
    {
        Assert.Empty(SaveProblems(new ConnectionInput
        {
            Name = "basic", DriverType = DriverIds.ArrowFlightSql, Host = "flight.example", Port = 443,
            AuthMode = AuthMode.SqlAuth, UserId = "svc", Password = "pw",
        }));

        Assert.Empty(SaveProblems(new ConnectionInput
        {
            Name = "bearer", DriverType = DriverIds.ArrowFlightSql, ConnectionString = "grpc+tls://flight.example:443",
            AuthMode = AuthMode.SqlAuth, Password = "token",
            Properties = new()
            {
                ["authMethod"] = "Bearer", ["useTls"] = "true", ["skipCertificateValidation"] = "true",
                ["parameterMode"] = "Inline", ["some.custom"] = "x",
            },
        }));

        Assert.Empty(SaveProblems(new ConnectionInput
        {
            Name = "open", DriverType = DriverIds.ArrowFlightSql, Host = "localhost", Port = 32010, AuthMode = AuthMode.None,
            // A leftover from before the method was switched off: hidden, so not validated.
            Properties = new() { ["authMethod"] = "Bearer", ["useTls"] = "false" },
        }));
    }

    [Fact]
    public void An_Unknown_Auth_Method_Is_Rejected_At_Save()
    {
        var problems = SaveProblems(new ConnectionInput
        {
            Name = "c", DriverType = DriverIds.ArrowFlightSql, Host = "h", Port = 1, AuthMode = AuthMode.SqlAuth,
            Properties = new() { ["authMethod"] = "Kerberos" },
        });

        Assert.Contains(problems, p => p.Contains("Kerberos") || p.Contains("authMethod") || p.Contains("Method"));
    }

    [Fact]
    public void Has_No_Default_Port() => Assert.Null(Find(Form(), DriverParameters.Port).Default);

    [Fact]
    public void Free_Form_Properties_Stay_Last_And_Every_Name_Is_Unique()
    {
        var form = Form();
        Assert.Equal(DriverParameters.Properties, form[^1].Name);
        Assert.Equal(form.Count, form.Select(p => p.Name).Distinct().Count());
    }
}

public sealed class DriverRegistrationTests
{
    [Fact]
    public void There_Is_One_Driver_Per_Id_And_The_Ids_Match_The_Constants()
    {
        var drivers = ArrowFlightSqlDriver.CreateAll();
        Assert.Equal(DriverIds.ArrowFlightSqlVariants, drivers.Select(d => d.DriverType));
        Assert.Equal(drivers.Count, drivers.Select(d => d.DisplayName).Distinct().Count());
    }

    [Fact]
    public void Every_Variant_Is_A_Source_Only_Driver_With_One_Reader()
    {
        foreach (var driver in ArrowFlightSqlDriver.CreateAll())
        {
            Assert.Single(driver.Readers);
            Assert.Empty(driver.Writers);
            Assert.Empty(driver.StagingProviders);
            Assert.Equal("BatchReload", driver.Readers[0].Kind);
        }
    }

    [Fact]
    public void Registers_And_Describes_Itself_Like_Any_Driver()
    {
        var registry = new DriverRegistry();
        foreach (var driver in ArrowFlightSqlDriver.CreateAll())
            registry.Register(driver);

        var caps = registry.Describe(DriverIds.ArrowFlightSqlPostgres)!;
        Assert.True(caps.SupportsConnectionTest);
        Assert.Equal("SELECT 1", caps.DefaultTestQuery);
        Assert.Equal("SELECT 1 FROM DUAL", registry.Describe(DriverIds.ArrowFlightSqlOracle)!.DefaultTestQuery);
    }
}

public sealed class DialectTests
{
    [Fact]
    public void Every_Overridable_Member_Of_SqlDialect_Is_Forwarded()
    {
        // A member left out would silently use the base class's ANSI default under an engine that does
        // it differently — and nothing would fail.
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var overridable = typeof(SqlDialect).GetMembers(flags)
            .OfType<MethodInfo>()
            .Where(m => (m.IsVirtual || m.IsAbstract) && !m.IsFinal && (m.IsPublic || m.IsFamily))
            .Where(m => m.Name is not ("Finalize"))
            .Select(m => m.Name)
            .ToHashSet();

        var forwarded = typeof(FlightSqlDialect).GetMembers(flags)
            .OfType<MethodInfo>()
            .Where(m => m.GetBaseDefinition().DeclaringType == typeof(SqlDialect))
            .Select(m => m.Name)
            .ToHashSet();

        Assert.Empty(overridable.Except(forwarded));
    }

    private static FlightSqlDialect For(string id) =>
        (FlightSqlDialect)new ArrowFlightSqlDriver(ArrowFlightSqlDriver.Variants.Single(v => v.DriverType == id)).Dialect;

    [Theory]
    [InlineData(DriverIds.ArrowFlightSql, "\"a\"")]
    [InlineData(DriverIds.ArrowFlightSqlPostgres, "\"a\"")]
    [InlineData(DriverIds.ArrowFlightSqlMsSql, "[a]")]
    [InlineData(DriverIds.ArrowFlightSqlMySql, "`a`")]
    [InlineData(DriverIds.ArrowFlightSqlOracle, "\"a\"")]
    [InlineData(DriverIds.ArrowFlightSqlDuckDb, "\"a\"")]
    public void Quotes_Identifiers_As_The_Named_Engine_Does(string id, string expected) =>
        Assert.Equal(expected, For(id).QuoteIdentifier("a"));

    [Theory]
    [InlineData(DriverIds.ArrowFlightSqlMsSql, "[dbo].[My.Table]", new[] { "dbo", "My.Table" })]
    [InlineData(DriverIds.ArrowFlightSqlMySql, "`db`.`t`", new[] { "db", "t" })]
    [InlineData(DriverIds.ArrowFlightSqlPostgres, "\"s\".\"t.x\"", new[] { "s", "t.x" })]
    public void Splits_Qualified_Names_With_The_Right_Quote_Characters(string id, string name, string[] parts) =>
        Assert.Equal(parts, For(id).SplitQualifiedName(name));

    [Theory]
    [InlineData(DriverIds.ArrowFlightSqlMsSql, "@p")]
    [InlineData(DriverIds.ArrowFlightSqlPostgres, "@p")]
    [InlineData(DriverIds.ArrowFlightSqlOracle, ":p")]
    [InlineData(DriverIds.ArrowFlightSqlDuckDb, "$p")]
    public void References_Parameters_The_Way_The_Named_Engine_Does(string id, string expected) =>
        Assert.Equal(expected, For(id).ParameterReference("p"));

    [Fact]
    public void Limits_Rows_The_Way_The_Named_Engine_Does()
    {
        Assert.Equal(("", " LIMIT 5"), For(DriverIds.ArrowFlightSqlDuckDb).RenderRowLimit(5));
        Assert.Equal(MsSqlDialect.Instance.RenderRowLimit(5), For(DriverIds.ArrowFlightSqlMsSql).RenderRowLimit(5));
        Assert.Equal(("", "\nFETCH FIRST 5 ROWS ONLY"), For(DriverIds.ArrowFlightSql).RenderRowLimit(5));
    }

    [Fact]
    public async Task Selecting_A_Database_Sends_Nothing_And_Never_Throws()
    {
        // The SQL Server dialect's own would issue USE [db]; a proxy has no such statement to run.
        var connection = new FlightSqlConnection(
            FlightSqlSettings.From(new ConnectionConfig
            {
                Name = "c", DriverType = DriverIds.ArrowFlightSqlMsSql, AuthMode = AuthMode.None, Host = "h",
            }, null),
            For(DriverIds.ArrowFlightSqlMsSql));

        await For(DriverIds.ArrowFlightSqlMsSql).UseDatabaseAsync(connection, "sales", default);

        Assert.Equal("sales", connection.Database);
    }

    [Theory]
    [InlineData("bigint", CanonicalTypeKind.Int64)]
    [InlineData("integer", CanonicalTypeKind.Int32)]
    [InlineData("smallint", CanonicalTypeKind.Int16)]
    [InlineData("tinyint", CanonicalTypeKind.Int8)]
    [InlineData("boolean", CanonicalTypeKind.Boolean)]
    [InlineData("real", CanonicalTypeKind.Float)]
    [InlineData("double precision", CanonicalTypeKind.Double)]
    [InlineData("decimal(18,2)", CanonicalTypeKind.Decimal)]
    [InlineData("varchar", CanonicalTypeKind.String)]
    [InlineData("varbinary", CanonicalTypeKind.Binary)]
    [InlineData("date", CanonicalTypeKind.Date)]
    [InlineData("time", CanonicalTypeKind.Time)]
    [InlineData("timestamp", CanonicalTypeKind.Timestamp)]
    [InlineData("timestamp with time zone", CanonicalTypeKind.TimestampTz)]
    public void Reads_The_Ansi_Names_A_Flight_Column_Reports_Under_Every_Variant(string native, CanonicalTypeKind kind)
    {
        foreach (var variant in ArrowFlightSqlDriver.Variants)
            Assert.Equal(kind, For(variant.DriverType).ToCanonicalType(native).Kind);
    }

    [Fact]
    public void Decimal_Keeps_Its_Precision_And_Scale()
    {
        var type = For(DriverIds.ArrowFlightSql).ToCanonicalType("decimal(18,2)");
        Assert.Equal(18, type.Precision);
        Assert.Equal(2, type.Scale);
    }

    [Fact]
    public void Falls_Back_To_The_Named_Engines_Own_Type_Names()
    {
        // `nvarchar` is not an ANSI name; the SQL Server variant still understands it.
        Assert.Equal(CanonicalTypeKind.String, For(DriverIds.ArrowFlightSqlMsSql).ToCanonicalType("nvarchar(50)").Kind);
        Assert.Equal(CanonicalTypeKind.Unmappable, For(DriverIds.ArrowFlightSql).ToCanonicalType("nvarchar(50)").Kind);
    }

    [Fact]
    public void Every_Arrow_Type_Name_Maps_To_Something_Provisioning_Can_Use()
    {
        IArrowType[] types =
        [
            BooleanType.Default, Int8Type.Default, Int16Type.Default, Int32Type.Default, Int64Type.Default,
            UInt8Type.Default, UInt16Type.Default, UInt32Type.Default, UInt64Type.Default, FloatType.Default,
            DoubleType.Default, new Decimal128Type(10, 2), StringType.Default, BinaryType.Default, Date32Type.Default,
            new TimestampType(TimeUnit.Microsecond, (string?)null), new TimestampType(TimeUnit.Microsecond, "UTC"),
            new Time64Type(TimeUnit.Microsecond),
        ];

        foreach (var type in types)
        {
            var canonical = AnsiDialect.Instance.ToCanonicalType(ArrowValues.NativeTypeName(type));
            Assert.NotEqual(CanonicalTypeKind.Unmappable, canonical.Kind);
        }
    }
}

public sealed class ArrowValueTests
{
    [Fact]
    public void Reads_Scalars_And_Nulls()
    {
        var ints = new Int32Array.Builder().Append(7).AppendNull().Build();
        Assert.Equal(7, ArrowValues.ValueAt(ints, 0));
        Assert.Null(ArrowValues.ValueAt(ints, 1));

        Assert.Equal(true, ArrowValues.ValueAt(new BooleanArray.Builder().Append(true).Build(), 0));
        Assert.Equal(1.5, ArrowValues.ValueAt(new DoubleArray.Builder().Append(1.5).Build(), 0));
        Assert.Equal("héllo", ArrowValues.ValueAt(new StringArray.Builder().Append("héllo").Build(), 0));
        Assert.Equal(new byte[] { 1, 2 }, ArrowValues.ValueAt(new BinaryArray.Builder().Append([1, 2]).Build(), 0));
        Assert.Equal((byte)200, ArrowValues.ValueAt(new UInt8Array.Builder().Append(200).Build(), 0));
    }

    [Fact]
    public void Decimal_Keeps_Its_Scale()
    {
        var array = new Decimal128Array.Builder(new Decimal128Type(18, 3)).Append(12.345m).Build();
        Assert.Equal(12.345m, ArrowValues.ValueAt(array, 0));
    }

    [Fact]
    public void An_Unzoned_Timestamp_Is_A_DateTime_And_A_Zoned_One_Keeps_Its_Instant()
    {
        var when = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

        var unzoned = new TimestampArray.Builder(new TimestampType(TimeUnit.Microsecond, (string?)null)).Append(when).Build();
        var value = Assert.IsType<DateTime>(ArrowValues.ValueAt(unzoned, 0));
        Assert.Equal(new DateTime(2026, 3, 4, 5, 6, 7), value);
        Assert.Equal(DateTimeKind.Unspecified, value.Kind);

        var zoned = new TimestampArray.Builder(new TimestampType(TimeUnit.Microsecond, "UTC")).Append(when).Build();
        Assert.Equal(when, Assert.IsType<DateTimeOffset>(ArrowValues.ValueAt(zoned, 0)));
    }

    [Fact]
    public void Dates_And_Times()
    {
        Assert.Equal(new DateTime(2026, 1, 2), ArrowValues.ValueAt(new Date32Array.Builder().Append(new DateTime(2026, 1, 2)).Build(), 0));

        var time = new Time64Array.Builder(new Time64Type(TimeUnit.Microsecond)).Append(3_600_000_000L).Build();
        Assert.Equal(TimeSpan.FromHours(1), ArrowValues.ValueAt(time, 0));
    }

    [Fact]
    public void A_Type_With_No_Scalar_Equivalent_Becomes_Text_Rather_Than_Failing_The_Read()
    {
        var list = new ListArray.Builder(Int32Type.Default);
        var values = (Int32Array.Builder)list.ValueBuilder;
        list.Append();
        values.Append(1);
        values.Append(2);
        Assert.Equal("[1, 2]", ArrowValues.ValueAt(list.Build(), 0));
    }
}

public sealed class ValueBindingTests
{
    private static FlightSqlParameter Bind(string nativeType, string raw) =>
        (FlightSqlParameter)FlightSqlValueBinding.Instance.CreateParameter("p", raw, new ColumnMetadata("c", nativeType, true, false, false));

    [Theory]
    [InlineData("bigint", "42", typeof(long))]
    [InlineData("integer", "42", typeof(int))]
    [InlineData("decimal(18,2)", "10.25", typeof(decimal))]
    [InlineData("double precision", "1.5", typeof(double))]
    [InlineData("boolean", "true", typeof(bool))]
    [InlineData("timestamp", "2026-01-02T03:04:05", typeof(DateTime))]
    [InlineData("timestamp with time zone", "2026-01-02T03:04:05+00:00", typeof(DateTimeOffset))]
    [InlineData("date", "2026-01-02", typeof(DateOnly))]
    [InlineData("varchar", "abc", typeof(string))]
    public void Types_The_Bound_To_The_Column(string nativeType, string raw, Type clr) =>
        Assert.IsType(clr, Bind(nativeType, raw).Value);

    [Fact]
    public void A_Bound_That_Does_Not_Fit_The_Column_Says_So() =>
        Assert.Contains("'c'", Assert.Throws<InvalidOperationException>(() => Bind("bigint", "not-a-number")).Message);

    [Fact]
    public void Prepared_Parameters_Become_Typed_Arrow_Values()
    {
        var batch = ArrowParameters.Build([1L, "x", 2.5m, null, new DateTime(2026, 1, 2), true, new byte[] { 9 }, Guid.Empty]);

        Assert.Equal(8, batch.ColumnCount);
        Assert.Equal(1, batch.Length);
        Assert.IsType<Int64Array>(batch.Column(0));
        Assert.IsType<StringArray>(batch.Column(1));
        var decimalType = Assert.IsType<Decimal128Type>(batch.Column(2).Data.DataType);
        Assert.Equal((38, 1), (decimalType.Precision, decimalType.Scale));
        Assert.IsType<NullArray>(batch.Column(3));
        Assert.IsType<TimestampArray>(batch.Column(4));
        Assert.IsType<BooleanArray>(batch.Column(5));
        Assert.IsType<BinaryArray>(batch.Column(6));
        Assert.IsType<StringArray>(batch.Column(7));
    }

    [Fact]
    public void A_Parameter_Type_Nothing_Can_Send_Says_How_To_Get_Around_It() =>
        Assert.Contains("Inline", Assert.Throws<NotSupportedException>(() => ArrowParameters.Build([new object()])).Message);
}

public sealed class ParameterCollectionTests
{
    [Fact]
    public void Finds_A_Parameter_By_Name_Whatever_Sigil_Either_Side_Uses()
    {
        var collection = new FlightSqlParameterCollection();
        collection.Add(new FlightSqlParameter { ParameterName = "@Min", Value = 1 });

        Assert.Equal(0, collection.IndexOf("min"));
        Assert.Equal(0, collection.IndexOf(":MIN"));
        Assert.Equal(-1, collection.IndexOf("max"));
    }

    [Fact]
    public void Parameters_Are_Input_Only() =>
        Assert.Throws<NotSupportedException>(() => new FlightSqlParameter().Direction = ParameterDirection.Output);
}
