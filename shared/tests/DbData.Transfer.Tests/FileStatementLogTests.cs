using System.Text.Json;
using DbData.Dialect;
using Xunit;

namespace DbData.Transfer.Tests;

public class FileStatementLogTests
{
    [Fact]
    public async Task The_file_log_is_json_lines_flushed_per_entry_and_holds_no_values_by_default()
    {
        var dir = Directory.CreateTempSubdirectory("stmtlog").FullName;
        try
        {
            string path;
            using (var log = new FileStatementLog(dir, "apply run/1", Guid.NewGuid()))
            {
                path = log.Path;
                var gate = new WriteGate(new FakeExecutor(), new WriteGateOptions("apply", log));
                var s = new SqlStatement("s", "INSERT INTO t VALUES (@p)", [new SqlParameter("@p", "p", CanonicalType.String(), "PLAIN-VALUE")], new WriteIntent("s", WriteKind.Insert, "t"));
                await gate.ExecuteAsync(s, TestContext.Current.CancellationToken);
                // readable while the log is still open: every line was flushed
                using var rd = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                var text = await rd.ReadToEndAsync(TestContext.Current.CancellationToken);
                Assert.Equal(2, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            }
            var lines = (await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken)).Select(l => JsonDocument.Parse(l).RootElement).ToList();
            Assert.Equal("begin", lines[0].GetProperty("phase").GetString());
            Assert.Equal("string(unicode)", lines[0].GetProperty("parameters")[0].GetProperty("type").GetString());
            Assert.DoesNotContain("PLAIN-VALUE", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            Assert.DoesNotContain("/", Path.GetFileName(path));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Creating_a_log_never_overwrites_an_existing_one()
    {
        var dir = Directory.CreateTempSubdirectory("stmtlog").FullName;
        try
        {
            var id = Guid.NewGuid();
            using var a = new FileStatementLog(dir, "x", id);
            // same second, same id: the second create must fail rather than truncate
            Assert.Throws<IOException>(() => new FileStatementLog(dir, "x", id).Dispose());
        }
        finally { Directory.Delete(dir, true); }
    }
}
