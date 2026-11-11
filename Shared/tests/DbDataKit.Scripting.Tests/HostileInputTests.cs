using DbDataKit.Scripting;

namespace DbDataKit.Scripting.Tests;

/// <summary>The hostile inputs of H1 (docs/review/SECURITY.md): MSBuild injection through the package id and version, and traversal through names. Moved with the validation from `Connections.Libraries` (plan 13a, S02).</summary>
public class HostileInputTests
{
    public static TheoryData<string> BadPackageIds => new()
    {
        "", " ", "Foo\"/><Target Name=\"x\"><Exec Command=\"calc\"/></Target><X Include=\"", "Foo\" Version=\"1\" /><Import Project=\"evil.props",
        "Foo Bar", "Foo\nBar", "Foo;Bar", "Foo$(Evil)", "$(MSBuildProjectDirectory)", "%(Identity)", "..\\..\\evil", "../evil", "a/b", "C:\\evil",
        "Foo..Bar", ".Foo", "Foo.", "-Foo", "Foo<Bar>", "Foo&amp;", "Foo'Bar", "Fo\u043e", "Foo\0", new string('a', 101), "Foo,Bar", "Foo*", "Foo|Bar",
    };

    [Theory]
    [MemberData(nameof(BadPackageIds))]
    public void Hostile_package_ids_are_refused(string id) => Assert.Throws<LibraryValidationException>(() => LibraryValidation.ValidatePackageId(id));

    [Theory]
    [InlineData("MySqlConnector")]
    [InlineData("Microsoft.Data.SqlClient")]
    [InlineData("Npgsql")]
    [InlineData("DuckDB.NET.Data.Full")]
    [InlineData("System.Data.Odbc")]
    [InlineData("Oracle.ManagedDataAccess.Core")]
    [InlineData("IBM.Data.DB2.Core-lnx")]
    [InlineData("Some_Pkg-2.Thing")]
    public void Real_package_ids_pass(string id) => LibraryValidation.ValidatePackageId(id);

    public static TheoryData<string> BadVersions => new()
    {
        "", " ", "1", "1.0", "01.0.0", "1.0.0.", "1.0.0\"/><Target Name=\"x\"/>", "1.0.0\" /><Import Project=\"x", "$(Foo)", "$(Version)", "*", "1.*", "1.0.*", "[1.0,2.0)",
        "(1.0,)", "[1.0.0]", ">=1.0.0", "1.0.0;2.0.0", "1.0.0 ", " 1.0.0", "1.0.0\n", "v1.0.0", "latest", "1.0.0-", "1.0.0-beta..1", "1.0.0+", "1.0.0-\u0431", "1.0.0%(x)",
        "..\\1.0.0", "1.0.0/../x", new string('1', 70) + ".0.0",
    };

    [Theory]
    [MemberData(nameof(BadVersions))]
    public void Hostile_versions_are_refused(string v) => Assert.Throws<LibraryValidationException>(() => LibraryValidation.ValidateVersion(v));

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("7.1.1")]
    [InlineData("10.0.3")]
    [InlineData("1.5.6")]
    [InlineData("1.0.0-beta.1")]
    [InlineData("1.0.0-rc.2+build.5")]
    [InlineData("1.2.3.4")]
    [InlineData("0.0.0")]
    public void Real_versions_pass(string v) => LibraryValidation.ValidateVersion(v);

    public static TheoryData<string> BadNames => new()
    {
        "", "..", ".", "../x", "..\\x", "a/b", "a\\b", "/abs", "C:\\x", "C:x", "UPPER", "con", "nul", "com1", "aux.txt", "a..b", ".hidden", "trailing.", "-lead", "a b", "a\0b",
        "a\nb", new string('a', 65), "caf\u00e9", "a:stream", "x*", "x?",
    };

    [Theory]
    [MemberData(nameof(BadNames))]
    public void Hostile_library_ids_and_file_names_are_refused(string name) => Assert.Throws<LibraryValidationException>(() => LibraryValidation.ValidateName(name));

    [Theory]
    [InlineData("mysql-connector")]
    [InlineData("system-data-odbc")]
    [InlineData("oracle")]
    [InlineData("a")]
    [InlineData("driver.v2_x")]
    public void Real_names_pass(string name) => LibraryValidation.ValidateName(name);

    [Theory]
    [InlineData("MySqlConnector.MySqlConnectorFactory, MySqlConnector")]
    [InlineData("Microsoft.Data.SqlClient.SqlClientFactory, Microsoft.Data.SqlClient")]
    [InlineData("Ns.Outer+Inner,Asm")]
    [InlineData("Ns.Type")]
    public void Factory_types_pass(string t) => LibraryValidation.ValidateFactoryType(t);

    [Theory]
    [InlineData("")]
    [InlineData("A.B, C; Evil")]
    [InlineData("A.B\nC")]
    [InlineData("A.B, C, Version=1.0.0.0")]
    [InlineData("../A")]
    [InlineData("A<B>")]
    public void Odd_factory_types_are_refused(string t) => Assert.Throws<LibraryValidationException>(() => LibraryValidation.ValidateFactoryType(t));

    [Fact]
    public void Error_messages_do_not_echo_control_characters()
    {
        var ex = Assert.Throws<LibraryValidationException>(() => LibraryValidation.ValidatePackageId("evil\r\nINJECT"));
        Assert.DoesNotContain('\n', ex.Message);
        Assert.DoesNotContain('\r', ex.Message);
    }

    // ---- paths ------------------------------------------------------------------------------------
}
