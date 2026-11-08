namespace DbDataKit.State.Tests;

public sealed class SqliteLockGuardTests : IDisposable
{
    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    [Fact]
    public void A_second_connection_under_an_open_reader_is_refused_naming_both_call_sites_and_after_the_reader_is_closed_it_is_not()
    {
        var database = _engines.Open(StateEngineIds.Sqlite);
        using var outer = database.OpenConnection();
        using var command = database.Command(outer, "SELECT name FROM sqlite_master;");
        using (var reader = command.ExecuteReader())
        {
            Assert.True(reader.Read());
            var refused = Assert.Throws<SqliteConnectionUnderLockException>(() => database.OpenConnection());
            Assert.Contains(nameof(A_second_connection_under_an_open_reader_is_refused_naming_both_call_sites_and_after_the_reader_is_closed_it_is_not), refused.Message);
            SqliteLockGuard.Forget();
            Assert.Contains("the earlier connection was opened at", refused.Message);
        }

        using var second = database.OpenConnection();
    }

    [Fact]
    public void A_second_connection_beside_a_connection_that_holds_no_lock_is_not_refused()
    {
        var database = _engines.Open(StateEngineIds.Sqlite);
        using var outer = database.OpenConnection();
        using var second = database.OpenConnection();
    }
}
