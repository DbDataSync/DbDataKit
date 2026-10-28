using DbDataSync.Core.Config;
using DbDataSync.Scripting;
using DbDataSync.Scripting.Abstractions;
using DbDataSync.Core.Sql;

namespace DbDataSync.Scripting.Tests;

/// <summary>Compiling operator-written C# — the spine of the whole scripting feature.</summary>
public sealed class ScriptCompilerTests : IDisposable
{
    private readonly string _cacheRoot = Path.Combine(Path.GetTempPath(), $"ds-script-cache-{Guid.NewGuid():N}");
    private readonly ScriptCompiler _compiler;

    public ScriptCompilerTests() => _compiler = new ScriptCompiler(new ScriptCacheDirectory(_cacheRoot));

    public void Dispose()
    {
        try { Directory.Delete(_cacheRoot, recursive: true); } catch (IOException) { }
    }

    /// <summary>A script is its code and a last expression that is the slot's implementation; <paramref name="entryType"/> is the type that expression builds (empty: the script ends with nothing).</summary>
    private static ScriptDefinition Script(string code, string entryType = "Upper") => new()
    {
        Manifest = new ScriptConfig { Name = "upper", Kind = ScriptSlots.SqlColumnExpression },
        Code = entryType.Length == 0 ? code : code + $"\nnew {entryType}()\n",
    };

    private const string ValidCode = """
        using DbDataSync.Scripting.Abstractions;

        public sealed class Upper : ISqlColumnExpression
        {
            public string? RenderSql(SqlColumnExpressionContext context) =>
                $"UPPER({context.ColumnReference})";
        }
        """;

    [Fact]
    public void ValidCode_Compiles()
    {
        var compilation = _compiler.Compile(Script(ValidCode));

        Assert.True(compilation.Success, string.Join("; ", compilation.Diagnostics));
        Assert.NotNull(compilation.Hash);
    }

    [Fact]
    public void TheCompiledTypeCanBeConstructedAndCalled()
    {
        var expression = _compiler.Compile(Script(ValidCode)).CreateInstance<ISqlColumnExpression>("upper");

        var rendered = expression.RenderSql(new SqlColumnExpressionContext(
            "Region", "Region", null, "[Region]", new FakeDialect(), ScriptParameters.Empty));

        Assert.Equal("UPPER([Region])", rendered);
    }

    [Fact]
    public void BrokenCode_ReportsDiagnosticsWithALineNumber()
    {
        var compilation = _compiler.Compile(Script("""
            using DbDataSync.Scripting.Abstractions;

            public sealed class Upper : ISqlColumnExpression
            {
                public string? RenderSql(SqlColumnExpressionContext context) => nonsense;
            }
            """));

        Assert.False(compilation.Success);
        var diagnostic = Assert.Single(compilation.Diagnostics);
        // Line and column, because a message with neither sends the operator hunting.
        Assert.Equal(5, diagnostic.Line);
        Assert.True(diagnostic.Column > 0);
        Assert.Contains("nonsense", diagnostic.Message);
    }

    [Fact]
    public void AnAssemblyOutsideTheReferenceSet_FailsToCompile()
    {
        // The guardrail. Not a security boundary — reflection defeats it — but it turns "I will just
        // call an HTTP API from inside a transform" into a design conversation rather than a
        // production incident.
        var compilation = _compiler.Compile(Script("""
            using System.Net.Http;
            using DbDataSync.Scripting.Abstractions;

            public sealed class Upper : ISqlColumnExpression
            {
                public string? RenderSql(SqlColumnExpressionContext context)
                {
                    using var client = new HttpClient();
                    return null;
                }
            }
            """));

        Assert.False(compilation.Success);
        Assert.Contains(compilation.Diagnostics, d => d.Message.Contains("HttpClient") || d.Message.Contains("Net"));
    }

    [Fact]
    public void FileIo_IsRejectedByTheSyntaxGuardRatherThanTheReferenceSet()
    {
        // The reference set cannot do this one: System.IO.File lives in System.Private.CoreLib next to
        // string, so there is no reference set that admits one and not the other. The guard catches a
        // spelled-out qualified name, which is the shape the accident actually takes.
        var compilation = _compiler.Compile(Script("""
            using DbDataSync.Scripting.Abstractions;

            public sealed class Upper : ISqlColumnExpression
            {
                public string? RenderSql(SqlColumnExpressionContext context) =>
                    System.IO.File.ReadAllText("/etc/passwd");
            }
            """));

        Assert.False(compilation.Success);
        Assert.Contains("System.IO", Assert.Single(compilation.Diagnostics).Message);
    }

