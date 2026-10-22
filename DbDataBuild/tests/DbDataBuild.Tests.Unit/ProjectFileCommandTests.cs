using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.State;
using Json.Schema;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>`project file list|read|write|move|delete` (plan 13, W01): confined to the project tree, guarded by content hashes, a JSON document with a closed schema for each.</summary>
public class ProjectFileCommandTests : IDisposable
{
    private static readonly JsonSchema Schema = SchemaConformanceTests.LoadSchema("output");
    private readonly string _dir = NewProjectDir();

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private (int Exit, JsonNode Doc) Run(string? stdin = null, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var exit = CliApp.Run([.. args, "--project", _dir, "--format", "json"], o, e, input: new StringReader(stdin ?? ""), environment: _ => null);
        var doc = JsonNode.Parse(o.ToString())!;
        Assert.True(Schema.Evaluate(JsonSerializer.SerializeToNode(doc), new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid, o.ToString());
        Assert.Equal(exit, doc["exit_code"]!.GetValue<int>());
        return (exit, doc);
    }

    private static string Code(JsonNode doc) => doc["diagnostics"]!.AsArray().First()!["code"]!.GetValue<string>();

    private string Put(string relative, string text)
    {
        var full = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return Hashing.ScriptHash(text);
    }

    [Fact]
    public void List_reports_every_file_with_its_size_and_hash_and_leaves_out_git_and_state()
    {
        var hash = Put("models/a.sql", "SELECT 1\n");
        Put(".git/config", "x");
        Put(".dbdatabuild/seed.duckdb", "x");
        var (exit, doc) = Run(null, "project", "file", "list");
        Assert.Equal(0, exit);
        var files = doc["data"]!["files"]!.AsArray();
        var a = Assert.Single(files, f => f!["path"]!.GetValue<string>() == "models/a.sql")!;
        Assert.Equal(hash, a["hash"]!.GetValue<string>());
        Assert.Equal(9, a["size"]!.GetValue<int>());
        Assert.DoesNotContain(files, f => f!["path"]!.GetValue<string>().StartsWith(".git/") || f["path"]!.GetValue<string>().StartsWith(".dbdatabuild/"));
    }

    [Fact]
    public void Read_returns_the_text_and_the_hash_with_line_endings_read_as_lf()
    {
        Put("models/a.sql", "SELECT 1\r\nFROM t\r\n");
        var (exit, doc) = Run(null, "project", "file", "read", "models/a.sql");
        Assert.Equal(0, exit);
        Assert.Equal("SELECT 1\nFROM t\n", doc["data"]!["content"]!.GetValue<string>());
        Assert.Equal(Hashing.ScriptHash("SELECT 1\nFROM t\n"), doc["data"]!["hash"]!.GetValue<string>());
    }

    [Fact]
    public void Write_with_the_current_hash_replaces_the_file_as_lf_without_a_bom_and_returns_the_new_hash()
    {
        var hash = Put("models/a.sql", "SELECT 1\n");
        var (exit, doc) = Run("SELECT 2\r\n", "project", "file", "write", "models/a.sql", "--expect-hash", hash);
        Assert.Equal(0, exit);
        var bytes = File.ReadAllBytes(Path.Combine(_dir, "models/a.sql"));
        Assert.Equal("SELECT 2\n", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal(Hashing.ScriptHash("SELECT 2\n"), doc["data"]!["hash"]!.GetValue<string>());
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "models"), "*.tmp"));
    }

    [Fact]
    public void Write_against_a_stale_hash_is_refused_and_changes_nothing()
    {
        var stale = Put("models/a.sql", "SELECT 1\n");
        File.WriteAllText(Path.Combine(_dir, "models/a.sql"), "SELECT 99\n");          // someone else edited it
        var (exit, doc) = Run("SELECT 2\n", "project", "file", "write", "models/a.sql", "--expect-hash", stale);
        Assert.Equal(1, exit);
        Assert.Equal("DDB-471", Code(doc));
        Assert.Equal("SELECT 99\n", File.ReadAllText(Path.Combine(_dir, "models/a.sql")));
    }

    [Fact]
    public void Write_needs_exactly_one_of_expect_hash_and_create()
    {
        Put("models/a.sql", "x\n");
        Assert.Equal(2, Run("y", "project", "file", "write", "models/a.sql").Exit);
        Assert.Equal(2, Run("y", "project", "file", "write", "models/a.sql", "--create", "--expect-hash", "abc").Exit);
    }

    [Fact]
    public void Create_makes_folders_and_refuses_a_file_that_exists()
    {
        var (exit, doc) = Run("SELECT 1\n", "project", "file", "write", "models/new/deep/b.sql", "--create");
        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(_dir, "models/new/deep/b.sql")));
        var (again, refused) = Run("SELECT 2\n", "project", "file", "write", "models/new/deep/b.sql", "--create");
        Assert.Equal(1, again);
        Assert.Equal("DDB-471", Code(refused));
        Assert.Equal("SELECT 1\n", File.ReadAllText(Path.Combine(_dir, "models/new/deep/b.sql")));
    }

    [Fact]
    public void Move_renames_with_the_current_hash_and_refuses_an_existing_destination_or_a_stale_hash()
    {
        var hash = Put("models/a.sql", "SELECT 1\n");
        Put("models/taken.sql", "other\n");
        Assert.Equal("DDB-472", Code(Run(null, "project", "file", "move", "models/a.sql", "models/taken.sql", "--expect-hash", hash).Doc));
        Assert.Equal("DDB-471", Code(Run(null, "project", "file", "move", "models/a.sql", "models/b.sql", "--expect-hash", new string('0', 64)).Doc));
        var (exit, doc) = Run(null, "project", "file", "move", "models/a.sql", "sub/b.sql", "--expect-hash", hash);
        Assert.Equal(0, exit);
        Assert.False(File.Exists(Path.Combine(_dir, "models/a.sql")));
        Assert.Equal("SELECT 1\n", File.ReadAllText(Path.Combine(_dir, "sub/b.sql")));
        Assert.Equal("models/a.sql", doc["data"]!["from"]!.GetValue<string>());
    }

    [Fact]
    public void Delete_needs_the_current_hash()
    {
        var hash = Put("models/a.sql", "SELECT 1\n");
        Assert.Equal("DDB-471", Code(Run(null, "project", "file", "delete", "models/a.sql", "--expect-hash", new string('0', 64)).Doc));
        Assert.True(File.Exists(Path.Combine(_dir, "models/a.sql")));
        Assert.Equal(0, Run(null, "project", "file", "delete", "models/a.sql", "--expect-hash", hash).Exit);
        Assert.False(File.Exists(Path.Combine(_dir, "models/a.sql")));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("models/../../outside.txt")]
    [InlineData("a/b/../../../outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("..\\outside.txt")]
    [InlineData(".git/config")]
    [InlineData("models/.GIT/config")]
    [InlineData(".git")]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("models/a\0.sql")]
    public void A_path_that_leaves_the_project_or_names_git_is_refused_by_every_command(string path)
    {
        var hash = new string('0', 64);
        var outside = Path.Combine(Path.GetDirectoryName(_dir)!, "outside.txt");
        foreach (var args in new[]
        {
            new[] { "project", "file", "read", path },
            new[] { "project", "file", "write", path, "--create" },
            new[] { "project", "file", "write", path, "--expect-hash", hash },
            new[] { "project", "file", "move", path, "models/x.sql", "--expect-hash", hash },
            new[] { "project", "file", "move", "models/x.sql", path, "--expect-hash", hash },
            new[] { "project", "file", "delete", path, "--expect-hash", hash },
        })
        {
            var (exit, doc) = Run("pwned\n", args);
            Assert.Equal(1, exit);
            Assert.Equal("DDB-470", Code(doc));
        }
        Assert.False(File.Exists(outside));
    }

    [Fact]
    public void A_link_that_points_out_of_the_project_is_refused_for_reading_and_for_writing_through_it()
    {
        var elsewhere = Directory.CreateTempSubdirectory("ddb-outside-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(elsewhere, "secret.txt"), "secret\n");
            try { Directory.CreateSymbolicLink(Path.Combine(_dir, "link"), elsewhere); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return; }     // Windows without the right to make links
            Assert.Equal("DDB-470", Code(Run(null, "project", "file", "read", "link/secret.txt").Doc));
            Assert.Equal("DDB-470", Code(Run("x\n", "project", "file", "write", "link/new.txt", "--create").Doc));
            Assert.False(File.Exists(Path.Combine(elsewhere, "new.txt")));
            Assert.DoesNotContain(Run(null, "project", "file", "list").Doc["data"]!["files"]!.AsArray(), f => f!["path"]!.GetValue<string>().StartsWith("link/"));
        }
        finally { Directory.Delete(elsewhere, true); }
    }

    [Fact]
    public void A_missing_binary_or_large_file_is_unusable()
    {
        File.WriteAllBytes(Path.Combine(_dir, "bin.dat"), [0xFF, 0xFE, 0x00, 0x80]);
        Assert.Equal("DDB-472", Code(Run(null, "project", "file", "read", "missing.sql").Doc));
        Assert.Equal("DDB-472", Code(Run(null, "project", "file", "read", "bin.dat").Doc));
        Put("big.sql", new string('x', ProjectFileCommand.MaxBytes + 1));
        Assert.Equal("DDB-472", Code(Run(null, "project", "file", "read", "big.sql").Doc));
        Assert.Equal("DDB-472", Code(Run(new string('y', ProjectFileCommand.MaxBytes + 1), "project", "file", "write", "new.sql", "--create").Doc));
    }
}
