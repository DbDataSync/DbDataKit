using System.Data;
using DbDataBuild.State;

namespace DbDataBuild.Execution;

/// <summary>The effect class of one statement. A command permits some of these and the gate refuses the rest (DESIGN.md 9.3).</summary>
[Flags]
public enum StatementKind { Tracking = 1, Data = 2, Ddl = 4 }

/// <summary>A value bound through the driver. Statement text never contains values.</summary>
public sealed record GateParameter(string Name, DbType Type, object? Value);

/// <summary>
/// A statement the gate may execute. There is no public constructor: a statement comes from a plan step (<see cref="FromPlanStep"/>) or from a
/// tracking-table writer inside this assembly (<see cref="Tracking"/>), so arbitrary text cannot be executed by accident.
/// </summary>
public sealed class GateStatement
{
    public string StepId { get; }
    public StatementKind Kind { get; }
    public string Text { get; }
    public IReadOnlyList<GateParameter> Parameters { get; }
    public string Hash { get; }

    /// <summary>The parameters are rows of a bulk load: the log records how many values were sent, not the values (a load of thousands of rows would make the log as large as the data, and it is reproducible from its source).</summary>
    public bool BulkValues { get; }

    private GateStatement(string stepId, StatementKind kind, string text, IReadOnlyList<GateParameter> parameters, bool bulkValues = false)
    {
        StepId = stepId; Kind = kind; Text = text; Parameters = parameters; Hash = Hashing.ScriptHash(text); BulkValues = bulkValues;
    }

    /// <summary>A batch of rows of a bulk load: an INSERT with a parameter per value. A data statement; its values are not written to the statement log.</summary>
    public static GateStatement BulkInsert(string stepId, string text, IReadOnlyList<GateParameter> parameters) => new(stepId, StatementKind.Data, text, parameters, bulkValues: true);

    /// <summary>
    /// The rows of a copy, written to a staging table with the engine's bulk route (SQL Server's bulk copy, PostgreSQL's binary `COPY`). A data statement; the log records the table, the columns and the row count, never a value.
    /// <paramref name="table"/> is the already-quoted qualified name the plan carries, so the text in the log says what the plan says.
    /// </summary>
    public static GateStatement BulkCopy(string stepId, string table, IReadOnlyList<TransferColumn> columns) =>
        new(stepId, StatementKind.Data, $"BULK COPY INTO {table} ({string.Join(", ", columns.Select(c => c.Name))})", [], bulkValues: true);

    /// <summary>A step of a plan. Plan steps are data loads or DDL; tracking writes never come from a plan.</summary>
    public static GateStatement FromPlanStep(string stepId, StatementKind kind, string text, IReadOnlyList<GateParameter>? parameters = null)
    {
        if (kind == StatementKind.Tracking || !Enum.IsDefined(kind)) throw new ArgumentException("A plan step is a data or DDL statement.", nameof(kind));
        return new(stepId, kind, text, parameters ?? []);
    }

    internal static GateStatement Tracking(string stepId, string text, IReadOnlyList<GateParameter>? parameters = null) =>
        new(stepId, StatementKind.Tracking, text, parameters ?? []);
}

// The statement log's entry and interface are the shared ones (DbDataKit.Transfer, plan 13, B06): what the log records about one execution, with error detail a type name and a number, never driver text, which can
// contain data. The csproj aliases them as IStatementLog, StatementLogEntry and MemoryStatementLog for this tool's projects; the file log (one JSON line per entry, one file per run, flushed before Append
// returns) is the shared FileStatementLog too (plan 13, B07). What stays here is the retention of the files (`StatementLogRetention`).
