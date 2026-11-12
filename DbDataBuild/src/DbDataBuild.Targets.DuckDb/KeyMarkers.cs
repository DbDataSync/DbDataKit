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
    private static readonly Regex Name = new(@"\bddb_key_([tidm]+)\s*\(", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // the markers of a key kept in a map: `ddb_keymap_ti_customer_key('marts', a, b)` reads the key, `ddb_keytext_ti_customer_key('marts', a, b)` is the text of the business key (see SurrogateKey.KeyMapMarkerName)
    private static readonly Regex MapName = new(@"\b(ddb_key(?:map|text)_([tidm]+)_[A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Registers every marker the statements call (none: nothing is registered).</summary>
    public static void Register(DuckDBConnection connection, IEnumerable<string> statements)
    {
        var texts = statements.ToList();
        foreach (var codes in texts.SelectMany(s => Name.Matches(s).Select(m => m.Groups[1].Value.ToLowerInvariant())).Distinct(StringComparer.Ordinal))
            RegisterOne(connection, "ddb_key_" + codes, codes, withSchema: false, returnsText: false);
        foreach (var marker in texts.SelectMany(s => MapName.Matches(s).Select(m => (Name: m.Groups[1].Value, Codes: m.Groups[2].Value.ToLowerInvariant()))).DistinctBy(m => m.Name.ToLowerInvariant()))
            RegisterOne(connection, marker.Name.ToLowerInvariant(), marker.Codes, withSchema: true, returnsText: marker.Name.StartsWith("ddb_keytext_", StringComparison.OrdinalIgnoreCase));
    }

    private static void RegisterOne(DuckDBConnection connection, string name, string codes, bool withSchema, bool returnsText)
    {
        var function = NativeMethods.ScalarFunction.DuckDBCreateScalarFunction();
        try
        {
            NativeMethods.ScalarFunction.DuckDBScalarFunctionSetName(function, name);
            if (withSchema)
                using (var schema = NativeMethods.LogicalType.DuckDBCreateLogicalType(DuckDBType.Varchar))
                    NativeMethods.ScalarFunction.DuckDBScalarFunctionAddParameter(function, schema);          // the owner's schema name, which says where the map is
            foreach (var code in codes)
                using (var type = NativeMethods.LogicalType.DuckDBCreateLogicalType(code switch { 't' => DuckDBType.Varchar, 'i' => DuckDBType.BigInt, 'd' => DuckDBType.Date, _ => DuckDBType.Timestamp }))
                    NativeMethods.ScalarFunction.DuckDBScalarFunctionAddParameter(function, type);
            using (var returns = NativeMethods.LogicalType.DuckDBCreateLogicalType(returnsText ? DuckDBType.Varchar : DuckDBType.BigInt)) NativeMethods.ScalarFunction.DuckDBScalarFunctionSetReturnType(function, returns);
            NativeMethods.ScalarFunction.DuckDBScalarFunctionSetFunction(function, &Run);
            NativeMethods.ScalarFunction.DuckDBScalarFunctionSetVolatile(function);                       // never folded: the call stays in the plan
            if (!DuckDBStateExtensions.IsSuccess(NativeMethods.ScalarFunction.DuckDBRegisterScalarFunction(connection.NativeConnection, function)))
                throw new InvalidOperationException($"DuckDB refused to register the key marker {name}");
        }
        finally { NativeMethods.ScalarFunction.DuckDBDestroyScalarFunction(ref function); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Run(nint info, nint chunk, nint output) =>
        NativeMethods.ScalarFunction.DuckDBScalarFunctionSetError(info, "A key marker is a placeholder for lowering and is never evaluated.");
}
