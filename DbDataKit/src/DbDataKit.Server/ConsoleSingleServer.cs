using System.Diagnostics;
using System.Text.RegularExpressions;
using DbDataKit.Hosting.Cli;
using DbDataKit.Hosting.Startup;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using DbDataKit.Web.Trust;

namespace DbDataKit.Server;

/// <summary>
/// The console's part in <c>setup single-server</c>, read from and written to the console's own folder as <c>setup invite</c> does: a join token per
/// server of the machine (with the admin ceiling, made by the account that runs the command and audited as <c>server.invite</c>), the join request that
/// carries it, the servers <c>instances.yaml</c> lists, and the first administrator's link from <c>FIRST-RUN.txt</c>.
/// Design: docs/install.md#setting-up-one-machine-or-inviting-another
/// </summary>
public sealed partial class ConsoleSingleServer(string directory, Func<string, string?>? environment = null) : ISingleServerConsole
{
    public string Directory { get; } = Path.GetFullPath(directory);

    public bool CanMakeTokens => File.Exists(Path.Combine(Directory, DbDataKit.Hosting.Certificates.DirectoryCertificate.PfxFileName));

    public string WriteJoinRequest(SetupMember member)
    {
        var product = ConsoleJoins.Products.FirstOrDefault(p => p.Value == member.Service.Program).Key
            ?? throw new InvalidOperationException($"{member.Service.Program} does not join the console.");
        if (!CanMakeTokens) throw new InvalidOperationException($"The console in {Directory} has no certificate yet: it makes one at its first start.");
        var listen = ListenPorts.ReadPinned(ConsoleState.SettingsFileIn(Directory))
                     ?? throw new InvalidOperationException($"The console in {Directory} has not written its address to {ConsoleState.SettingsFileIn(Directory)} yet.");
        string fingerprint;
        try { fingerprint = PairingProof.CertificateFingerprint(ConsoleHost.Certificate(Directory)); }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException)
        {
            throw new InvalidOperationException($"The console's certificate in {Directory} cannot be read: {ex.Message}", ex);
        }

        // the server is on this machine: it reaches the console at the address the console listens on
        var address = ServerJoin.AddressFor(listen);
        var state = ConsoleState.Open(Directory, ConsoleState.Resolve(Directory, environment).Settings, environment);
        var account = Environment.UserName;
        var minted = new ConsoleJoins(state).Mint(new JoinScope(product, ServerRole.Admin), account, $"{account} (setup single-server)", actorKind: "command");
        GiveBackToFolderOwner(Directory);
        var path = ServerJoin.RequestPath(member.DataFolder);
        JoinRequestFile.From(address.GetLeftPart(UriPartial.Authority), fingerprint, minted.Token).Write(path);
        return path;
    }

    public bool IsJoined(string certificateFingerprint) =>
        new InstanceRegistry(Path.Combine(Directory, "instances.yaml")).All()
            .Any(i => string.Equals(Thumbprints.Parse(i.CertificateFingerprint) ?? i.CertificateFingerprint, certificateFingerprint, StringComparison.OrdinalIgnoreCase));

    public string FirstAdministrator()
    {
        var file = Path.Combine(Directory, BootstrapInvite.FileName);
        try
        {
            var state = ConsoleState.Open(Directory, ConsoleState.Resolve(Directory, environment).Settings, environment);
            if (new IdentityUserStore(state).Any())
                return "The console has a user already, so it made no setup link. To let someone in: dbdatakit user invite --email <address>.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return $"The console's users could not be read ({ex.Message}); the first administrator's link, if there is one, is in {file}.";
        }
        var link = File.Exists(file) ? SetupLinkIn().Match(File.ReadAllText(file)) : Match.Empty;
        return link.Success
            ? $"Open this link once to create the first administrator. It works once and ends after its lifetime (it is also in {file}): {link.Value}{Environment.NewLine}" +
              "If it ended or was lost, make a new one on this machine with: dbdatakit setup link"
            : $"The console wrote no setup link to {file}. Make one on this machine with: dbdatakit setup link";
    }

    [GeneratedRegex(@"https://\S+/invite#\S+")]
    private static partial Regex SetupLinkIn();

    // Opening the console's SQLite state as root can make its journal files (state.db-wal, state.db-shm) root's, which the console's own user then
    // cannot write: what this command made in the console's folder goes back to the folder's owner
    private static void GiveBackToFolderOwner(string folder)
    {
        if (OperatingSystem.IsWindows() || !Environment.IsPrivilegedProcess) return;
        foreach (var file in System.IO.Directory.EnumerateFiles(folder, "state.db*"))
        {
            try
            {
                using var chown = Process.Start(new ProcessStartInfo("chown", ["--reference=" + folder, file]) { UseShellExecute = false, RedirectStandardError = true });
                chown?.WaitForExit(10_000);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { /* the console's start says which file it cannot open */ }
        }
    }
}
