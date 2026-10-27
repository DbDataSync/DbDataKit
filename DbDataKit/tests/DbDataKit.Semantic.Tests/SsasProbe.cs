using Xunit;

namespace DbDataKit.Semantic.Tests;

/// <summary>
/// Not a test of the engine: with DBDATA_TEST_SSAS_PROBE set, it sends a set of DAX shapes to a real server and writes what each returned or the error it
/// gave to <c>ssas-probe.txt</c>, so an unknown about what SSAS accepts can be settled in one workflow run. Does nothing otherwise.
/// </summary>
public class SsasProbe
{
    private static readonly string? Ssas = Environment.GetEnvironmentVariable("DBDATA_TEST_SSAS");

    private const string Rollup2 = "ROLLUPADDISSUBTOTAL ( 'Customer'[Region], \"_sub_region\" ), ROLLUPADDISSUBTOTAL ( 'Date'[Fiscal Quarter], \"_sub_quarter\" )";

    private static readonly (string Name, string Dax)[] Variants =
    [
        ("version", "EVALUATE SELECTCOLUMNS ( INFO.VIEW.TABLES (), \"n\", [Name] )"),
        ("A bare, one column", "EVALUATE SUMMARIZECOLUMNS ( ROLLUPADDISSUBTOTAL ( 'Customer'[Region], \"_sub_region\" ), \"b\", [Budget Amount] )"),
        ("B bare, flag name as a measure-like literal with no spaces in call", "EVALUATE SUMMARIZECOLUMNS(ROLLUPADDISSUBTOTAL('Customer'[Region],\"isTotal\"),\"b\",[Budget Amount])"),
        ("C inside SELECTCOLUMNS", "EVALUATE SELECTCOLUMNS ( SUMMARIZECOLUMNS ( ROLLUPADDISSUBTOTAL ( 'Customer'[Region], \"_sub_region\" ), \"b\", [Budget Amount] ), \"r\", 'Customer'[Region], \"b\", [b] )"),
        ("D inside ADDCOLUMNS", "EVALUATE ADDCOLUMNS ( SUMMARIZECOLUMNS ( ROLLUPADDISSUBTOTAL ( 'Customer'[Region], \"_sub_region\" ), \"b\", [Budget Amount] ), \"lvl\", IF ( [_sub_region], 1, 0 ) )"),
        ("E with ROLLUPGROUP", "EVALUATE SUMMARIZECOLUMNS ( ROLLUPADDISSUBTOTAL ( ROLLUPGROUP ( 'Customer'[Region] ), \"_sub_region\" ), \"b\", [Budget Amount] )"),
        ("F two rolled up", "EVALUATE SUMMARIZECOLUMNS ( " + Rollup2 + ", \"b\", [Budget Amount] )"),
        ("G one rolled up, one plain", "EVALUATE SUMMARIZECOLUMNS ( 'Date'[Fiscal Quarter], ROLLUPADDISSUBTOTAL ( 'Customer'[Region], \"_sub_region\" ), \"b\", [Budget Amount] )"),
        ("H rolled up first, then plain", "EVALUATE SUMMARIZECOLUMNS ( ROLLUPADDISSUBTOTAL ( 'Customer'[Region], \"_sub_region\" ), 'Date'[Fiscal Quarter], \"b\", [Budget Amount] )"),
        ("I ROLLUP with SUMMARIZE and ISSUBTOTAL", "EVALUATE SUMMARIZE ( 'Budget', ROLLUP ( 'Customer'[Region] ), \"b\", [Budget Amount], \"lvl\", ISSUBTOTAL ( 'Customer'[Region] ) )"),
        ("J ROLLUPADDISSUBTOTAL, filter by TREATAS", "EVALUATE SUMMARIZECOLUMNS ( ROLLUPADDISSUBTOTAL ( 'Customer'[Region], \"_sub_region\" ), TREATAS ( { \"FY27-Q1\" }, 'Date'[Fiscal Quarter] ), \"b\", [Budget Amount] )"),
        ("K blank row today (Q1, West measure)", "EVALUATE SELECTCOLUMNS ( SUMMARIZECOLUMNS ( 'Customer'[Region], TREATAS ( { \"FY27-Q1\" }, 'Date'[Fiscal Quarter] ), \"w\", CALCULATE ( [Budget Amount], KEEPFILTERS ( TREATAS ( { \"West\" }, 'Customer'[Region] ) ) ) ), \"r\", 'Customer'[Region], \"w\", [w] )"),
        ("L blank row fix: ADDCOLUMNS over the dimension values", "EVALUATE ADDCOLUMNS ( CALCULATETABLE ( VALUES ( 'Customer'[Region] ), TREATAS ( { \"FY27-Q1\" }, 'Date'[Fiscal Quarter] ) ), \"w\", CALCULATE ( [Budget Amount], KEEPFILTERS ( TREATAS ( { \"West\" }, 'Customer'[Region] ) ) ) )"),
        ("M blank row fix: IGNORE-style, +0 on the measure", "EVALUATE SUMMARIZECOLUMNS ( 'Customer'[Region], TREATAS ( { \"FY27-Q1\" }, 'Date'[Fiscal Quarter] ), \"w\", CALCULATE ( [Budget Amount], KEEPFILTERS ( TREATAS ( { \"West\" }, 'Customer'[Region] ) ) ) + 0 )"),
        ("N Customer rows in that quarter, to see what East has", "EVALUATE SUMMARIZECOLUMNS ( 'Customer'[Region], TREATAS ( { \"FY27-Q1\" }, 'Date'[Fiscal Quarter] ), \"all\", [Budget Amount], \"n\", [Budget Lines] )"),
        // how blanks behave in the filters the emitter writes (SQL drops a NULL from <>, NOT IN, NOT LIKE and every comparison)
        ("O1 NOT IN over a blank (West made blank)", "EVALUATE FILTER ( ADDCOLUMNS ( 'Customer', \"r\", IF ( [Region] = \"West\", BLANK (), [Region] ) ), NOT ( [r] IN { \"East\" } ) )"),
        ("O2 > over a blank", "EVALUATE FILTER ( ADDCOLUMNS ( 'Customer', \"r\", IF ( [Region] = \"West\", BLANK (), [Region] ) ), [r] > \"A\" )"),
        ("O3 NOT CONTAINSSTRING over a blank", "EVALUATE FILTER ( ADDCOLUMNS ( 'Customer', \"r\", IF ( [Region] = \"West\", BLANK (), [Region] ) ), NOT CONTAINSSTRING ( [r], \"zzz\" ) )"),
        ("O4 = over a blank and the empty string", "EVALUATE FILTER ( ADDCOLUMNS ( 'Customer', \"r\", IF ( [Region] = \"West\", BLANK (), [Region] ) ), [r] = \"\" )"),
        ("O5 the guarded form", "EVALUATE FILTER ( ADDCOLUMNS ( 'Customer', \"r\", IF ( [Region] = \"West\", BLANK (), [Region] ) ), NOT ISBLANK ( [r] ) && NOT ( [r] IN { \"East\" } ) )"),
        // what EXECUTE would do with text that is not a DAX query: the executor runs agent text as it is
        ("P1 MDX through the same command", "SELECT [Measures].[Budget Amount] ON 0 FROM [Model]"),
        ("P2 a DMV query", "SELECT [DATABASE_NAME] FROM $SYSTEM.DBSCHEMA_CATALOGS"),
    ];

    [Fact]
    public void Probe()
    {
        if (Ssas is null || Environment.GetEnvironmentVariable("DBDATA_TEST_SSAS_PROBE") is null) return;
        var conn = Ssas + ";Catalog=qis_budget";
        DbDataKit.Semantic.Ssas.SsasExecutor.DeployModel(Ssas, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "conformance", "ssas", "model.tmsl.json")));
        var text = string.Join("\n\n", Variants.Select(v => $"### {v.Name}\n{v.Dax}\n-->\n{DbDataKit.Semantic.Ssas.SsasExecutor.Probe(conn, v.Dax)}"));
        File.WriteAllText("ssas-probe.txt", text);
        Console.WriteLine(text);
    }
}
