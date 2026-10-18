using DbDataBuild.Core;
using DbDataBuild.Execution;

namespace DbDataBuild.Tests.Unit;

public class ReadGuardTests
{
    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("select a, b from t where c = 'insert'")]
    [InlineData("WITH x AS (SELECT 1 AS a) SELECT a FROM x;")]
    [InlineData("-- drop table x\nSELECT 1 /* delete */")]
    [InlineData("SELECT \"update\" FROM t")]
    [InlineData("SELECT [drop] FROM t")]
    [InlineData("SELECT 'it''s; fine'")]
    public void Plain_reads_pass(string sql) => Assert.Null(ReadGuard.Check(sql));

    [Theory]
    [InlineData("")]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("SELECT 1; DROP TABLE t")]
    [InlineData("SELECT * INTO copy FROM t")]
    [InlineData("WITH x AS (INSERT INTO t VALUES (1) RETURNING a) SELECT a FROM x")]
    [InlineData("EXEC sp_who")]
    [InlineData("SELECT 1 UNION ALL SELECT 2; CREATE TABLE t (a int)")]
    [InlineData("SELECT set_config('x', 'y', false), 1 FROM (SELECT 1) s WHERE 1 = 1; SET x = 1")]
    public void Anything_else_is_refused(string sql) => Assert.Equal("DDB-504", ReadGuard.Check(sql)!.Code);

    [Theory]
    // a string that hides a quote from the scanner must not hide a statement from the engine
    [InlineData("SELECT $$'$$; DROP TABLE x; --'")]
    [InlineData("SELECT $tag$'$tag$; DROP TABLE x; --'")]
    [InlineData("SELECT 1 `; DROP TABLE x; --`")]
    public void Quote_hiding_tricks_are_refused(string sql) => Assert.Equal("DDB-504", ReadGuard.Check(sql)!.Code);
}
