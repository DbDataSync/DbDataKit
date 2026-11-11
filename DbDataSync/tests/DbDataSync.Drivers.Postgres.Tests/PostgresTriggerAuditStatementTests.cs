namespace DbDataSync.Drivers.Postgres.Tests;

/// <summary>DDL-shape assertions with no database involved (the live ones are in <c>TriggerAuditReaderTests</c>).</summary>
public sealed class PostgresTriggerAuditStatementTests
{
    [Fact]
    public void TheUpdateBranch_RecordsTheOldKeyAsADelete_WhenAnyKeyColumnChanges()
    {
        var sql = PostgresTriggerAudit.CreateFunction("public", "orders", ["Region", "Id"]);

        Assert.Contains(
            "IF (OLD.\"Region\" IS DISTINCT FROM NEW.\"Region\" OR OLD.\"Id\" IS DISTINCT FROM NEW.\"Id\") THEN", sql);
        Assert.Contains(PostgresTriggerAudit.VersionMarker, sql);

        var update = sql.IndexOf("ELSIF (TG_OP = 'UPDATE')", StringComparison.Ordinal);
        var deleteOfOld = sql.IndexOf("'D', OLD.\"Region\", OLD.\"Id\"", update, StringComparison.Ordinal);
        var updateOfNew = sql.IndexOf("'U', NEW.\"Region\", NEW.\"Id\"", update, StringComparison.Ordinal);
        Assert.InRange(deleteOfOld, update + 1, updateOfNew - 1);
    }

    /// <summary>Replacing an installed trigger of the old text is the function alone: the trigger names the
    /// function, so no DROP TRIGGER and no second CREATE TRIGGER (which would fail on a duplicate).</summary>
    [Fact]
    public void TheFunctionAlone_CarriesNoTrigger_AndTheFullStepCarriesBoth()
    {
        var function = PostgresTriggerAudit.CreateFunction("public", "orders", ["Id"]);
        var both = PostgresTriggerAudit.CreateFunctionAndTrigger("public", "orders", ["Id"]);

        Assert.DoesNotContain("CREATE TRIGGER", function);
        Assert.Contains("CREATE OR REPLACE FUNCTION", function);
        Assert.StartsWith(function, both);
        Assert.Contains("CREATE TRIGGER \"ds_trg_orders\"", both);
    }
}
