namespace DbData.Odbc.Metadata.Tests;

public class ConnectionStringBuildTests
{
    public static TheoryData<string> Hostile =>
    [
        "x};Driver={evil};UID={",
        "x;Driver={evil}",
        "}}};FILEDSN=c:\\x.dsn;",
        "a=b",
        "{leading brace",
        " padded ",
        "semi;colon",
        "",
        "plain",
    ];

    [Theory]
    [MemberData(nameof(Hostile))]
    public void A_quoted_value_round_trips_as_exactly_one_value(string value)
    {
        string text = OdbcConnectionString.Build(("Driver", "SQL Server"), ("PWD", value), ("Server", "db"));

        Assert.True(OdbcConnectionString.TryParseStrict(text, out var pairs, out string? error), error);
        Assert.Equal([("driver", "SQL Server"), ("pwd", value), ("server", "db")], pairs);
        Assert.Equal(value, OdbcConnectionString.Get(text, "pwd"));   // the lenient reader agrees
    }

    [Fact]
    public void Plain_values_are_left_alone_and_others_are_braced()
    {
        Assert.Equal("abc", OdbcConnectionString.Quote("abc"));
        Assert.Equal("{a;b}", OdbcConnectionString.Quote("a;b"));
        Assert.Equal("{a}}b}", OdbcConnectionString.Quote("a}b"));
        Assert.Equal("{x}};Driver={evil}};UID={}", OdbcConnectionString.Quote("x};Driver={evil};UID={"));
    }

    [Theory]
    [InlineData("a\0b")]
    [InlineData("a\nb")]
    public void Control_characters_are_refused_because_a_nul_ends_the_string_in_the_driver_manager(string value) =>
        Assert.Throws<ArgumentException>(() => OdbcConnectionString.Quote(value));

    [Theory]
    [InlineData("")]
    [InlineData("a;b")]
    [InlineData("a=b")]
    [InlineData("{a")]
    [InlineData(" a")]
    [InlineData("a\0")]
    public void Keys_must_be_plain(string key) =>
        Assert.Throws<ArgumentException>(() => OdbcConnectionString.Build((key, "v")));

    [Fact]
    public void Naive_concatenation_of_the_same_value_is_what_the_policy_catches()
    {
        string hostile = "x};Driver={evil};UID={";
        var policy = new OdbcConnectionPolicy { AllowedDrivers = new HashSet<string> { "SQL Server" } };

        Assert.False(policy.TryValidate("Driver={SQL Server};PWD={" + hostile + "}", out string? reason));   // unescaped: closes the brace early
        Assert.NotNull(reason);
        Assert.True(policy.TryValidate(OdbcConnectionString.Build(("Driver", "SQL Server"), ("PWD", hostile)), out _));
    }

    [Fact]
    public void Strict_parsing_rejects_what_the_driver_manager_might_read_differently()
    {
        string[] bad =
        [
            "Driver={a}b;Server=x",          // text after a braced value
            "Driver={a;Server=x",            // unclosed brace
            "Driver=a{b;Server=x",           // brace inside an unbraced value
            "Server=x;junk;UID=u",           // a part that is not key=value
            "=value",                        // empty key
            "Server=a\0b",                   // NUL
        ];
        foreach (string text in bad)
            Assert.False(OdbcConnectionString.TryParseStrict(text, out _, out string? error), text);

        Assert.True(OdbcConnectionString.TryParseStrict("Driver = {SQL Server} ; Server = x ;;", out var pairs, out _));
        Assert.Equal([("driver", "SQL Server"), ("server", "x")], pairs);
    }
}

public class OdbcConnectionPolicyTests
{
    private static HashSet<string> Set(params string[] values) => new(values);

