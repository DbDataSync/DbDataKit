using DbDataKit.Hosting.WindowsService;
using DbDataGateway.Server.Hosting;

namespace DbDataGateway.Tests.Hosting;

/// <summary>
/// The rename DbDataProxy to DbDataGateway is a clean break (plan 20, O14): every leftover of the old name stops the program with a message that names the old name, the new one and the file or variable found.
/// Nothing is read under the old name, and a clean installation is not bothered.
/// </summary>
public sealed class RenamedFromTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ddg-renamed-" + Guid.NewGuid().ToString("N")[..8]);
    private string Data => Path.Combine(root, "common", "DbDataGateway");
    private string Common => Path.Combine(root, "common");

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    private IReadOnlyList<string> Problems(string[]? args = null) =>
        RenamedFrom.Problems(args ?? [], Data, Common);

    private void Write(string relative, string text)
    {
        var path = Path.Combine(Data, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void A_clean_installation_has_nothing_to_report()
    {
        Write("config/server.yaml", "schemaVersion: 2\n");
        Write("config/connections/erp.yaml", "schema: connection/1\nextensions:\n  gateway:\n    access: readonly\n");
        Assert.Empty(Problems(["server", "run", "--Gateway:Listen:Port=9000"]));
    }

    [Fact]
    public void An_old_command_line_setting_is_refused()
    {
        var problem = Assert.Single(Problems(["server", "run", "--Proxy:Listen:Port=9000"]));
        Assert.Contains("--Proxy:Listen:Port=9000", problem);
        Assert.Contains("--Gateway:Listen:Port=9000", problem);
    }

    [Fact]
    public void The_old_data_directory_is_named_when_the_new_one_has_no_settings()
    {
        Directory.CreateDirectory(Path.Combine(Common, "DbDataProxy", "config"));
        var problem = Assert.Single(Problems());
        Assert.Contains(Path.Combine(Common, "DbDataProxy"), problem);
        Assert.Contains(Data, problem);
        Write("config/server.yaml", "schemaVersion: 2\n");
        Assert.Empty(Problems());
    }

    [Fact]
    public void The_old_authentication_folder_a_reference_to_an_old_secret_and_an_old_extension_key_are_refused_with_their_file()
    {
        Write("config/server.yaml", "schemaVersion: 2\n");
        Write("config/proxy/auth.yaml", "x: 1\n");
        Write("config/connections/erp.yaml", "schema: connection/1\nconnection_string_secret: 'secret:dbdata:proxy:datasource:erp'\nextensions:\n  proxy:\n    access: readonly\n");
        var all = string.Join("\n", Problems());
        Assert.Contains(Path.Combine(Data, "config", "proxy"), all);
        Assert.Contains(Path.Combine(Data, "config", "gateway"), all);
        Assert.Contains("erp.yaml", all);
        Assert.Contains("dbdata:proxy:", all);
        Assert.Contains("dbdata:gateway:", all);
        Assert.Contains("extensions: proxy:", all);
        Assert.Contains("extensions: gateway:", all);
    }

    [Fact]
    public void Secrets_stored_under_the_old_names_are_refused()
    {
        Write("config/server.yaml", "schemaVersion: 2\n");
        Write("secrets/secrets.yaml", "secrets:\n  dbdata:proxy:datasource:erp:password: abc\n");
        var problem = Assert.Single(Problems());
        Assert.Contains("secrets.yaml", problem);
        Assert.Contains("dbdata:gateway:", problem);
    }

    [Fact]
    public void A_service_registered_under_the_old_name_stops_install()
    {
        var scm = new InMemoryServiceControlManager();
        Assert.Null(RenamedFrom.OldService(scm));
        scm.Create(new ServiceInstallSpec { Name = "dbdataproxy", BinaryPath = @"C:\old\dbdataproxy.exe" }, null);
        var message = RenamedFrom.OldService(scm);
        Assert.NotNull(message);
        Assert.Contains("dbdataproxy", message);
        Assert.Contains("dbdatagateway", message);
    }
}
