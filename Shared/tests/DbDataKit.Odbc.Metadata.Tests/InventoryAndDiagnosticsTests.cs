namespace DbDataKit.Odbc.Metadata.Tests;

internal sealed class FakeRegistry : IOdbcRegistryReader
{
    public Dictionary<(OdbcRegistryHive, OdbcBitness, string), Dictionary<string, string>> Keys = [];

    public FakeRegistry Add(OdbcRegistryHive hive, OdbcBitness view, string key, params (string Name, string Value)[] values)
    {
        Keys[(hive, view, key)] = values.ToDictionary(v => v.Name, v => v.Value);
        return this;
    }

    public IReadOnlyList<string> GetValueNames(OdbcRegistryHive hive, OdbcBitness view, string subKey) =>
        Keys.TryGetValue((hive, view, subKey), out var values) ? values.Keys.ToList() : [];

    public string? GetValue(OdbcRegistryHive hive, OdbcBitness view, string subKey, string name) =>
        Keys.TryGetValue((hive, view, subKey), out var values) && values.TryGetValue(name, out string? v) ? v : null;
}

internal sealed class FakeFiles(Dictionary<string, string> files, Dictionary<string, string>? environment = null) : IOdbcFileReader
{
    public string? ReadAllText(string path) => files.GetValueOrDefault(path);
    public string? GetEnvironmentVariable(string name) => environment?.GetValueOrDefault(name);
}

public class OdbcDriverInventoryTests
{
    private const string Drivers = @"SOFTWARE\ODBC\ODBCINST.INI\ODBC Drivers";
    private const string Sources = @"SOFTWARE\ODBC\ODBC.INI\ODBC Data Sources";
    private static string DriverKey(string name) => @"SOFTWARE\ODBC\ODBCINST.INI\" + name;

    private static FakeRegistry Machine() => new FakeRegistry()
        // 64-bit view: SQL Server 18 and a system DSN
        .Add(OdbcRegistryHive.LocalMachine, OdbcBitness.X64, Drivers, ("ODBC Driver 18 for SQL Server", "Installed"), ("Gone", "Uninstalled"))
        .Add(OdbcRegistryHive.LocalMachine, OdbcBitness.X64, DriverKey("ODBC Driver 18 for SQL Server"), ("Driver", @"C:\Windows\System32\msodbcsql18.dll"))
        .Add(OdbcRegistryHive.LocalMachine, OdbcBitness.X64, Sources, ("prod", "ODBC Driver 18 for SQL Server"))
        // 32-bit view: the Microsoft text driver (32-bit only) and a user DSN on it
        .Add(OdbcRegistryHive.LocalMachine, OdbcBitness.X86, Drivers, ("Microsoft Text Driver (*.txt; *.csv)", "Installed"))
        .Add(OdbcRegistryHive.LocalMachine, OdbcBitness.X86, DriverKey("Microsoft Text Driver (*.txt; *.csv)"), ("Driver", @"C:\Windows\SysWOW64\odtext32.dll"))
        .Add(OdbcRegistryHive.CurrentUser, OdbcBitness.X86, Sources, ("legacy", "Microsoft Text Driver (*.txt; *.csv)"));

    [Fact]
    public void Windows_drivers_and_dsns_are_read_from_both_registry_views_with_their_bitness()
    {
        var inventory = OdbcDriverInventory.FromWindowsRegistry(Machine());

        Assert.Equal(
            [("ODBC Driver 18 for SQL Server", OdbcBitness.X64), ("Microsoft Text Driver (*.txt; *.csv)", OdbcBitness.X86)],
            inventory.Drivers.Select(d => (d.Name, d.Bitness)));
        Assert.Equal(@"C:\Windows\SysWOW64\odtext32.dll", inventory.Drivers[1].Library);
        Assert.DoesNotContain(inventory.Drivers, d => d.Name == "Gone");     // only "Installed" entries count

        Assert.Equal([("prod", OdbcDsnScope.System, OdbcBitness.X64), ("legacy", OdbcDsnScope.User, OdbcBitness.X86)],
            inventory.Dsns.Select(d => (d.Name, d.Scope, d.Bitness)));
    }

