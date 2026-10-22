using DbDataKit.Secrets;

namespace DbDataKit.Connections.Tests;

public class LoginTests
{
    private static ConnectionDefinition Def(Action<ConnectionDefinition>? edit = null)
    {
        var d = new ConnectionDefinition { Name = "warehouse", Engine = "sqlserver", Address = new AddressSpec { Host = "h" } };
        edit?.Invoke(d);
        return d;
    }

    [Fact]
    public void Env_names_follow_the_convention()
    {
        Assert.Equal("DBDATA_BUILD_WAREHOUSE_READ", LoginResolver.EnvName("build", "warehouse", LoginRole.Read));
        Assert.Equal("DBDATA_SYNC_MY_DW_1_WRITE", LoginResolver.EnvName("sync", "my-dw.1", LoginRole.Write));
        Assert.Equal("DBDATA_PROXY_X", LoginResolver.EnvName("proxy", "x", LoginRole.Default));
    }

    [Fact]
    public async Task Read_login_comes_from_the_read_env_var()
    {
        var r = new LoginResolver(Env.Chain(new() { ["DBDATA_BUILD_WAREHOUSE_READ"] = "Server=h;User Id=ro;Password=ropw" }), "build");
        using var login = await r.ResolveAsync(Def(), LoginRole.Read);
        Assert.NotNull(login);
        Assert.Equal("Server=h;User Id=ro;Password=ropw", login!.ConnectionString!.RevealString());
        Assert.Equal("env:DBDATA_BUILD_WAREHOUSE_READ", login.Source);
        Assert.DoesNotContain("ropw", login.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_never_falls_back_to_the_write_env_var_or_write_login()
    {
        var r = new LoginResolver(Env.Chain(new() { ["DBDATA_BUILD_WAREHOUSE_WRITE"] = "Server=h;User Id=rw;Password=rwpw" }), "build");
        Assert.Null(await r.ResolveAsync(Def(), LoginRole.Read));

        var withWriteLogin = Def(d => d.WriteLogin = new LoginSpec { User = "rw", PasswordSecret = "env:RW" });
        var r2 = new LoginResolver(Env.Chain(new() { ["RW"] = "rwpw" }), "build");
        Assert.Null(await r2.ResolveAsync(withWriteLogin, LoginRole.Read));
    }

    [Fact]
    public async Task The_old_DBDATABUILD_names_are_not_read()
    {
        var r = new LoginResolver(Env.Chain(new() { ["DBDATA_BUILD_WAREHOUSE_READ"] = "new", ["DBDATABUILD_WAREHOUSE_READ"] = "old" }), "build");
        using var login = await r.ResolveAsync(Def(), LoginRole.Read);
        Assert.Equal("new", login!.ConnectionString!.RevealString());

        // and with only the old name set, nothing resolves
        var onlyOld = new LoginResolver(Env.Chain(new() { ["DBDATABUILD_WAREHOUSE_READ"] = "old" }), "build");
        Assert.Null(await onlyOld.ResolveAsync(Def(), LoginRole.Read));
    }

    [Fact]
    public async Task Write_on_a_readonly_connection_is_refused()
    {
        var r = new LoginResolver(Env.Chain(new() { ["DBDATA_BUILD_WAREHOUSE_WRITE"] = "x" }), "build");
        await Assert.ThrowsAsync<ConnectionPolicyException>(async () => await r.ResolveAsync(Def(d => d.Access = AccessMode.Readonly), LoginRole.Write));
    }

    [Fact]
    public async Task Explicit_logins_resolve_their_secrets()
    {
        var d = Def(x =>
        {
            x.Auth = new LoginSpec { User = "app", PasswordSecret = "env:APP_PW" };
            x.ReadLogin = new LoginSpec { User = "ro", PasswordSecret = "env:RO_PW" };
        });
        var r = new LoginResolver(Env.Chain(new() { ["APP_PW"] = "apppw", ["RO_PW"] = "ropw" }), "sync");
        using var read = await r.ResolveAsync(d, LoginRole.Read);
        using var dflt = await r.ResolveAsync(d, LoginRole.Default);
        using var write = await r.ResolveAsync(d, LoginRole.Write);
        Assert.Equal("ro", read!.UserName);
        Assert.Equal("ropw", read.Password!.RevealString());
        Assert.Equal("app", dflt!.UserName);
        Assert.Equal("app", write!.UserName); // write falls back to the default login, never the reverse
    }

    [Fact]
    public async Task A_missing_secret_is_an_error_not_a_silent_empty_password()
    {
        var d = Def(x => x.Auth = new LoginSpec { User = "app", PasswordSecret = "env:NOPE" });
        var r = new LoginResolver(Env.Chain([]), "sync");
        await Assert.ThrowsAsync<SecretNotFoundException>(async () => await r.ResolveAsync(d, LoginRole.Default));
    }

    [Fact]
    public async Task Whole_string_secret_on_the_definition_is_the_default_login()
    {
        var d = Def(x => { x.Address = null; x.ConnectionStringSecret = "env:WHOLE"; });
        var r = new LoginResolver(Env.Chain(new() { ["WHOLE"] = "Host=h" }), "sync");
        using var login = await r.ResolveAsync(d, LoginRole.Default);
        Assert.Equal("Host=h", login!.ConnectionString!.RevealString());
    }
}
