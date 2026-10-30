using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using DuckDB.NET.Data;
using DuckDB.NET.Native;

namespace DbDataBuild.Targets.DuckDb;

/// <summary>
/// The marker functions of surrogate keys (plan 25, K04). A key is a macro, and DuckDB expands a macro while it binds, so the plan the lowerer reads would hold the hash as DuckDB computes it (`sha256`, `substr`, a hex
/// cast), which no other engine takes. For lowering, the key's macro instead calls `ddb_key_ti(a, b)` (one letter per argument: `t` text, `i` whole number, `d` date), a function registered here on the planning
/// connection, so the call stays in the plan with its arguments and each target writes its own hash over them (`TargetRules.Finish`). The function is volatile, so DuckDB never folds a call with constant arguments, and
/// it is never run: a planning connection only describes and serializes plans. Like <see cref="ClockOverrides"/> it is registered on planning connections only.
/// </summary>
public static unsafe class KeyMarkers
{
    private static readonly Regex Name = new(@"\bddb_key_([tid]+)\s*\(", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Registers every marker the statements call (none: nothing is registered).</summary>
    public static void Register(DuckDBConnection connection, IEnumerable<string> statements)
    {
        foreach (var codes in statements.SelectMany(s => Name.Matches(s).Select(m => m.Groups[1].Value.ToLowerInvariant())).Distinct(StringComparer.Ordinal))
            RegisterOne(connection, codes);
    }

    private static void RegisterOne(DuckDBConnection connection, string codes)
    {
        var function = NativeMethods.ScalarFunction.DuckDBCreateScalarFunction();
        try
        {
            NativeMethods.ScalarFunction.DuckDBScalarFunctionSetName(function, "ddb_key_" + codes);
            foreach (var code in codes)
                using (var type = NativeMethods.LogicalType.DuckDBCreateLogicalType(code switch { 't' => DuckDBType.Varchar, 'i' => DuckDBType.BigInt, _ => DuckDBType.Date }))
                    NativeMethods.ScalarFunction.DuckDBScalarFunctionAddParameter(function, type);
            using (var returns = NativeMethods.LogicalType.DuckDBCreateLogicalType(DuckDBType.BigInt)) NativeMethods.ScalarFunction.DuckDBScalarFunctionSetReturnType(function, returns);
            NativeMethods.ScalarFunction.DuckDBScalarFunctionSetFunction(function, &Run);
            NativeMethods.ScalarFunction.DuckDBScalarFunctionSetVolatile(function);                       // never folded: the call stays in the plan
            if (!DuckDBStateExtensions.IsSuccess(NativeMethods.ScalarFunction.DuckDBRegisterScalarFunction(connection.NativeConnection, function)))
                throw new InvalidOperationException($"DuckDB refused to register the key marker ddb_key_{codes}");
        }
        finally { NativeMethods.ScalarFunction.DuckDBDestroyScalarFunction(ref function); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Run(nint info, nint chunk, nint output) =>
        NativeMethods.ScalarFunction.DuckDBScalarFunctionSetError(info, "A key marker is a placeholder for lowering and is never evaluated.");
}
