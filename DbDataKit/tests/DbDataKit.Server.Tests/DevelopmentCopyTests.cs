using DbDataKit.Connections;
using DbDataKit.Server;

namespace DbDataKit.Server.Tests;

/// <summary>The copy of a draft's connection that Set up for development writes (<see cref="DevelopmentCopy"/>): the draft's file renamed, its logins replaced by the typed user and the stored secret, checked by the strict loader.</summary>
public sealed class DevelopmentCopyTests
{
    private const string Secret = "dbdatakit:runner:dev-login:sales-dw.dev.warehouse:password";

    [Fact]
    public void A_build_connection_keeps_its_engine_and_extensions_and_gets_one_login_and_a_warning_that_it_names_no_address()
    {
        const string draft = "# Connections are files\nschema: connection/1\nname: warehouse\nengine: sqlserver\nextensions:\n  build:\n    version: 16\nread_login:\n  user: reader\n  password_secret: secret:x:y\n";

        var copy = DevelopmentCopy.Write(draft, "connections/warehouse.yaml", "sales-dw-warehouse-dev", "ana_dev", Secret);

        Assert.Null(copy.Error);
        var loaded = ConnectionDefinitionLoader.LoadConnectionOfAnyKind(copy.Text!).Require();
        Assert.Equal("sales-dw-warehouse-dev", loaded.Name);
        Assert.Equal("ana_dev", loaded.Auth!.User);
        Assert.Equal("secret:" + Secret, loaded.Auth.PasswordSecret);
        Assert.Null(loaded.ReadLogin);
        Assert.Contains("version: 16", copy.Text);
        Assert.Contains("# Connections are files", copy.Text);
        Assert.Contains(copy.Warnings, w => w.Contains("names no address", StringComparison.Ordinal));
    }

    [Fact]
    public void A_user_with_a_quote_is_written_as_typed()
    {
        var copy = DevelopmentCopy.Write("schema: connection/1\nname: crm\nengine: postgres\nconnection_string: 'Host=h;Database=d'\n", "connections/crm.yaml", "p-crm-dev", "o'brien", Secret);

        Assert.Null(copy.Error);
        Assert.Equal("o'brien", ConnectionDefinitionLoader.LoadConnectionOfAnyKind(copy.Text!).Require().Auth!.User);
        Assert.Empty(copy.Warnings);
    }

    [Fact]
    public void No_user_typed_and_none_in_the_draft_is_refused_with_400()
    {
        var copy = DevelopmentCopy.Write("schema: connection/1\nname: crm\nengine: postgres\nconnection_string: 'Host=h'\n", "connections/crm.yaml", "p-crm-dev", null, Secret);

        Assert.Equal(400, copy.Status);
        Assert.Contains("Type the login's user", copy.Error);
    }

    [Theory]
    [InlineData("crm-to-dw", "crm", "dev", "crm-to-dw-crm-dev")]
    [InlineData("2024-sales", "crm", "dev", "c-2024-sales-crm-dev")]
    public void The_copy_is_named_by_package_slot_and_channel(string package, string slot, string channel, string expected) =>
        Assert.Equal(expected, DevelopmentCopy.NameOf(package, slot, channel));

    [Fact]
    public void A_name_longer_than_a_runner_takes_ends_with_a_hash_of_the_whole()
    {
        var a = DevelopmentCopy.NameOf(new string('a', 60), "crm", "dev");
        var b = DevelopmentCopy.NameOf(new string('a', 60), "crm", "test");

        Assert.Equal(64, a.Length);
        Assert.NotEqual(a, b);
        Assert.True(DbDataKit.Runner.Api.RunnerConnectionService.NameRule(a));
    }
}
