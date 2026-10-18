using System.Reflection;
using System.Runtime.InteropServices;

namespace DbDataBuild.Sql.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct PolyglotResultNative
{
    public IntPtr Data;
    public IntPtr Error;
    public int Status;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PolyglotValidationResultNative
{
    public int Valid;
    public IntPtr ErrorsJson;
    public IntPtr Error;
    public int Status;
}

/// <summary>Raw P/Invoke surface of polyglot-sql-ffi (C ABI, JSON payloads). Strings are UTF-8.</summary>
internal static class PolyglotNative
{
    public const string Library = "polyglot_sql_ffi";
    public const string PinnedCommit = "0a7a1a77da6e3d5169119e133d4d608d74a5c9e1";

    static PolyglotNative() => NativeLibrary.SetDllImportResolver(typeof(PolyglotNative).Assembly, Resolve);

    /// <summary>Probe order: DBDATABUILD_POLYGLOT_PATH, next to the application (the extraction directory of a single-file app), runtimes/&lt;rid&gt;/native, then the OS default.</summary>
    private static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
    {
        if (name != Library) return IntPtr.Zero;
        var file = OperatingSystem.IsWindows() ? "polyglot_sql_ffi.dll"
            : OperatingSystem.IsMacOS() ? "libpolyglot_sql_ffi.dylib" : "libpolyglot_sql_ffi.so";
        var rid = OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsMacOS() ? "osx-arm64" : "linux-x64";
        var dir = AppContext.BaseDirectory;     // not Assembly.Location: that is empty inside a single-file app
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("DBDATABUILD_POLYGLOT_PATH"),
            Path.Combine(dir, file),
            Path.Combine(dir, "runtimes", rid, "native", file),
        };
        foreach (var c in candidates)
            if (!string.IsNullOrEmpty(c) && File.Exists(c) && NativeLibrary.TryLoad(c, out var h)) return h;
        return IntPtr.Zero;
    }

    [DllImport(Library, EntryPoint = "polyglot_free_string")] public static extern void FreeString(IntPtr s);
    [DllImport(Library, EntryPoint = "polyglot_version")] public static extern IntPtr Version();
    [DllImport(Library, EntryPoint = "polyglot_dialect_list")] public static extern IntPtr DialectList();

    [DllImport(Library, EntryPoint = "polyglot_transpile_with_options")]
    public static extern PolyglotResultNative TranspileWithOptions(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, [MarshalAs(UnmanagedType.LPUTF8Str)] string from,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string to, [MarshalAs(UnmanagedType.LPUTF8Str)] string optionsJson);

    [DllImport(Library, EntryPoint = "polyglot_parse")]
    public static extern PolyglotResultNative Parse([MarshalAs(UnmanagedType.LPUTF8Str)] string sql, [MarshalAs(UnmanagedType.LPUTF8Str)] string dialect);

    [DllImport(Library, EntryPoint = "polyglot_generate")]
    public static extern PolyglotResultNative Generate([MarshalAs(UnmanagedType.LPUTF8Str)] string astJson, [MarshalAs(UnmanagedType.LPUTF8Str)] string dialect);

    [DllImport(Library, EntryPoint = "polyglot_format")]
    public static extern PolyglotResultNative Format([MarshalAs(UnmanagedType.LPUTF8Str)] string sql, [MarshalAs(UnmanagedType.LPUTF8Str)] string dialect);

    [DllImport(Library, EntryPoint = "polyglot_validate_with_options")]
    public static extern PolyglotValidationResultNative ValidateWithOptions(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, [MarshalAs(UnmanagedType.LPUTF8Str)] string dialect,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string optionsJson);

    [DllImport(Library, EntryPoint = "polyglot_output_columns")]
    public static extern PolyglotResultNative OutputColumns([MarshalAs(UnmanagedType.LPUTF8Str)] string sql, [MarshalAs(UnmanagedType.LPUTF8Str)] string dialect);

    [DllImport(Library, EntryPoint = "polyglot_analyze_query")]
    public static extern PolyglotResultNative AnalyzeQuery([MarshalAs(UnmanagedType.LPUTF8Str)] string sql, [MarshalAs(UnmanagedType.LPUTF8Str)] string optionsJson);

    [DllImport(Library, EntryPoint = "polyglot_free_result")] public static extern void FreeResult(PolyglotResultNative r);
    [DllImport(Library, EntryPoint = "polyglot_free_validation_result")] public static extern void FreeValidationResult(PolyglotValidationResultNative r);

    public static string? Read(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
}