    [Fact]
    public void A_driver_installed_only_as_32_bit_cannot_be_loaded_by_a_64_bit_process()
    {
        var inventory = OdbcDriverInventory.FromWindowsRegistry(Machine());

        Assert.False(inventory.CanLoadDriver("Microsoft Text Driver (*.txt; *.csv)", OdbcBitness.X64));
        Assert.True(inventory.CanLoadDriver("Microsoft Text Driver (*.txt; *.csv)", OdbcBitness.X86));
        Assert.True(inventory.CanLoadDriver("odbc driver 18 for sql server", OdbcBitness.X64));
        Assert.False(inventory.CanLoadDriver("No Such Driver", OdbcBitness.X64));
        Assert.Equal([OdbcBitness.X86], inventory.DriverBitnesses("Microsoft Text Driver (*.txt; *.csv)"));
        Assert.Equal([OdbcBitness.X86], inventory.DsnBitnesses("LEGACY"));
    }

    [Fact]
    public void A_driver_in_both_views_is_listed_for_both()
    {
        var registry = new FakeRegistry();
        foreach (var view in new[] { OdbcBitness.X64, OdbcBitness.X86 })
            registry.Add(OdbcRegistryHive.LocalMachine, view, Drivers, ("SQL Server", "Installed"));

        var inventory = OdbcDriverInventory.FromWindowsRegistry(registry);
        Assert.Equal([OdbcBitness.X64, OdbcBitness.X86], inventory.DriverBitnesses("SQL Server"));
    }

    [Fact]
    public void On_32_bit_windows_only_the_one_view_is_read()
    {
        var inventory = OdbcDriverInventory.FromWindowsRegistry(Machine(), is64BitOperatingSystem: false);
        Assert.Equal(["Microsoft Text Driver (*.txt; *.csv)"], inventory.Drivers.Select(d => d.Name));
    }

    private const string OdbcInst = """
        [ODBC Driver 18 for SQL Server]
        Description=Microsoft ODBC Driver 18 for SQL Server
        Driver=/opt/microsoft/msodbcsql18/lib64/libmsodbcsql-18.3.so.2.1
        UsageCount=1

        ; a comment
        [Legacy 32]
        Driver = /usr/lib32/odbc/libold.so

        [SQLite3]
        Driver=/usr/lib/libsqlite3odbc.so

        [ODBC]
        Pooling=No
        """;

    [Fact]
    public void Unix_drivers_come_from_odbcinst_ini_with_bitness_from_the_library_path_when_it_says()
    {
        var files = new FakeFiles(new() { ["/etc/odbcinst.ini"] = OdbcInst });
        var inventory = OdbcDriverInventory.FromUnixFiles(files);

        Assert.Equal(
            [("ODBC Driver 18 for SQL Server", OdbcBitness.X64), ("Legacy 32", OdbcBitness.X86), ("SQLite3", OdbcBitness.Unknown)],
            inventory.Drivers.Select(d => (d.Name, d.Bitness)));
        Assert.True(inventory.CanLoadDriver("SQLite3", OdbcBitness.X64));     // unknown bitness is not held against it
        Assert.False(inventory.CanLoadDriver("Legacy 32", OdbcBitness.X64));
    }

    [Fact]
    public void Unix_dsns_come_from_system_and_user_odbc_ini_and_inherit_the_drivers_bitness()
    {
        var files = new FakeFiles(
            new()
            {
                ["/etc/odbcinst.ini"] = OdbcInst,
                ["/etc/odbc.ini"] = "[ODBC Data Sources]\nx=y\n[prod]\nDriver=ODBC Driver 18 for SQL Server\nServer=db\n",
                ["/home/u/.odbc.ini"] = "[mine]\nDriver=Legacy 32\n",
            },
            new() { ["HOME"] = "/home/u" });
        var inventory = OdbcDriverInventory.FromUnixFiles(files);

        Assert.Equal([("prod", OdbcDsnScope.System, OdbcBitness.X64), ("mine", OdbcDsnScope.User, OdbcBitness.X86)],
            inventory.Dsns.Select(d => (d.Name, d.Scope, d.Bitness)));
    }

