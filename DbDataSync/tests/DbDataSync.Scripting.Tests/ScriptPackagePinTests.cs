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

    [Fact]
    public void TheFirstResolution_WritesTheLockFile_BesideTheScript()
    {
        _feed.Add("Pinned.Pkg", "1.0.0", "original");
        Assert.False(File.Exists(LockPath));

        var compilation = Compiler("a").Compile(Script());

        Assert.True(compilation.Success, string.Join("; ", compilation.Diagnostics));
        var text = File.ReadAllText(LockPath);
        Assert.Contains("\"schema\": \"scripting-lock/1\"", text);
        Assert.Contains("pinned.pkg/1.0.0", text);
    }

    [Fact]
    public void AnotherMachine_WithTheSameBytes_Compiles_AndWritesNothingNew()
    {
        _feed.Add("Pinned.Pkg", "1.0.0", "original");
        Assert.True(Compiler("a").Compile(Script()).Success);
        var stamp = File.GetLastWriteTimeUtc(LockPath);
        Thread.Sleep(20);

        Assert.True(Compiler("b").Compile(Script()).Success);

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(LockPath));
    }

    [Fact]
    public void AFetchWhoseBytesDiffer_IsRefused_NamingThePackageTheVersionAndTheLockFile()
    {
        _feed.Add("Pinned.Pkg", "1.0.0", "original");
        Assert.True(Compiler("a").Compile(Script()).Success);
        var before = File.ReadAllText(LockPath);

        _feed.Add("Pinned.Pkg", "1.0.0", "replaced on the feed");                 // same id and version, other bytes, and a machine with an empty store fetches it
        var refused = Compiler("b").Compile(Script());

        Assert.False(refused.Success);
        var message = string.Join("; ", refused.Diagnostics);
        Assert.Contains("Pinned.Pkg 1.0.0", message);
        Assert.Contains("scripts/upper.lock", message);
        Assert.Equal(before, File.ReadAllText(LockPath));
    }

    [Fact]
    public void ADifferentVersion_IsANewPin_AndTheOldOneIsDropped()
    {
        _feed.Add("Pinned.Pkg", "1.0.0", "one");
        _feed.Add("Pinned.Pkg", "2.0.0", "two");
        Assert.True(Compiler("a").Compile(Script()).Success);

        Assert.True(Compiler("a").Compile(Script("#r \"nuget: Pinned.Pkg, 2.0.0\"")).Success);

        var text = File.ReadAllText(LockPath);
        Assert.Contains("pinned.pkg/2.0.0", text);
        Assert.DoesNotContain("pinned.pkg/1.0.0", text);
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
        Assert.False(File.Exists(LockPath));
    }
}
