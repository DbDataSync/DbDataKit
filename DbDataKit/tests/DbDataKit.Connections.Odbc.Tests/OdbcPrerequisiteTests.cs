using System.Data.Odbc;
using DbDataKit.Connections.Odbc;
using Xunit;

namespace DbDataKit.Connections.Odbc.Tests;

public sealed class OdbcPrerequisiteTests
{
    [Fact]
    public void A_missing_driver_manager_is_named_as_such_and_the_loader_text_is_not_repeated()
    {
        var manager = new DllNotFoundException("Unable to load shared library 'libodbc.so.2' at /secret/path");

        var text = OdbcPrerequisite.Describe(manager);

        Assert.Contains("ODBC driver manager is not installed", text);
        Assert.Contains("unixODBC", text);
        Assert.DoesNotContain("/secret/path", text);
    }

    [Fact]
    public void The_driver_manager_is_found_when_the_provider_wraps_it()
    {
        var manager = new DllNotFoundException("x");
        var text = OdbcPrerequisite.Describe(manager);

        Assert.Equal(text, OdbcPrerequisite.Describe(new TypeInitializationException("System.Data.Odbc.OdbcEnvironment", manager)));
        Assert.Equal(text, OdbcPrerequisite.Describe(new InvalidOperationException("outer", new TypeInitializationException("x", manager))));
    }

    [Fact]
    public void Anything_else_is_not_a_prerequisite() =>
        Assert.Null(OdbcPrerequisite.Describe(new InvalidOperationException("password=hunter2")));

    [Fact]
    public void A_data_source_that_is_not_there_is_named_without_the_login()
    {
        // the real driver manager, when the machine has one: a name that is neither a driver nor a DSN. Without one, the manager is what is reported.
        const string secret = "dbk_no_such_data_source_qq";
        string? text;
        try { using var c = new OdbcConnection($"DSN={secret};UID=reader;PWD=hunter2"); c.Open(); return; }
        catch (Exception ex) { text = OdbcPrerequisite.Describe(ex); }

        Assert.NotNull(text);
        Assert.True(text.Contains("ODBC driver manager is not installed") || text.Contains("ODBC driver or data source named in the login was not found"), text);
        Assert.DoesNotContain(secret, text);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("reader", text);
    }
}
