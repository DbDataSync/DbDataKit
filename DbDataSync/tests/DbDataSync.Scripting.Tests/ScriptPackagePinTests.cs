using DbDataSync.Core.Config;
using DbDataSync.Scripting;
using DbDataSync.Scripting.Abstractions;
using DbDataSync.Tests.Shared;

namespace DbDataSync.Scripting.Tests;

/// <summary>
/// S12: the hashes of the packages a <c>.csx</c> script names are pinned in <c>scripts/&lt;name&gt;.lock</c>, committed beside the script. Run against a local folder feed: no network.
/// </summary>
public sealed class ScriptPackagePinTests : IDisposable
{
    private readonly LocalPackageFeed _feed = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ds-script-pin-" + Guid.NewGuid().ToString("N")[..10]);
    private string Scripts => Path.Combine(_root, "scripts");
    private string LockPath => Path.Combine(Scripts, "upper.lock");

    public ScriptPackagePinTests() => Directory.CreateDirectory(Scripts);

    public void Dispose()
    {
        _feed.Dispose();
        try { Directory.Delete(_root, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private const string Body = """
        using DbDataSync.Scripting.Abstractions;

        public sealed class Upper : ISqlColumnExpression
        {
            public string? RenderSql(SqlColumnExpressionContext context) => $"UPPER({context.ColumnReference})";
        }
        """;

    private static ScriptDefinition Script(string packageLine = "#r \"nuget: Pinned.Pkg, 1.0.0\"") => new()
    {
        Manifest = new ScriptConfig { Name = "upper", Kind = ScriptSlots.SqlColumnExpression },
        Code = packageLine + "\n" + Body + "\nnew Upper()\n",
    };

    /// <summary>A compiler as a machine has it: its own package store (<paramref name="machine"/>) over the repository's scripts folder.</summary>
    private ScriptCompiler Compiler(string machine) => new(new ScriptCacheDirectory(Path.Combine(_root, machine)), Scripts, _feed.Directory);

    /// <summary>What saving the script does with the compilation's lock text (<c>ConfigRepository.SaveScript</c>), without a repository.</summary>
    private void Pin(ScriptCompilation compilation)
    {
        Assert.True(compilation.Success, string.Join("; ", compilation.Diagnostics));
        File.WriteAllText(LockPath, compilation.PackageLock);
    }

    [Fact]
    public void TheFirstResolution_ReturnsTheLockText_AndWritesNoFile()
    {
        _feed.Add("Pinned.Pkg", "1.0.0", "original");
        Assert.False(File.Exists(LockPath));

        var compilation = Compiler("a").Compile(Script());

        Assert.True(compilation.Success, string.Join("; ", compilation.Diagnostics));
        Assert.Contains("\"schema\": \"scripting-lock/1\"", compilation.PackageLock);
        Assert.Contains("pinned.pkg/1.0.0", compilation.PackageLock);
        Assert.Empty(Directory.GetFiles(Scripts));
    }

    [Fact]
    public void ValidatingAScript_WithoutSavingIt_LeavesNoLockFile_AndSavingItWritesAndCommitsOne()
    {
        _feed.Add("Pinned.Pkg", "1.0.0", "original");
        var repoRoot = Path.Combine(_root, "repo");
        Directory.CreateDirectory(repoRoot);
        LibGit2Sharp.Repository.Init(repoRoot);
        var repository = new ConfigRepository(Path.Combine(repoRoot, "config"), new DbDataKit.Config.Git.GitCommitService(repoRoot),
            DbDataSync.Core.Secrets.SecretStore.ForProviders(new DbDataSync.Core.Secrets.InMemorySecretProvider()));
        var compiler = new ScriptCompiler(new ScriptCacheDirectory(Path.Combine(_root, "cache")), repository.ScriptsDirectory, _feed.Directory);
        var repoLock = Path.Combine(repository.ScriptsDirectory, "upper.lock");
        var author = new DbDataKit.Config.Git.GitAuthor("Ada", "ada@example.test");

        var validated = compiler.Compile(Script());

        Assert.True(validated.Success, string.Join("; ", validated.Diagnostics));
        Assert.NotNull(validated.PackageLock);
        Assert.False(File.Exists(repoLock));
        using (var git = new LibGit2Sharp.Repository(repoRoot))
            Assert.Empty(git.RetrieveStatus(new LibGit2Sharp.StatusOptions()).Untracked);

        repository.SaveScript(Script(), author, validated.PackageLock);

        Assert.Equal(validated.PackageLock, File.ReadAllText(repoLock));
        using (var git = new LibGit2Sharp.Repository(repoRoot))
        {
            Assert.NotNull(git.Head.Tip["config/scripts/upper.lock"]);
            Assert.Empty(git.RetrieveStatus(new LibGit2Sharp.StatusOptions()).Untracked);
        }

        // validating again with the pin in place changes nothing, and saving what it returned leaves the file alone
        var stamp = File.GetLastWriteTimeUtc(repoLock);
        var again = compiler.Compile(Script());
        Assert.Equal(validated.PackageLock, again.PackageLock);
        repository.SaveScript(Script(), author, again.PackageLock);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(repoLock));
    }

    [Fact]
    public void AnotherMachine_WithTheSameBytes_Compiles_AndWantsTheSameLockText()
    {
        _feed.Add("Pinned.Pkg", "1.0.0", "original");
        var first = Compiler("a").Compile(Script());
        Pin(first);

        var second = Compiler("b").Compile(Script());

        Assert.True(second.Success, string.Join("; ", second.Diagnostics));
        Assert.Equal(first.PackageLock, second.PackageLock);
        Assert.Equal(first.PackageLock, File.ReadAllText(LockPath));
    }

    [Fact]
    public void AFetchWhoseBytesDiffer_IsRefused_NamingThePackageTheVersionAndTheLockFile()
    {
        _feed.Add("Pinned.Pkg", "1.0.0", "original");
        Pin(Compiler("a").Compile(Script()));
        var before = File.ReadAllText(LockPath);

        _feed.Add("Pinned.Pkg", "1.0.0", "replaced on the feed");                 // same id and version, other bytes, and a machine with an empty store fetches it
        var refused = Compiler("b").Compile(Script());

        Assert.False(refused.Success);
        var message = string.Join("; ", refused.Diagnostics);
        Assert.Contains("Pinned.Pkg 1.0.0", message);
        Assert.Contains("scripts/upper.lock", message);
        Assert.Null(refused.PackageLock);
        Assert.Equal(before, File.ReadAllText(LockPath));
    }

    [Fact]
    public void ADifferentVersion_IsANewPin_AndTheOldOneIsDropped()
    {
        _feed.Add("Pinned.Pkg", "1.0.0", "one");
        _feed.Add("Pinned.Pkg", "2.0.0", "two");
        Pin(Compiler("a").Compile(Script()));

        var second = Compiler("a").Compile(Script("#r \"nuget: Pinned.Pkg, 2.0.0\""));

        Assert.True(second.Success, string.Join("; ", second.Diagnostics));
        Assert.Contains("pinned.pkg/2.0.0", second.PackageLock);
        Assert.DoesNotContain("pinned.pkg/1.0.0", second.PackageLock);
    }

    [Fact]
    public void ALockFileWithAnUnknownKey_FailsTheScript_NamingTheFileAndTheKey()
    {
        _feed.Add("Pinned.Pkg", "1.0.0", "original");
        File.WriteAllText(LockPath, """{"schema":"scripting-lock/1","hashes":{}}""");

        var compilation = Compiler("a").Compile(Script());

        Assert.False(compilation.Success);
        var message = string.Join("; ", compilation.Diagnostics);
        Assert.Contains("scripts/upper.lock", message);
        Assert.Contains("$.hashes", message);
    }

    [Fact]
    public void AScriptWithNoPackages_WritesNoLockFile()
    {
        var compilation = Compiler("a").Compile(new ScriptDefinition { Manifest = new ScriptConfig { Name = "upper", Kind = ScriptSlots.SqlColumnExpression }, Code = Body + "\nnew Upper()\n" });

        Assert.True(compilation.Success, string.Join("; ", compilation.Diagnostics));
        Assert.Null(compilation.PackageLock);
        Assert.False(File.Exists(LockPath));
    }
}
