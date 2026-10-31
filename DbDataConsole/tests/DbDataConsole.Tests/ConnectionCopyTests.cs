using DbDataConsole.Server;
using DbDataKit.Connections;

namespace DbDataConsole.Tests;

/// <summary>What a copy of a connection file changes for another server (plan 13a, E05), and what it leaves exactly as it was.</summary>
public sealed class ConnectionCopyTests
{
    private static ConnectionDefinition Read(CopyPlan plan) => ConnectionDefinitionLoader.Load(plan.Text ?? throw new InvalidOperationException(plan.Problem), "copy.yaml").Require();

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
        var plan = ConnectionCopy.Plan(Base + "\n", ConnectionCopy.Proxy, ConnectionCopy.Proxy, "erp2");

        Assert.Null(plan.Problem);
        Assert.Equal(Base.Replace("name: erp   # the name", "name: erp2   # the name") + "\n", plan.Text);
        Assert.DoesNotContain(plan.Notes, n => n.Contains("left out"));
    }

    [Fact]
    public void A_managed_by_is_dropped_with_a_note_whatever_the_target()
    {
        var plan = ConnectionCopy.Plan(Base + "\nmanaged_by: build-9\n", ConnectionCopy.Proxy, ConnectionCopy.Proxy, "erp2");

        Assert.Null(Read(plan).ManagedBy);
        Assert.DoesNotContain("build-9", plan.Text);
        Assert.Contains(plan.Notes, n => n.Contains("managed_by (build-9)") && n.Contains("a Proxy"));
    }

    [Fact]
    public void Another_products_extension_goes_the_targets_own_stays_and_nothing_empty_is_left_behind()
    {
        var both = Base + "\nextensions:\n  sync:\n    testQuery: SELECT 1\n  proxy:\n    allow_writes: false\n  build:\n    x: 1\n";

        var toProxy = Read(ConnectionCopy.Plan(both, ConnectionCopy.Sync, ConnectionCopy.Proxy, "e"));
        Assert.Equal(["proxy"], toProxy.Extensions.Items.Keys);

        var toSync = ConnectionCopy.Plan(both, ConnectionCopy.Proxy, ConnectionCopy.Sync, "e");
        Assert.Equal(["sync"], Read(toSync).Extensions.Items.Keys);
        Assert.Contains(toSync.Notes, n => n.Contains("extensions.build, extensions.proxy"));

        var onlyForeign = ConnectionCopy.Plan(Base + "\nextensions:\n  sync:\n    testQuery: SELECT 1\n", ConnectionCopy.Sync, ConnectionCopy.Proxy, "e");
        Assert.DoesNotContain("extensions", onlyForeign.Text);
        Assert.Empty(Read(onlyForeign).Extensions.Items);
    }

    [Theory]
    [InlineData(ConnectionCopy.Proxy, ConnectionCopy.Sync, "env:PW", "secret:dbdata:sync:connection:e2")]               // Sync reads one reference only
    [InlineData(ConnectionCopy.Sync, ConnectionCopy.Sync, "secret:dbdata:sync:connection:erp", "secret:dbdata:sync:connection:e2")]
    [InlineData(ConnectionCopy.Sync, ConnectionCopy.Proxy, "secret:dbdata:sync:connection:erp", "secret:dbdata:proxy:connection:e2")]   // not Sync's store
    [InlineData(ConnectionCopy.Proxy, ConnectionCopy.Proxy, "env:PW", "env:PW")]                                                   // kept as written
    [InlineData(ConnectionCopy.Proxy, ConnectionCopy.Proxy, "secret:dbdata:proxy:connection:erp", "secret:dbdata:proxy:connection:erp")]
    public void The_password_reference_is_the_targets_to_hold_and_the_note_says_so(string from, string to, string reference, string expected)
    {
        var plan = ConnectionCopy.Plan(Base.Replace("env:PW", reference) + "\n", from, to, "e2");

        Assert.Equal(expected, Read(plan).Auth!.PasswordSecret);
        Assert.Equal([expected], plan.Secrets);
        Assert.Contains(plan.Notes, n => n.Contains("never copied") && n.Contains($"`{expected}`"));
    }

    [Fact]
    public void A_flow_style_file_and_the_read_and_write_logins_of_a_Proxy_are_edited_too()
    {
        var text = "schema: connection/1\nname: erp\nengine: sqlserver\nmanaged_by: c\nconnection_string: Server=s\nread_login: { mode: sql, user: r, password_secret: 'env:R' }\nwrite_login: { mode: sql, user: w, password_secret: 'env:W' }\nextensions: { proxy: { allow_writes: true }, sync: { testQuery: x } }\n";

        var plan = ConnectionCopy.Plan(text, ConnectionCopy.Proxy, ConnectionCopy.Proxy, "e2");

        var copy = Read(plan);
        Assert.Equal("e2", copy.Name);
        Assert.Equal(["proxy"], copy.Extensions.Items.Keys);
        Assert.Equal(["env:R", "env:W"], plan.Secrets.Order());   // kept; the logins of a Proxy are not Sync's
        Assert.Null(copy.ManagedBy);
    }

    [Fact]
    public void A_scripts_and_hooks_note_is_made_for_a_Sync_copy_that_keeps_them()
    {
        var text = Base.Replace("'env:PW'", "'secret:dbdata:sync:connection:erp'") + "\nextensions:\n  sync:\n    scripts:\n      before: { name: warm-up }\n";

        Assert.Contains(ConnectionCopy.Plan(text, ConnectionCopy.Sync, ConnectionCopy.Sync, "e2").Notes, n => n.Contains("scripts and hooks"));
        Assert.DoesNotContain(ConnectionCopy.Plan(text, ConnectionCopy.Sync, ConnectionCopy.Proxy, "e2").Notes, n => n.Contains("scripts and hooks"));
    }

    [Fact]
    public void A_source_that_is_not_a_connection_file_a_bad_name_and_an_unsupported_target_are_refused_with_a_reason()
    {
        Assert.Contains("line", ConnectionCopy.Plan("schema: connection/1\nname: a\nengine: sqlserver\nbogus: 1\nconnection_string: x\n", ConnectionCopy.Sync, ConnectionCopy.Proxy, "a").Problem);
        Assert.Contains("schema", ConnectionCopy.Plan("driverType: sqlserver\n", ConnectionCopy.Sync, ConnectionCopy.Proxy, "a").Problem);
        Assert.Contains("not a connection name", ConnectionCopy.Plan(Base, ConnectionCopy.Sync, ConnectionCopy.Proxy, "../x").Problem);
        Assert.Contains("not a connection name", ConnectionCopy.Plan(Base, ConnectionCopy.Sync, ConnectionCopy.Proxy, new string('a', 65)).Problem);
        Assert.Contains("dbdatabuild", ConnectionCopy.Plan(Base, ConnectionCopy.Sync, "dbdatabuild", "a").Problem);
        Assert.Null(ConnectionCopy.Plan(Base, ConnectionCopy.Sync, ConnectionCopy.Proxy, "a").Problem);
    }

    [Fact]
    public void No_secret_value_is_ever_in_the_copy_only_references()
    {
        var plan = ConnectionCopy.Plan(Base, ConnectionCopy.Proxy, ConnectionCopy.Sync, "e2");

        Assert.All(plan.Secrets, s => Assert.Matches("^(secret|env|file|keyring|dpapi):", s));
        Assert.DoesNotContain("password:", plan.Text, StringComparison.OrdinalIgnoreCase);
    }
}