    [Theory]
    [InlineData("Driver={SQL Server};FILEDSN=c:\\evil.dsn")]
    [InlineData("filedsn=c:\\evil.dsn")]
    [InlineData("DSN=x; FileDSN = c:\\evil.dsn")]
    public void File_dsn_is_forbidden_by_default(string text)
    {
        var ex = Assert.Throws<OdbcPolicyViolationException>(() => OdbcConnectionPolicy.Default.Validate(text));
        Assert.Contains("filedsn", ex.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_file_is_forbidden_by_default_and_both_can_be_switched_off()
    {
        Assert.Throws<OdbcPolicyViolationException>(() => OdbcConnectionPolicy.Default.Validate("Driver={x};SAVEFILE=c:\\out.dsn"));
        var relaxed = new OdbcConnectionPolicy { ForbidFileDsn = false, ForbidSaveFile = false };
        relaxed.Validate("Driver={x};SAVEFILE=c:\\out.dsn;FILEDSN=c:\\in.dsn");
    }

    [Fact]
    public void Default_allows_an_ordinary_string_and_a_bare_dsn()
    {
        OdbcConnectionPolicy.Default.Validate("Driver={ODBC Driver 18 for SQL Server};Server=tcp:db,1433;Database=x;UID=u;PWD={p;w}");
        OdbcConnectionPolicy.Default.Validate("mydsn");
    }

    [Fact]
    public void Only_listed_drivers_are_allowed_by_name_ignoring_case()
    {
        var policy = new OdbcConnectionPolicy { AllowedDrivers = Set("ODBC Driver 18 for SQL Server") };

        policy.Validate("driver={odbc driver 18 for sql server};Server=x");
        Assert.Throws<OdbcPolicyViolationException>(() => policy.Validate("Driver={Other};Server=x"));
        Assert.Throws<OdbcPolicyViolationException>(() => policy.Validate("Driver=/tmp/evil.so;Server=x"));
        Assert.Throws<OdbcPolicyViolationException>(() => policy.Validate("Server=x"));   // names neither
        Assert.Throws<OdbcPolicyViolationException>(() => policy.Validate("mydsn"));      // a DSN could resolve to any driver
    }

    [Fact]
    public void A_driver_list_and_a_dsn_list_are_each_enforced()
    {
        var policy = new OdbcConnectionPolicy { AllowedDrivers = Set("A"), AllowedDsns = Set("prod") };

        policy.Validate("Driver={A}");
        policy.Validate("DSN=PROD");
        Assert.Throws<OdbcPolicyViolationException>(() => policy.Validate("DSN=dev"));
        Assert.Throws<OdbcPolicyViolationException>(() => policy.Validate("Driver={B}"));
    }

    [Theory]
    [InlineData("DSN=prod;Driver={evil}")]            // DSN smuggling: a good DSN with a driver beside it
    [InlineData("Driver={evil};DSN=prod")]
    [InlineData("DSN=prod;DSN=evil")]                 // duplicates: which one wins depends on the driver manager
    [InlineData("Driver={A};Driver={evil}")]
    [InlineData("prod;Driver={evil}")]                // a "bare DSN name" with a tail
    [InlineData("DSN=prod;FILEDSN=c:\\x.dsn")]
    [InlineData("DSN={prod}x")]
    public void Dsn_and_driver_smuggling_is_rejected(string text)
    {
        var policy = new OdbcConnectionPolicy { AllowedDrivers = Set("A"), AllowedDsns = Set("prod") };
        Assert.False(policy.TryValidate(text, out string? reason));
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public void Only_dsns_allowed_rejects_driver_strings_and_the_reverse()
    {
        var dsnOnly = new OdbcConnectionPolicy { AllowedDsns = Set("prod") };
        Assert.Throws<OdbcPolicyViolationException>(() => dsnOnly.Validate("Driver={A}"));
        dsnOnly.Validate("DSN=prod;UID=u");

        var driverOnly = new OdbcConnectionPolicy { AllowedDrivers = Set("A") };
        Assert.Throws<OdbcPolicyViolationException>(() => driverOnly.Validate("DSN=prod"));
    }

    [Fact]
    public void Extra_forbidden_keys_are_matched_ignoring_case_and_every_occurrence_counts()
    {
        var policy = new OdbcConnectionPolicy { ForbidKeys = Set("Trusted_Connection", "Authentication") };

        policy.Validate("Driver={x};UID=u");
        Assert.Throws<OdbcPolicyViolationException>(() => policy.Validate("Driver={x};trusted_connection=yes"));
        Assert.Throws<OdbcPolicyViolationException>(() => policy.Validate("Driver={x};UID=u;Authentication=ActiveDirectoryIntegrated"));
    }

    [Fact]
    public void A_violation_never_repeats_a_password()
    {
        var policy = new OdbcConnectionPolicy { ForbidKeys = Set("uid") };
        var ex = Assert.Throws<OdbcPolicyViolationException>(() => policy.Validate("Driver={x};UID=bob;PWD=hunter2"));
        Assert.DoesNotContain("hunter2", ex.Message);
        Assert.DoesNotContain("hunter2", ex.Reason);
    }

    [Fact]
    public void Control_characters_and_unparsable_text_are_rejected_even_by_the_default()
    {
        Assert.Throws<OdbcPolicyViolationException>(() => OdbcConnectionPolicy.Default.Validate("Driver={x};PWD=a\0b"));
        Assert.Throws<OdbcPolicyViolationException>(() => OdbcConnectionPolicy.Default.Validate("Driver={x;Server=y"));
        OdbcConnectionPolicy.Unrestricted.Validate("Driver={x;Server=y");   // opt-out for administrator-written strings
    }

    [Fact]
    public void Connect_refuses_before_any_driver_is_touched() =>
        Assert.Throws<OdbcPolicyViolationException>(() => OdbcCatalog.Connect("Driver={x};FILEDSN=c:\\evil.dsn"));

    [Fact]
    public async Task The_native_layer_enforces_the_policy_before_SQLDriverConnect()
    {
        var api = new FakeOdbcApi();
        await using var functions = new OdbcNativeFunctions("DSN=a;Driver={evil}", new OdbcCatalogOptions { Policy = new OdbcConnectionPolicy { AllowedDrivers = Set("A"), AllowedDsns = Set("a") } }, api);

        await Assert.ThrowsAsync<OdbcPolicyViolationException>(() => functions.TablesAsync(null, null, null, null, CancellationToken.None));
        Assert.Equal(0, api.Connects);
    }
}