    [Fact]
    public void ABannedUsingDirective_IsRejectedWithItsLineNumber()
    {
        var compilation = _compiler.Compile(Script("""
            using System.IO;
            using DbDataSync.Scripting.Abstractions;

            public sealed class Upper : ISqlColumnExpression
            {
                public string? RenderSql(SqlColumnExpressionContext context) => null;
            }
            """));

        Assert.False(compilation.Success);
        var diagnostic = Assert.Single(compilation.Diagnostics);
        Assert.Equal(1, diagnostic.Line);
        Assert.Contains("does not read files", diagnostic.Message);
    }

    [Fact]
    public void AScriptOfTheOperatorsOwnNamedLikeABannedType_IsNotFalselyRejected()
    {
        // The guard matches namespaces, not identifiers. Someone's own `File` class is their business.
        var compilation = _compiler.Compile(Script("""
            using DbDataSync.Scripting.Abstractions;

            public sealed class File { public static string Name => "ok"; }

            public sealed class Upper : ISqlColumnExpression
            {
                public string? RenderSql(SqlColumnExpressionContext context) => File.Name;
            }
            """));

        Assert.True(compilation.Success, string.Join("; ", compilation.Diagnostics));
    }

    [Fact]
    public void AScriptThatEndsWithNoExpression_SaysHowToEndIt()
    {
        var compilation = _compiler.Compile(Script(ValidCode, entryType: ""));

        Assert.True(compilation.Success);          // it compiles: what is missing is the value, found when the instance is asked for
        var ex = Assert.Throws<ScriptExecutionException>(() => compilation.CreateInstance<ISqlColumnExpression>("upper"));
        Assert.Contains("ends with no value", ex.Message);
        Assert.Contains("new MyScript()", ex.Message);
        Assert.Contains("no semicolon", ex.Message);
    }

    [Fact]
    public void AScriptThatEndsWithTheWrongThing_NamesWhatItEndsWith()
    {
        var compilation = _compiler.Compile(Script("var n = 42;\nn", entryType: ""));

        var ex = Assert.Throws<ScriptExecutionException>(() => compilation.CreateInstance<ISqlColumnExpression>("upper"));
        Assert.Contains("Int32", ex.Message);
    }

    [Fact]
    public void AWrongContract_FailsWhenTheInstanceIsAskedFor()
    {
        var compilation = _compiler.Compile(Script("public sealed class Upper { }"));

        var ex = Assert.Throws<ScriptExecutionException>(
            () => compilation.CreateInstance<ISqlColumnExpression>("upper"));
        Assert.Contains(nameof(ISqlColumnExpression), ex.Message);
    }

    [Fact]
    public void EmptyCode_IsAFailureRatherThanAnEmptyAssembly()
    {
        Assert.False(_compiler.Compile(Script("")).Success);
    }

    [Fact]
    public void CompilingTheSameSourceTwice_ComesBackFromTheDiskCache()
    {
        // The cache is what makes this usable from the TaskRunner, which is a process per run and would
        // otherwise pay Roslyn's start-up cost on every fifteen-second pass.
        Assert.True(_compiler.Compile(Script(ValidCode)).Success);

        var cached = Directory.GetFiles(_cacheRoot, "*.dll", SearchOption.AllDirectories);
        Assert.Single(cached);

        var second = _compiler.Compile(Script(ValidCode));
        Assert.True(second.Success);
        Assert.Single(Directory.GetFiles(_cacheRoot, "*.dll", SearchOption.AllDirectories));
    }

    [Fact]
    public void TheContentHash_IsOfTheCodeAlone()
    {
        Assert.Equal(ScriptConfig.ComputeHash("a"), ScriptConfig.ComputeHash("a"));
        Assert.NotEqual(ScriptConfig.ComputeHash("a"), ScriptConfig.ComputeHash("b"));
    }

    [Fact]
    public void LoadBringsInAnotherScriptOfTheScriptsFolder_AndNothingOutsideIt()
    {
        var scripts = Path.Combine(_cacheRoot, "config", "scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "shared.csx"), "string Wrap(string s) => \"UPPER(\" + s + \")\";");
        File.WriteAllText(Path.Combine(_cacheRoot, "outside.csx"), "int Secret() => 1;");
        var compiler = new ScriptCompiler(new ScriptCacheDirectory(Path.Combine(_cacheRoot, "cache2")), scripts);
        const string Body = """
            using DbDataSync.Scripting.Abstractions;

            public sealed class Wrapped : ISqlColumnExpression
            {
                public string? RenderSql(SqlColumnExpressionContext context) => $"x";
            }
            """;

        var inside = compiler.Compile(new ScriptDefinition { Manifest = new ScriptConfig { Name = "uses-shared", Kind = ScriptSlots.SqlColumnExpression }, Code = "#load \"shared.csx\"\n" + Body + "\nWrap(\"c\") == \"UPPER(c)\" ? new Wrapped() : null\n" });
        Assert.True(inside.Success, string.Join("; ", inside.Diagnostics));
        Assert.NotNull(inside.CreateInstance<ISqlColumnExpression>("uses-shared"));

        var outside = compiler.Compile(new ScriptDefinition { Manifest = new ScriptConfig { Name = "escapes", Kind = ScriptSlots.SqlColumnExpression }, Code = "#load \"../../outside.csx\"\n" + Body + "\nnew Wrapped()\n" });
        Assert.False(outside.Success);
    }

