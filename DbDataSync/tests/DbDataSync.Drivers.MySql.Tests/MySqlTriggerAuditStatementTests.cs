namespace DbDataSync.Drivers.MySql.Tests;

/// <summary>DDL-shape assertions with no database involved — the string-level defects worth catching
/// without a live server, mirroring <c>PostgresTriggerAudit</c>'s own coverage split between this and
/// the live-server assertions in <c>TriggerAuditReaderTests</c>.</summary>
public sealed class MySqlTriggerAuditStatementTests
{
    [Fact]
    public void CreateTriggers_EmitsThreeSeparateTriggers_NotOne()
    {
        var sql = MySqlTriggerAudit.CreateTriggers("db", "orders", ["id"]);

        Assert.Contains("AFTER INSERT ON", sql);
        Assert.Contains("AFTER UPDATE ON", sql);
        Assert.Contains("AFTER DELETE ON", sql);
        // MySQL cannot combine the three events in one definition — the whole reason this method
        // exists rather than reusing Postgres's single-trigger shape.
        Assert.DoesNotContain("INSERT OR UPDATE OR DELETE", sql);
    }

    [Fact]
    public void CreateTriggers_PrecedesEachWithDropIfExists_SoTheStepIsSafelyRerunnable()
    {
        var sql = MySqlTriggerAudit.CreateTriggers("db", "orders", ["id"]);

        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(sql, "DROP TRIGGER IF EXISTS").Count);
    }

    [Fact]
    public void CreateTriggers_UsesColonFreeRowReferences_NewAndOldWithNoSigil()
    {
        var sql = MySqlTriggerAudit.CreateTriggers("db", "orders", ["id"]);

        Assert.Contains("NEW.`id`", sql);
        Assert.Contains("OLD.`id`", sql);
        Assert.DoesNotContain(":NEW", sql);
        Assert.DoesNotContain(":OLD", sql);
    }

    [Fact]
    public void CreateTriggers_QualifiesTheShadowTableWithDatabaseDotTable()
    {
        var sql = MySqlTriggerAudit.CreateTriggers("mydb", "orders", ["id"]);

        Assert.Contains("`mydb`.`DS_Changes_orders`", sql);
    }

    [Fact]
    public void CreateTriggers_HandlesACompositeKey_InEveryTrigger()
    {
        var sql = MySqlTriggerAudit.CreateTriggers("db", "orders", ["region", "id"]);

        Assert.Contains("NEW.`region`, NEW.`id`", sql);
        Assert.Contains("OLD.`region`, OLD.`id`", sql);
    }

    [Fact]
    public void CreateShadowTable_UsesAutoIncrement_NotGeneratedAlwaysAsIdentity()
    {
        var sql = MySqlTriggerAudit.CreateShadowTable("db", "orders", ["`id` INT NOT NULL"]);

        Assert.Contains("BIGINT AUTO_INCREMENT PRIMARY KEY", sql);
        Assert.DoesNotContain("GENERATED ALWAYS", sql);
    }

    [Fact]
    public void TriggerName_IsStableAndDistinctPerEvent()
    {
        Assert.Equal("ds_trg_orders_ai", MySqlTriggerAudit.TriggerName("orders", "ai"));
        Assert.Equal("ds_trg_orders_au", MySqlTriggerAudit.TriggerName("orders", "au"));
        Assert.Equal("ds_trg_orders_ad", MySqlTriggerAudit.TriggerName("orders", "ad"));
    }

    [Fact]
    public void TheUpdateTrigger_RecordsTheOldKeyAsADelete_WhenTheKeyChanges()
    {
        var sql = MySqlTriggerAudit.CreateTriggers("db", "orders", ["region", "id"]);

        Assert.Contains("SELECT 'D', OLD.`region`, OLD.`id` FROM DUAL WHERE NOT (OLD.`region` <=> NEW.`region` AND OLD.`id` <=> NEW.`id`)", sql);
        Assert.Contains("UNION ALL SELECT 'U', NEW.`region`, NEW.`id`", sql);
    }

    /// <summary>One statement per trigger, so a ";" followed by a newline still splits the script.</summary>
    [Fact]
    public void EveryTrigger_IsOneStatement_WithNoCompoundBody()
    {
        var sql = MySqlTriggerAudit.CreateTriggers("db", "orders", ["id"]);

        Assert.DoesNotContain("BEGIN", sql);
        Assert.Equal(6, sql.Split(";\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length);
    }

    [Fact]
    public void TheCurrentUpdateTrigger_IsRecognised_AndTheOldTextIsNot()
    {
        var sql = MySqlTriggerAudit.CreateTriggers("db", "orders", ["id"]);
        var current = sql.Split(";\n").Single(s => s.Contains("AFTER UPDATE"));
        const string old = "INSERT INTO `db`.`DS_Changes_orders` (`DS_Op`, `id`) VALUES ('U', NEW.`id`)";

        Assert.True(MySqlTriggerAudit.IsCurrentUpdateTrigger(current));
        Assert.False(MySqlTriggerAudit.IsCurrentUpdateTrigger(old));
        Assert.False(MySqlTriggerAudit.IsCurrentUpdateTrigger(null));
    }
}
