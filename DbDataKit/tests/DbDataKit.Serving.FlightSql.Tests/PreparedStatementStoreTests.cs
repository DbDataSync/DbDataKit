namespace DbDataKit.Serving.FlightSql.Tests;

public class PreparedStatementStoreTests
{
    [Fact]
    public void A_prepared_statement_handle_survives_being_read_as_a_string()
    {
        // The Apache .NET Flight SQL client keeps the handle as a string; raw random bytes came back changed and the statement was "unknown".
        var store = new PreparedStatementStore(TimeProvider.System);
        for (int i = 0; i < 2000; i++)
        {
            byte[] handle = store.Add("alice", "numbers", "SELECT 1");
            byte[] roundTripped = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(handle));
            Assert.Equal(handle, roundTripped);
            Assert.NotNull(store.Get(roundTripped, "alice"));
        }
    }
}