    [Fact]
    public void ODBCSYSINI_replaces_the_default_search_path()
    {
        var files = new FakeFiles(
            new() { ["/etc/odbcinst.ini"] = "[Wrong]\nDriver=/x\n", ["/opt/odbc/odbcinst.ini"] = "[Right]\nDriver=/y\n" },
            new() { ["ODBCSYSINI"] = "/opt/odbc/" });

        Assert.Equal(["Right"], OdbcDriverInventory.FromUnixFiles(files).Drivers.Select(d => d.Name));
    }

    [Fact]
    public void Missing_files_give_an_empty_inventory() =>
        Assert.Empty(OdbcDriverInventory.FromUnixFiles(new FakeFiles([])).Drivers);

    [Fact]
    public void Reading_this_machine_does_not_throw()
    {
        var inventory = OdbcDriverInventory.Read();
        Assert.NotNull(inventory.Drivers);
        Assert.NotNull(inventory.Dsns);
    }
}

public class OdbcDiagnosticsTests
{
    private static OdbcInventory Inventory() => new(
        [new("ODBC Driver 18 for SQL Server", null, OdbcBitness.X64), new("Microsoft Text Driver (*.txt; *.csv)", null, OdbcBitness.X86)],
        [new("prod", "ODBC Driver 18 for SQL Server", OdbcDsnScope.System, OdbcBitness.X64), new("legacy", "x", OdbcDsnScope.User, OdbcBitness.X86)]);

    [Theory]
    [InlineData("IM002")]
    [InlineData("IM014")]
    public void IM002_and_IM014_explain_the_word_size_rule_even_without_context(string state)
    {
        var diagnosis = OdbcDiagnostics.Explain(state, null, null, OdbcBitness.X64);
        Assert.Equal(state, diagnosis.SqlState);
        Assert.Contains("64-bit", diagnosis.ToString());
        Assert.Contains("32-bit", diagnosis.ToString());
    }

    [Fact]
    public void A_driver_installed_only_as_32_bit_is_named_as_such_for_a_64_bit_process()
    {
        var diagnosis = OdbcDiagnostics.Explain("IM014", "Driver={Microsoft Text Driver (*.txt; *.csv)};DefaultDir=c:\\data;PWD=secret", Inventory(), OdbcBitness.X64);

        Assert.Contains("installed for 32-bit only", diagnosis.Advice);
        Assert.Contains("Microsoft Text Driver", diagnosis.Advice);
        Assert.DoesNotContain("secret", diagnosis.ToString());     // never repeats other values
    }

    [Fact]
    public void A_dsn_defined_only_for_the_other_bitness_is_called_out()
    {
        var diagnosis = OdbcDiagnostics.Explain("IM002", "legacy", Inventory(), OdbcBitness.X64);
        Assert.Contains("DSN 'legacy' is defined for 32-bit only", diagnosis.Advice);

        var fine = OdbcDiagnostics.Explain("IM002", "legacy", Inventory(), OdbcBitness.X86);
        Assert.DoesNotContain("defined for", fine.Advice);
    }

    [Fact]
    public void A_missing_driver_or_dsn_lists_what_is_installed()
    {
        var driver = OdbcDiagnostics.Explain("IM002", "Driver={Nope}", Inventory(), OdbcBitness.X64);
        Assert.Contains("not installed under that name", driver.Advice);
        Assert.Contains("ODBC Driver 18 for SQL Server", driver.Advice);

        var dsn = OdbcDiagnostics.Explain("IM002", "DSN=nope", Inventory(), OdbcBitness.X64);
        Assert.Contains("DSN 'nope' is not defined", dsn.Advice);
    }

    [Fact]
    public void The_exception_overload_uses_its_sqlstate_and_other_states_have_plain_text()
    {
        Assert.Equal("IM014", OdbcDiagnostics.Explain(new OdbcCatalogException("SQLDriverConnect", "IM014")).SqlState);
        Assert.Contains("login", OdbcDiagnostics.Explain("28000").Summary);
        Assert.Contains("timeout", OdbcDiagnostics.Explain("HYT00").Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ZZZZZ", OdbcDiagnostics.Explain("zzzzz").Summary);
    }
}
