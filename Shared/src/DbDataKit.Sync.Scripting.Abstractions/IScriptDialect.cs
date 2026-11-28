using DbDataKit.Dialect;
using DbDataKit.Sync.Drivers.Abstractions;
using DbDataKit.Sync.Core.Sql;

namespace DbDataKit.Sync.Scripting.Abstractions;

/// <summary>
/// What a script is told about the engine it is generating SQL for.
/// <para>
/// Deliberately narrower than the driver layer's own <c>SqlDialect</c>, and deliberately not that type:
/// the dialect lives in <c>DbDataKit.Sync.Drivers.Generic</c>, which references this project, so taking it
/// here would be circular. It is also a moving target — it gained four hooks across three phases — and
/// a script written today should still compile when it gains a fifth.
/// </para>
/// </summary>
public interface IScriptDialect
{
    /// <summary>The engine's name, for a script that has to branch: <c>MsSql</c>, <c>Postgres</c>.
    /// Matches <see cref="DbDataKit.Sync.Core.Config.DriverIds"/>'s built-in values.</summary>
    string EngineName { get; }

    string QuoteIdentifier(string identifier);

    /// <summary>How a parameter is written in statement text — <c>@p</c>, <c>:p</c>.</summary>
    string ParameterReference(string name);

    /// <summary>One of this engine's native type specs as the shared dialects' type (<c>DbDataKit.Dialect.CanonicalType</c>), or
    /// <see cref="CanonicalType.Unmappable"/> when the engine's table does not know it or Sync does not replicate it (an interval, a list).
    /// For <see cref="ILifecycleHook"/>: a schema-evolution hook has to reason about a column's type across engines, and hand-writing a
    /// type map inside a hook is the worst possible place for one.</summary>
    CanonicalType ToCanonicalType(string nativeType);

    /// <summary>The reverse direction: DDL for a type, in this dialect, with a note of what it loses.</summary>
    RenderedColumnType RenderColumnType(CanonicalType type);
}
