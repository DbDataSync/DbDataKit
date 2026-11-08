namespace DbDataKit.Runner.Limits.Tests;

/// <summary>Plan 28c QE12: the ODBC drivers of a machine as read-only capabilities, <c>odbc:&lt;id&gt;</c>.</summary>
public sealed class OdbcCapabilitiesTests
{
    [Theory]
    [InlineData("ODBC Driver 18 for SQL Server", "odbc:sqlserver18")]
    [InlineData("odbc driver 17 for sql server", "odbc:sqlserver17")]
    [InlineData("PostgreSQL Unicode", "odbc:postgresql_unicode")]
    [InlineData("  MySQL ODBC 8.0 Unicode Driver ", "odbc:mysql_odbc_8_0_unicode_driver")]
    public void A_driver_name_becomes_a_label_id(string driver, string expected) => Assert.Equal(expected, OdbcCapabilities.NameFor(driver));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    public void A_name_with_no_letter_or_digit_is_no_label(string driver) => Assert.Null(OdbcCapabilities.NameFor(driver));

    [Fact]
    public void The_text_of_odbcinst_and_of_an_ini_file_lists_the_drivers_and_not_the_managers_own_section()
    {
        var odbcinst = "[ODBC Driver 18 for SQL Server]\n[PostgreSQL Unicode]\n";
        Assert.Equal(["ODBC Driver 18 for SQL Server", "PostgreSQL Unicode"], OdbcCapabilities.ParseDriverList(odbcinst));

        var ini = "[ODBC Driver 18 for SQL Server]\r\nDescription=Microsoft ODBC Driver 18 for SQL Server\r\nDriver=/opt/microsoft/msodbcsql18/lib64/libmsodbcsql-18.so\r\nUsageCount=1\r\n\r\n[ODBC]\r\nTrace=No\r\n[odbc driver 18 for sql server]\r\n";
        Assert.Equal(["ODBC Driver 18 for SQL Server"], OdbcCapabilities.ParseDriverList(ini));
        Assert.Empty(OdbcCapabilities.ParseDriverList(""));
    }

    [Fact]
    public void The_capabilities_carry_the_drivers_own_name_and_keep_the_first_of_two_names_with_one_id()
    {
        var capabilities = OdbcCapabilities.From(["ODBC Driver 18 for SQL Server", "PostgreSQL Unicode", "PostgreSQL-Unicode", ""]);

        Assert.Equal(2, capabilities.Count);
        Assert.Equal("ODBC Driver 18 for SQL Server", capabilities["odbc:sqlserver18"]);
        Assert.Equal("PostgreSQL Unicode", capabilities["odbc:postgresql_unicode"]);
    }

    [Fact]
    public void Detect_on_this_machine_reports_only_odbc_capabilities_and_the_runner_report_includes_them()
    {
        var detected = OdbcCapabilities.Detect();
        Assert.All(detected.Keys, k => Assert.StartsWith(OdbcCapabilities.Prefix, k));

        var all = LimitCapabilities.Detect();
        foreach (var (name, driver) in detected) Assert.Equal(driver, all[name]);
        Assert.Contains("os", all.Keys);
    }
}
