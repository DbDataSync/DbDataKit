using DbData.Dialect;
using Xunit;

namespace DbData.Dialect.Tests;

public class ReadOnlyPolicyTests
{
    private static ReadOnlyDecision Check(string engine, string sql) => ReadOnlyPolicy.Check(sql, DialectCatalog.Builtin.Get(engine));

    [Theory]
    [InlineData("sqlserver", "SELECT TOP (5) * FROM [update] ORDER BY 1;")]
    [InlineData("sqlserver", "WITH a AS (SELECT 1 x) SELECT * FROM a")]
    [InlineData("postgres", "SELECT 'a; DROP TABLE x' -- delete\n")]
    [InlineData("postgres", @"SELECT E'it\'s; DROP TABLE x'")]
    [InlineData("postgres", "SELECT $$; DROP TABLE x$$")]
    [InlineData("postgres", "SELECT \"insert\" FROM t /* drop */")]
    [InlineData("mysql", "SELECT `delete` FROM t # drop table")]
    [InlineData("mysql", "(SELECT 1) UNION (SELECT 2)")]
    [InlineData("oracle", "SELECT q'[x'; DROP TABLE t]' FROM dual")]
    [InlineData("duckdb", "FROM tbl")]
    [InlineData("postgres", "TABLE tbl")]
    public void Allows_plain_reads(string engine, string sql)
        => Assert.True(Check(engine, sql).Allowed, Check(engine, sql).Reason);

    [Theory]
    [InlineData("postgres", @"SELECT E'it\'s'; DROP TABLE x")]
    [InlineData("postgres", @"SELECT 'a\'; DROP TABLE x --'")]
    [InlineData("postgres", "SELECT pg_sleep(10)")]
    [InlineData("postgres", "SELECT pg_catalog.pg_read_file('/etc/passwd')")]
    [InlineData("postgres", "SELECT lo_import('/etc/passwd')")]
    [InlineData("postgres", "SELECT nextval('s')")]
    [InlineData("postgres", "SELECT * FROM dblink('x','select 1') t(a int)")]
    [InlineData("postgres", "WITH d AS (DELETE FROM t RETURNING *) SELECT * FROM d")]
    [InlineData("postgres", "SELECT * INTO newtab FROM t")]
    [InlineData("postgres", "SELECT 1; SELECT 2")]
    [InlineData("postgres", "SELECT 'unterminated")]
    [InlineData("postgres", "COPY t TO '/tmp/x'")]
    [InlineData("sqlserver", "EXEC xp_cmdshell 'dir'")]
    [InlineData("sqlserver", "SELECT * FROM OPENROWSET('x','y','z')")]
    [InlineData("sqlserver", "SELECT * FROM OPENQUERY(srv, 'select 1')")]
    [InlineData("sqlserver", "SELECT * FROM OPENDATASOURCE('a','b').c.d.e")]
    [InlineData("sqlserver", "SELECT 1; DROP TABLE x")]
    [InlineData("sqlserver", "SELECT sys.xp_dirtree('c:')")]
    [InlineData("sqlserver", "DELETE FROM t")]
    [InlineData("mysql", "SELECT 1 /*!50000 ; DROP TABLE x */")]
    [InlineData("mysql", "SELECT * FROM t INTO OUTFILE '/tmp/x'")]
    [InlineData("mysql", "SELECT LOAD_FILE('/etc/passwd')")]
    [InlineData("mysql", "SELECT SLEEP(5)")]
    [InlineData("oracle", "SELECT utl_http.request('x') FROM dual")]
    [InlineData("oracle", "SELECT dbms_lock.sleep(5) FROM dual")]
    [InlineData("duckdb", "SELECT * FROM read_csv('/etc/passwd')")]
    [InlineData("duckdb", "COPY t TO 'x.csv'")]
    [InlineData("duckdb", "ATTACH 'x.db'")]
    [InlineData("duckdb", "INSTALL httpfs")]
    [InlineData("duckdb", "LOAD httpfs")]
    [InlineData("duckdb", "SELECT 1; ATTACH 'x'")]
    [InlineData("ansi", "")]
    [InlineData("ansi", "UPDATE t SET a = 1")]
    public void Refuses_hostile_sql(string engine, string sql)
    {
        var d = Check(engine, sql);
        Assert.False(d.Allowed);
        Assert.False(string.IsNullOrEmpty(d.Reason));
    }

    [Fact]
    public void Parser_seam_can_only_tighten()
    {
        var pg = DialectCatalog.Builtin.Get("postgres");
        Assert.False(ReadOnlyPolicy.Check("SELECT 1", pg, new FixedParser([SqlStatementKind.Delete])).Allowed);
        Assert.True(ReadOnlyPolicy.Check("SELECT 1", pg, new FixedParser([SqlStatementKind.Select])).Allowed);
        Assert.False(ReadOnlyPolicy.Check("DELETE FROM t", pg, new FixedParser([SqlStatementKind.Select])).Allowed);
    }

    private sealed class FixedParser(IReadOnlyList<SqlStatementKind> kinds) : ISqlParser
    {
        public IReadOnlyList<SqlStatementKind>? Parse(string sql, EngineId engine) => kinds;
    }

    [Fact]
    public void Classifier_kinds()
    {
        var d = DialectCatalog.Builtin.Get("sqlserver");
        SqlStatementKind K(string s) => SqlStatementClassifier.Classify(s, d).Statements[0].Kind;
        Assert.Equal(SqlStatementKind.Insert, K("insert into t values (1)"));
        Assert.Equal(SqlStatementKind.Merge, K("merge t using s on 1=1 when matched then delete;"));
        Assert.Equal(SqlStatementKind.Ddl, K("drop table t"));
        Assert.Equal(SqlStatementKind.Dcl, K("grant select on t to u"));
        Assert.Equal(SqlStatementKind.Exec, K("exec sp_who"));
        Assert.Equal(SqlStatementKind.Update, K("with a as (select 1) update t set x = 1"));
        Assert.Equal(SqlStatementKind.Unknown, K("frobnicate"));
        Assert.Equal(2, SqlStatementClassifier.Classify("select 1; select 2;;", d).Statements.Count);
    }
}
