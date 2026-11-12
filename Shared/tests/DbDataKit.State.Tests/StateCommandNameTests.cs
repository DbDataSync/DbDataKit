using System.Diagnostics.Metrics;
using DbDataKit.State;

namespace DbDataKit.State.Tests;

/// <summary>Plan 22a GP28: a state command is counted under a name (module and verb), never its text or parameters.</summary>
public sealed class StateCommandNameTests
{
    [Theory]
    [InlineData("runs", "SELECT * FROM {table:run} WHERE secret = $x", "runs.select")]
    [InlineData("runs", "  update t set a = 1", "runs.update")]
    [InlineData(null, "INSERT INTO t VALUES ($a)", "state.insert")]
    [InlineData("a b; DROP", "DELETE FROM t", "state.delete")]
    [InlineData("runs", "EXPLAIN SELECT 1", "runs.other")]
    [InlineData("runs", "", "runs.other")]
    public void The_name_is_the_module_and_a_verb_from_a_fixed_list(string? module, string sql, string expected) =>
        Assert.Equal(expected, StateDatabase.StatementName(module, sql));

    [Fact]
    public void A_command_made_counts_one_under_its_name_and_carries_no_text()
    {
        var path = Path.Combine(Path.GetTempPath(), "state-command-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var seen = new List<(string Statement, string Engine)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) => { if (instrument == StateDatabase.StateCommands) l.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string statement = "", engine = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "statement") statement = (string)tag.Value!;
                if (tag.Key == "engine") engine = (string)tag.Value!;
            }

            lock (seen) seen.Add((statement, engine));
        });
        listener.Start();
        try
        {
            var database = new StateDatabase(path, new StateSchema(["CREATE TABLE t (a TEXT);"]));
            using var connection = database.OpenConnection();
            using var command = database.Command("things", connection, "SELECT a FROM t WHERE a = 'ROW-VALUE-SENTINEL'");
            lock (seen) Assert.Contains(("things.select", StateEngineIds.Sqlite), seen);
            lock (seen) Assert.DoesNotContain(seen, s => s.Statement.Contains("SENTINEL") || s.Statement.Contains(' '));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}