    [Fact]
    public void AScriptMayAskForAPackage_ResolvedFromAFeed_AndUseItsTypes()
    {
        var packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } set ? set : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var folder = Path.Combine(packages, "npgsql");
        Assert.True(Directory.Exists(folder), "Npgsql is not in the machine's NuGet package folder: restore a project that uses it first");
        var version = Directory.EnumerateDirectories(folder).Select(Path.GetFileName).Where(v => v is not null && !v.Contains('-')).Order().Last();
        var compiler = new ScriptCompiler(new ScriptCacheDirectory(Path.Combine(_cacheRoot, "cache3")), packageSource: packages);
        const string Body = """
            using DbDataSync.Scripting.Abstractions;

            public sealed class UsesNpgsql : ISqlColumnExpression
            {
                public string? RenderSql(SqlColumnExpressionContext context) => typeof(Npgsql.NpgsqlConnection).Name;
            }
            """;

        var compilation = compiler.Compile(new ScriptDefinition
        {
            Manifest = new ScriptConfig { Name = "uses-npgsql", Kind = ScriptSlots.SqlColumnExpression },
            Code = $"#r \"nuget: Npgsql, {version}\"\n" + Body + "\nnew UsesNpgsql()\n",
        });

        Assert.True(compilation.Success, string.Join("; ", compilation.Diagnostics));
        var expression = compilation.CreateInstance<ISqlColumnExpression>("uses-npgsql");
        Assert.Equal("NpgsqlConnection", expression.RenderSql(new SqlColumnExpressionContext("c", "c", null, "[c]", new FakeDialect(), ScriptParameters.Empty)));
    }

    private sealed class FakeDialect : IScriptDialect
    {
        public string EngineName => "MsSql";
        public string QuoteIdentifier(string identifier) => $"[{identifier}]";
        public string ParameterReference(string name) => $"@{name}";
        public DbDataSync.Core.Sql.CanonicalType ToCanonicalType(string nativeType) => throw new NotSupportedException();
        public DbDataSync.Core.Sql.RenderedColumnType RenderColumnType(DbDataSync.Core.Sql.CanonicalType type) => throw new NotSupportedException();
    }

    /// <summary>
    /// A script must be able to read what its contract hands it. <c>ColumnMapping</c>,
    /// <c>SourceTableRef</c> and <c>TableRef</c> live in DbDataSync.Core, and until phase 41 that
    /// assembly was not in the reference set — so a lifecycle hook could implement the interface and
    /// not touch <c>context.Target</c>, which is exactly what phase 27's motivating example does.
    /// Nothing had run one, so nothing had found out.
    /// </summary>
    [Fact]
    public void AScriptCanReadTheConfigTypesItsContractHandsIt()
    {
        var result = _compiler.Compile(new ScriptDefinition
        {
            Manifest = new ScriptConfig
            {
                Name = "reads-its-context", Kind = ScriptSlots.LifecycleHook,
            },
            Code = """
                using System.Collections.Generic;
                using System.Linq;
                using DbDataSync.Drivers.Abstractions;
                using DbDataSync.Scripting.Abstractions;

                public sealed class ReadsItsContext : ILifecycleHook
                {
                    public IReadOnlyList<string> DeclarePoints(LifecycleHookContext c) => ["beforeStage"];

                    public IReadOnlyList<HookStatement> BuildStatements(string point, LifecycleHookContext c)
                    {
                        // Every config type a contract exposes: the mapping list, and both table refs.
                        var mapped = c.ColumnMappings.Select(m => m.TargetColumn).ToList();
                        var from = $"{c.Source.Schema}.{c.Source.Table}";
                        var to = $"{c.Target.Schema}.{c.Target.Table}";
                        return [new HookStatement($"-- {mapped.Count} column(s), {from} -> {to}", [])];
                    }
                }

                new ReadsItsContext()
                """,
        });

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
    }
}
