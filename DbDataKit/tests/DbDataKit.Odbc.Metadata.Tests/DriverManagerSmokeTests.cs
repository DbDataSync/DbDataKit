using System.Runtime.InteropServices;

namespace DbDataKit.Odbc.Metadata.Tests;

/// <summary>
/// The LibraryImport declarations against the real driver manager (odbc32.dll, libodbc.so.2), without any driver: allocation, a connect
/// that must fail with IM002, and diagnostic decoding. Skipped when no driver manager is installed.
/// </summary>
public class DriverManagerSmokeTests
{
    private static bool DriverManagerPresent()
    {
        string[] names = OperatingSystem.IsWindows() ? ["odbc32.dll"] : OperatingSystem.IsMacOS() ? ["libodbc.2.dylib"] : ["libodbc.so.2", "libodbc.so.1"];
        foreach (string name in names)
            if (NativeLibrary.TryLoad(name, out nint handle)) { NativeLibrary.Free(handle); return true; }
        return false;
    }

    [Fact]
    public async Task An_unknown_driver_fails_with_IM002_through_the_real_driver_manager()
    {
        Assert.SkipUnless(DriverManagerPresent(), "No ODBC driver manager is installed.");

        await using var catalog = OdbcCatalog.Connect("Driver={DbData No Such Driver};Server=x");
        var ex = await Assert.ThrowsAsync<OdbcCatalogException>(() => catalog.TablesAsync());

        Assert.Equal("SQLDriverConnect", ex.Function);
        Assert.Equal("IM002", ex.SqlState);
        Assert.False(ex.NotSupported);
        Assert.DoesNotContain("No Such Driver", ex.Message);
        Assert.Contains("word size", OdbcDiagnostics.Explain(ex).ToString());
    }

    [Fact]
    public async Task An_unknown_dsn_fails_with_IM002_and_the_session_surfaces_it_at_open()
    {
        Assert.SkipUnless(DriverManagerPresent(), "No ODBC driver manager is installed.");

        var ex = await Assert.ThrowsAsync<OdbcCatalogException>(() => OdbcCatalogSession.OpenAsync("dbdata_no_such_dsn"));
        Assert.Equal("IM002", ex.SqlState);
    }
}
