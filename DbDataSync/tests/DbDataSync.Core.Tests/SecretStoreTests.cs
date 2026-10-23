using DbDataKit.Secrets;
using DbDataSync.Core.Secrets;
using LibGit2Sharp;

namespace DbDataSync.Core.Tests;

/// <summary>
/// Sync's secrets are in the shared encrypted store under the repository root, named <c>dbdata:sync:...</c> (plan 13, A04): they
/// persist across instances, are not in the file in the clear, are kept out of git, and the old <c>dbdatasync:...</c> names are not read.
/// </summary>
public sealed class SecretStoreTests : IDisposable
{
    private const string Sentinel = "s3ntinel-P@ssw0rd-do-not-leak";

    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-secrets-").FullName;

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    private SecretStore Open() => SecretStore.Open(_root, new Dictionary<string, string?>().GetValueOrDefault);

    [Fact]
    public void AStoredSecret_IsReadBackByAnotherInstanceOnTheSameRoot()
    {
        Open().Store(SecretRefs.ForConnection("erp"), Sentinel);

        Assert.True(Open().TryResolve(SecretRefs.ForConnection("erp"), out var value));
        Assert.Equal(Sentinel, value);
    }

    [Fact]
    public void TheStoreFile_DoesNotHoldTheValueInTheClear()
    {
        var store = Open();
        store.Store(SecretRefs.ForConnection("erp"), Sentinel);

        var file = Path.Combine(_root, "secrets", "secrets.yaml");
        Assert.True(File.Exists(file));
        Assert.DoesNotContain(Sentinel, File.ReadAllText(file));
    }

    [Fact]
    public void DeletingASecret_RemovesIt()
    {
        var store = Open();
        store.Store(SecretRefs.ForConnection("erp"), Sentinel);

        store.Delete(SecretRefs.ForConnection("erp"));

        Assert.False(Open().TryResolve(SecretRefs.ForConnection("erp"), out _));
    }

    [Fact]
    public void ARepositoryRoot_GetsTheSecretsDirectoryIgnoredOnce()
    {
        Repository.Init(_root);

        Open().Store(SecretRefs.ForConnection("erp"), Sentinel);
        Open().Store(SecretRefs.ForConnection("crm"), Sentinel);

        var lines = File.ReadAllLines(Path.Combine(_root, ".gitignore"));
        Assert.Single(lines, "secrets/");
    }

    [Fact]
    public void ANameInTheOldForm_IsNotAName_AndNothingIsStoredOrRead()
    {
        var store = Open();

        var ex = Assert.Throws<FormatException>(() => store.Store("dbdatasync:connection:erp", Sentinel));

        Assert.Contains("dbdata:sync:", ex.Message);
        Assert.DoesNotContain(Sentinel, ex.Message);
        Assert.Throws<FormatException>(() => store.TryResolve("dbdatasync:connection:erp", out _));
    }

    [Fact]
    public void TheEnvironment_AnswersWhatTheStoreDoesNotHave()
    {
        var store = SecretStore.Open(_root, name => name == SecretStoreName("erp") ? "from-env" : null);

        Assert.Equal("from-env", store.Resolve(SecretRefs.ForConnection("erp")));
        Assert.StartsWith("DBDATA_SECRET_DBDATA_SYNC_CONNECTION_ERP", store.EnvName(SecretRefs.ForConnection("erp")));
    }

    [Fact]
    public void AMissingSecret_IsReportedByNameAlone_AndNoFailureMessageHoldsAValue()
    {
        var store = Open();
        store.Store(SecretRefs.ForConnection("erp"), Sentinel);

        var missing = Assert.Throws<SecretNotFoundException>(() => store.Resolve(SecretRefs.ForConnection("crm")));
        Assert.Contains("dbdata:sync:connection:crm", missing.Message);
        Assert.DoesNotContain(Sentinel, missing.ToString());

        var invalid = Assert.Throws<FormatException>(() => store.Resolve("not a name"));
        Assert.DoesNotContain(Sentinel, invalid.ToString());
    }

    private static string SecretStoreName(string connection) =>
        new SecretStore([], null).EnvName(SecretRefs.ForConnection(connection));
}
