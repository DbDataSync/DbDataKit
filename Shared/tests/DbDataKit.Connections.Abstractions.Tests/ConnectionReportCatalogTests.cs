using DbDataKit.Config.Yaml;

namespace DbDataKit.Connections.Abstractions.Tests;

/// <summary>Plan 32 RC01: the connection kind <c>report-catalog</c> (connection/2, additive): an endpoint (the portal), a <c>report_catalog</c> section, a sign-in; a program that keeps another group refuses it naming the file.</summary>
public sealed class ConnectionReportCatalogTests
{
    private const string Good = "schema: connection/2\nkind: report-catalog\nname: reports\nendpoint: https://reports.example.com/Reports\nreport_catalog: { provider: pbirs, name: sales }\nsign_in: { method: basic, user: svc, password_secret: secret:dbdatakit:server:connection:reports:password }\n";

    private static string[] Problems(string yaml) => [.. ConnectionDefinitionLoader.LoadConnectionOfAnyKind(yaml, "connections/reports.yml").Diagnostics.Where(d => d.Severity == ConfigSeverity.Error).Select(d => d.Message)];

    [Fact]
    public void A_report_catalog_connection_reads_and_is_a_group_of_its_own()
    {
        var result = ConnectionDefinitionLoader.LoadConnectionOfAnyKind(Good, "connections/reports.yml");
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == ConfigSeverity.Error);
        var d = result.Definition!;
        Assert.Equal((ConnectionKinds.ReportCatalog, "pbirs", "sales", "https://reports.example.com/Reports"), (d.KindName, d.ReportCatalog!.Provider, d.ReportCatalog.Name, d.Endpoint));
        Assert.Equal(ConnectionKindGroup.ReportCatalog, ConnectionKindGroups.Of(d));
        Assert.True(ConnectionKinds.IsKnown("report-catalog"));
    }

    [Fact]
    public void A_program_that_keeps_another_group_refuses_it_naming_the_file()
    {
        var refused = ConnectionDefinitionLoader.LoadDatabaseConnection(Good, "connections/reports.yml");
        Assert.Contains(refused.Diagnostics, x => x.Severity == ConfigSeverity.Error && x.Message.Contains("report-catalog", StringComparison.Ordinal) && x.Message.Contains("connections/reports.yml", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("endpoint: https://r.example.com/Reports\nsign_in: { method: none }\n", "needs a `report_catalog` section")]
    [InlineData("report_catalog: { provider: ssrs }\n", "needs an `endpoint`")]
    [InlineData("endpoint: https://r.example.com/Reports\nreport_catalog: { provider: tableau }\n", "provider")]
    [InlineData("endpoint: https://r.example.com/Reports\nreport_catalog: { provider: ssrs, name: \"bad name\" }\n", "does not match")]
    [InlineData("endpoint: https://r.example.com/Reports\nreport_catalog: { provider: ssrs, report_server_address: \"https://u:p@r.example.com/ReportServer\" }\n", "report_server_address")]
    [InlineData("endpoint: \"https://r.example.com/Reports?key=abc\"\nreport_catalog: { provider: ssrs }\n", "query")]
    [InlineData("endpoint: https://r.example.com/Reports\nreport_catalog: { provider: ssrs }\nsign_in: { method: kerberos }\n", "signs in with")]
    [InlineData("endpoint: https://r.example.com/Reports\nreport_catalog: { provider: ssrs }\nsign_in: { method: integrated, password_secret: env:PW }\n", "`password_secret` does not belong to `sign_in.method: integrated`")]
    [InlineData("endpoint: https://r.example.com/Reports\nreport_catalog: { provider: ssrs }\nsign_in: { method: integrated, user: svc }\n", "`user` does not belong")]
    [InlineData("endpoint: https://r.example.com/Reports\nreport_catalog: { provider: ssrs }\nsign_in: { method: windows, password_secret: env:PW }\n", "needs `user`")]
    [InlineData("endpoint: https://r.example.com/Reports\nreport_catalog: { provider: ssrs }\nsign_in: { method: windows, user: svc }\n", "needs `password_secret`")]
    [InlineData("endpoint: https://r.example.com/Reports\nreport_catalog: { provider: ssrs }\nsign_in: { method: windows, user: svc, password_secret: hunter2 }\n", "not a secret reference")]
    [InlineData("endpoint: https://r.example.com/Reports\nreport_catalog: { provider: ssrs }\nsign_in: { method: windows, user: svc, password_secret: env:PW, key_secret: env:K }\n", "`key_secret` does not belong")]
    [InlineData("endpoint: https://r.example.com/Reports\nreport_catalog: { provider: ssrs }\ntest_path: /x\n", "`test_path` belongs")]
    public void A_report_catalog_connection_that_is_not_right_is_refused_with_the_reason(string rest, string expected) =>
        Assert.Contains(Problems($"schema: connection/2\nkind: report-catalog\nname: reports\n{rest}"), p => p.Contains(expected, StringComparison.OrdinalIgnoreCase));

    [Theory]
    [InlineData("windows", "sign_in: { method: windows, user: 'CORP\\svc', password_secret: secret:dbdatakit:server:connection:reports:password }")]
    [InlineData("integrated", "sign_in: { method: integrated }")]
    public void A_windows_sign_in_reads_with_no_problem(string method, string signIn)
    {
        var yaml = $"schema: connection/2\nkind: report-catalog\nname: reports\nendpoint: https://reports.example.com/Reports\nreport_catalog: {{ provider: ssrs }}\n{signIn}\n";
        var result = ConnectionDefinitionLoader.LoadConnectionOfAnyKind(yaml, "connections/reports.yml");
        Assert.Empty(Problems(yaml));
        Assert.Equal(method, result.Definition!.SignIn!.Method);
    }

    [Theory]
    [InlineData("http", "endpoint: https://r.example.com\n", "windows", "kind `report-catalog`")]
    [InlineData("http", "endpoint: https://r.example.com\n", "integrated", "kind `report-catalog`")]
    public void The_windows_methods_belong_to_the_report_catalog_kind_only(string kind, string endpoint, string method, string expected) =>
        Assert.Contains(Problems($"schema: connection/2\nkind: {kind}\nname: x\n{endpoint}sign_in: {{ method: {method}, user: u, password_secret: env:PW }}\n"), p => p.Contains(expected, StringComparison.Ordinal));

    [Fact]
    public void The_section_belongs_to_its_kind_only()
    {
        Assert.Contains(Problems("schema: connection/2\nkind: http\nname: reports\nendpoint: https://r.example.com\nreport_catalog: { provider: ssrs }\n"), p => p.Contains("`report_catalog` belongs", StringComparison.Ordinal));
        Assert.Contains(Problems("schema: connection/1\nname: reports\nengine: duckdb\naddress: { database: x.db }\nreport_catalog: { provider: ssrs }\n"), p => p.Contains("report_catalog", StringComparison.Ordinal));
    }
}
