using DbDataKit.Secrets.Cli;
using DbDataKit.Secrets.Store;
using Xunit;

namespace DbDataKit.Secrets.Cli.Tests;

/// <summary>The commands over the real encrypted store (plan 13, V05), the way a product mounts them.</summary>
public class EndToEndTests : IDisposable
{
    private const string Name = "dbdata:proxy:datasource:erp:password";
    private const string Sentinel = "S3NT1NEL-end-to-end";
    private readonly string _root = Directory.CreateTempSubdirectory("dbdata-secret-cli-").FullName;

    public void Dispose()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                foreach (var entry in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories))
                    File.SetUnixFileMode(entry, Directory.Exists(entry) ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute : UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
    }

    private (SecretCli Cli, EncryptedFileSecretStore Store) Open(string folder)
    {
        var directory = Path.Combine(_root, folder);
        var keys = new KeyRing(Path.Combine(directory, "keys"));
        keys.CreateKey();
        var store = new EncryptedFileSecretStore(directory, keys);
        var chain = new SecretResolverChain([store]);
        return (new SecretCli(chain, store, store, () => [SecretRef.Parse("secret:" + Name)]), store);
    }

    private static string Run(SecretCli cli, FakeConsole console, params string[] args)
    {
        var exit = cli.Execute(args, console);
        Assert.True(exit == 0, $"{string.Join(' ', args)} exited {exit}: {console.All}");
        return console.All;
    }

    [Fact]
    public async Task Set_check_list_export_import_rotate_and_delete_work_over_the_real_store_and_no_output_has_the_value()
    {
        var (cli, store) = Open("a");
        var everything = new List<string>();

        Assert.Equal(1, cli.Execute(["check"], new FakeConsole()));                                         // not stored yet
        everything.Add(Run(cli, new FakeConsole { IsInputRedirected = true, Stdin = Sentinel + "\n" }, "set", Name));
        everything.Add(Run(cli, new FakeConsole(), "check"));
        var list = Run(cli, new FakeConsole(), "list");
        Assert.Contains(Name, list);
        Assert.Contains(" UTC", list);
        everything.Add(list);

        var file = Path.Combine(_root, "move.yaml");
        everything.Add(Run(cli, new FakeConsole { IsInputRedirected = true, Stdin = "a passphrase for moving\n" }, "export", file, "--passphrase-stdin"));
        Assert.DoesNotContain(Sentinel, File.ReadAllText(file));

        var (other, otherStore) = Open("b");
        everything.Add(Run(other, new FakeConsole { IsInputRedirected = true, Stdin = "a passphrase for moving\n" }, "import", file, "--passphrase-stdin"));
        everything.Add(Run(other, new FakeConsole(), "check"));
        using (var moved = await otherStore.TryResolveAsync(SecretRef.Parse("secret:" + Name))) Assert.Equal(Sentinel, moved!.RevealString());

        everything.Add(Run(cli, new FakeConsole(), "rotate-key"));
        using (var after = await store.TryResolveAsync(SecretRef.Parse("secret:" + Name))) Assert.Equal(Sentinel, after!.RevealString());

        everything.Add(Run(cli, new FakeConsole(), "delete", Name));
        Assert.Equal(1, cli.Execute(["check"], new FakeConsole()));                                          // gone again

        Assert.All(everything, text => Assert.DoesNotContain(Sentinel, text));
    }
}
