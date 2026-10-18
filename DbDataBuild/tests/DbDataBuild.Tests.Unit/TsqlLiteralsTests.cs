using DbDataBuild.Targets.Rules;

namespace DbDataBuild.Tests.Unit;

public class TsqlLiteralsTests
{
    [Theory]
    [InlineData("SELECT 'plain'", "SELECT 'plain'")]                                                         // ASCII is left as it was
    [InlineData("SELECT 'café'", "SELECT N'café'")]
    [InlineData("SELECT '日本語', 'ascii', '😀'", "SELECT N'日本語', 'ascii', N'😀'")]
    [InlineData("SELECT N'日本語'", "SELECT N'日本語'")]                                                        // already national
    [InlineData("SELECT n'é'", "SELECT n'é'")]
    [InlineData("SELECT 'it''s é'", "SELECT N'it''s é'")]                                                   // a doubled quote stays inside the literal
    [InlineData("SELECT 'a' + 'é'", "SELECT 'a' + N'é'")]
    [InlineData("SELECT [café] FROM [dbo].[tàble] WHERE \"é\" = 'x'", "SELECT [café] FROM [dbo].[tàble] WHERE \"é\" = 'x'")]      // identifiers are not literals
    [InlineData("SELECT 1 -- 'é' in a comment\nUNION SELECT 'é'", "SELECT 1 -- 'é' in a comment\nUNION SELECT N'é'")]
    [InlineData("SELECT /* 'é' /* nested */ 'é' */ 'x', 'é'", "SELECT /* 'é' /* nested */ 'é' */ 'x', N'é'")]
    [InlineData("SELECT 'é", "SELECT N'é")]                                                                // an unterminated literal does not loop or throw
    public void A_literal_with_a_character_outside_ASCII_gets_the_N_prefix_and_nothing_else_changes(string input, string expected) =>
        Assert.Equal(expected, TsqlLiterals.Nationalize(input));

    [Fact]
    public void It_is_applied_to_the_text_a_load_renders_for_SQL_Server_and_not_for_PostgreSQL()
    {
        Assert.Equal("SELECT N'é'", TargetRules.Finish("SELECT 'é'", "sqlserver"));
        Assert.Equal("SELECT 'é'", TargetRules.Finish("SELECT 'é'", "postgres"));
    }
}
