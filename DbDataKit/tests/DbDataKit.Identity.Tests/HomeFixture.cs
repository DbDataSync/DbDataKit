using DbDataKit.State;

namespace DbDataKit.Identity.Tests;

/// <summary>A home on a throwaway SQLite state database, with the host's key protection stood in for by a reversible mask.</summary>
internal sealed class HomeFixture : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ddk-identity-" + Guid.NewGuid().ToString("N"));

    public StateDatabase Database { get; }
    public UserStore Users { get; }
    public HomeDirectory Directory { get; }
    public OidcClients Clients { get; }
    public SigningKeys Keys { get; }

    public HomeFixture()
    {
        System.IO.Directory.CreateDirectory(_directory);
        Database = new StateDatabase(Path.Combine(_directory, "state.db"), HomeSchema.Schema);
        Users = new UserStore(Database);
        Directory = new HomeDirectory(Database);
        Clients = new OidcClients(Database);
        Keys = new SigningKeys(Database, new MaskProtector());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { System.IO.Directory.Delete(_directory, true); } catch (IOException) { /* the file is still released by the runtime */ }
    }

    private sealed class MaskProtector : ISigningKeyProtector
    {
        public byte[] Protect(byte[] privateKey) => privateKey.Select(b => (byte)(b ^ 0x5a)).ToArray();
        public byte[] Unprotect(byte[] protectedKey) => Protect(protectedKey);
    }
}
