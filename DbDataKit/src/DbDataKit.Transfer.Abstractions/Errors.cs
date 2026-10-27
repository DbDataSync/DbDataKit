namespace DbDataKit.Transfer;

/// <summary>Base of every transfer failure. Messages name columns, tables and codes, never values.</summary>
public class TransferException(string message, Exception? inner = null) : Exception(message, inner);

public enum ConversionFailure
{
    OutOfRange, PrecisionLoss, Truncated, InvalidFormat, NotFinite, InvalidText, NullNotAllowed, UnsupportedType,
}

/// <summary>A value did not fit its target column. Carries the column and a failure code, never the value.</summary>
public sealed class ValueConversionException(string column, ConversionFailure failure, string detail)
    : TransferException($"column `{column}`: {detail} ({failure})")
{
    public string Column { get; } = column;
    public ConversionFailure Failure { get; } = failure;
}

/// <summary>The key-reconcile delete guard refused a delete. Counts only.</summary>
public sealed class DeleteGuardExceededException(long scopeCount, long deleted, double maxRatio)
    : TransferException($"delete guard: {deleted} of {scopeCount} row(s) in scope would be deleted, above the allowed ratio {maxRatio:P0}")
{
    public long ScopeCount { get; } = scopeCount;
    public long Deleted { get; } = deleted;
}

/// <summary>Why the write gate refused a statement, so a product can word it in its own terms.</summary>
public enum GateRefusal
{
    /// <summary>The statement's effect class is not one the running command permits.</summary>
    NotPermitted,
    /// <summary>The gate is read-only and the statement writes.</summary>
    ReadOnly,
    /// <summary>The statement log could not be written, so nothing was executed.</summary>
    LogUnavailable,
}

/// <summary>The write gate refused a statement before it reached a driver.</summary>
public sealed class GateRefusedException(string message, GateRefusal reason = GateRefusal.NotPermitted) : TransferException(message)
{
    public GateRefusal Reason { get; } = reason;
}

/// <summary>The strategy/engine combination is not valid (see the capability matrix).</summary>
public sealed class StrategyNotSupportedException(string strategy, string engine, string reason)
    : TransferException($"strategy `{strategy}` is not available on `{engine}`: {reason}")
{
    public string Strategy { get; } = strategy;
    public string Engine { get; } = engine;
}
