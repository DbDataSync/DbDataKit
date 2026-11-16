using DbDataKit.Server;
using DbDataKit.Connections;

namespace DbDataKit.Server.Tests;

/// <summary>What a copy of a connection file changes for another server (plan 13a, E05), and what it leaves exactly as it was.</summary>
public sealed class ConnectionCopyTests
{
    private static ConnectionDefinition Read(CopyPlan plan) => ConnectionDefinitionLoader.LoadDatabaseConnection(plan.Text ?? throw new InvalidOperationException(plan.Problem), "copy.yaml").Require();

    private const string Base = """
        # kept
        schema: connection/1
        name: erp   # the name
        engine: sqlserver
        address:
          host: sql01
          port: 1433
          database: Erp
        auth:
          mode: sql
          user: u
          password_secret: 'env:PW'
        """;

    [Fact]
    public void Only_the_name_changes_and_the_sources_comments_and_layout_stay()
    {
        var plan = ConnectionCopy.Plan(Base + "\n", ConnectionCopy.Gateway, ConnectionCopy.Gateway, "erp2");

        Assert.Null(plan.Problem);
        Assert.Equal(Base.Replace("name: erp   # the name", "name: erp2   # the name") + "\n", plan.Text);
        Assert.DoesNotContain(plan.Notes, n => n.Contains("left out"));
    }

    [Fact]
    public void A_managed_by_is_dropped_with_a_note()
    {
        var plan = ConnectionCopy.Plan(Base + "\nmanaged_by: build-9\n", ConnectionCopy.Gateway, ConnectionCopy.Gateway, "erp2");

        Assert.Null(Read(plan).ManagedBy);
        Assert.DoesNotContain("build-9", plan.Text);
        Assert.Contains(plan.Notes, n => n.Contains("managed_by (build-9)") && n.Contains("a Gateway"));
    }

    [Fact]
    public void Another_products_extension_goes_the_targets_own_stays_and_nothing_empty_is_left_behind()
    {
        var both = Base + "\nextensions:\n  sync:\n    test_query: SELECT 1\n  gateway:\n    allow_writes: false\n  build:\n    x: 1\n";

        var toGateway = ConnectionCopy.Plan(both, ConnectionCopy.Gateway, ConnectionCopy.Gateway, "e");
        Assert.Equal(["gateway"], Read(toGateway).Extensions.Items.Keys);
        Assert.Contains(toGateway.Notes, n => n.Contains("extensions.build, extensions.sync"));

        var onlyForeign = ConnectionCopy.Plan(Base + "\nextensions:\n  sync:\n    test_query: SELECT 1\n", ConnectionCopy.Gateway, ConnectionCopy.Gateway, "e");
        Assert.DoesNotContain("extensions", onlyForeign.Text);
        Assert.Empty(Read(onlyForeign).Extensions.Items);
    }

    [Theory]
    [InlineData("env:PW")]
    [InlineData("secret:dbdatakit:gateway:connection:erp")]
    [InlineData("secret:dbdatakit:sync:connection:erp")]   // a reference is kept as written, whatever store it names
    public void The_password_reference_is_kept_as_written_and_the_note_says_the_target_must_hold_it(string reference)
    {
        var plan = ConnectionCopy.Plan(Base.Replace("env:PW", reference) + "\n", ConnectionCopy.Gateway, ConnectionCopy.Gateway, "e2");

        Assert.Equal(reference, Read(plan).Auth!.PasswordSecret);
        Assert.Equal([reference], plan.Secrets);
        Assert.Contains(plan.Notes, n => n.Contains("never copied") && n.Contains($"`{reference}`"));
    }

    [Fact]
    public void A_flow_style_file_with_read_and_write_logins_and_a_secret_connection_string_is_copied()
    {
        var text = "schema: connection/1\nname: erp\nengine: sqlserver\nmanaged_by: c\nconnection_string: Server=s\nread_login: { mode: sql, user: r, password_secret: 'env:R' }\nwrite_login: { mode: sql, user: w, password_secret: 'env:W' }\nextensions: { gateway: { allow_writes: true }, sync: { test_query: x } }\n";

        var plan = ConnectionCopy.Plan(text, ConnectionCopy.Gateway, ConnectionCopy.Gateway, "e2");

        var copy = Read(plan);
        Assert.Equal("e2", copy.Name);
        Assert.Equal(["gateway"], copy.Extensions.Items.Keys);
        Assert.Equal(["env:R", "env:W"], plan.Secrets.Order());
        Assert.Null(copy.ManagedBy);
        Assert.Null(ConnectionCopy.Plan("schema: connection/1\nname: erp\nengine: sqlserver\nconnection_string_secret: 'env:WHOLE'\n", ConnectionCopy.Gateway, ConnectionCopy.Gateway, "e2").Problem);
    }

    [Fact]
    public void A_source_that_is_not_a_connection_file_a_bad_name_and_an_unsupported_target_are_refused_with_a_reason()
    {
        Assert.Contains("line", ConnectionCopy.Plan("schema: connection/1\nname: a\nengine: sqlserver\nbogus: 1\nconnection_string: x\n", ConnectionCopy.Gateway, ConnectionCopy.Gateway, "a").Problem);
        Assert.Contains("schema", ConnectionCopy.Plan("driverType: sqlserver\n", ConnectionCopy.Gateway, ConnectionCopy.Gateway, "a").Problem);
        Assert.Contains("not a connection name", ConnectionCopy.Plan(Base, ConnectionCopy.Gateway, ConnectionCopy.Gateway, "../x").Problem);
        Assert.Contains("not a connection name", ConnectionCopy.Plan(Base, ConnectionCopy.Gateway, ConnectionCopy.Gateway, new string('a', 65)).Problem);
        Assert.Contains("dbdatasync", ConnectionCopy.Plan(Base, ConnectionCopy.Gateway, "dbdatasync", "a").Problem);   // the deleted Sync server keeps none
        Assert.Contains("dbdatarunner", ConnectionCopy.Plan(Base, ConnectionCopy.Gateway, "dbdatarunner", "a").Problem);
        Assert.Null(ConnectionCopy.Plan(Base, ConnectionCopy.Gateway, ConnectionCopy.Gateway, "a").Problem);
    }

    [Fact]
    public void No_secret_value_is_ever_in_the_copy_only_references()
    {
        var plan = ConnectionCopy.Plan(Base, ConnectionCopy.Gateway, ConnectionCopy.Gateway, "e2");

        Assert.All(plan.Secrets, s => Assert.Matches("^(secret|env|file|keyring|dpapi):", s));
        Assert.DoesNotContain("password:", plan.Text, StringComparison.OrdinalIgnoreCase);
    }
}
