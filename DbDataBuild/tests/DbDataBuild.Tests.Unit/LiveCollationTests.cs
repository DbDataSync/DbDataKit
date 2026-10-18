using DbDataBuild.Models;

namespace DbDataBuild.Tests.Unit;

public class LiveCollationTests
{
    private static ModelDefinition Model(params ColumnDefinition[] columns) =>
        new("marts.fct", ModelKinds.Full, [], null, null, [], null, columns, []);

    private static ColumnDefinition Col(string name, string? collation = null) => new(name, "VARCHAR(20)", true, collation);

    [Fact]
    public void A_live_collation_that_satisfies_the_profile_passes()
    {
        var d = CollationChecker.CheckLive(ProjectConfig.Default, "sqlserver", Model(Col("s")), [("s", "Latin1_General_100_CI_AS")]);
        Assert.Empty(d);
    }

    [Fact]
    public void A_live_collation_that_contradicts_the_profile_is_an_error_naming_the_column()
    {
        var d = Assert.Single(CollationChecker.CheckLive(ProjectConfig.Default, "sqlserver", Model(Col("s")), [("s", "Latin1_General_100_CS_AS")]));
        Assert.Equal("DDB-310", d.Code);
        Assert.Equal(DbDataBuild.Core.Severity.Error, d.Severity);
        Assert.Equal("sqlserver:marts.fct.s", d.Location.File);
        Assert.Contains("case is sensitive but the profile requires insensitive", d.Found);
    }

    [Fact]
    public void A_declared_exception_and_columns_the_model_does_not_declare_are_not_held_to_the_profile()
    {
        var model = Model(Col("exact", "exact"), Col("s"));
        Assert.Empty(CollationChecker.CheckLive(ProjectConfig.Default, "sqlserver", model, [("exact", "Latin1_General_100_BIN2"), ("foreign", "Latin1_General_100_CS_AS")]));
    }

    [Fact]
    public void A_column_on_the_database_default_cannot_be_verified()
    {
        var d = Assert.Single(CollationChecker.CheckLive(ProjectConfig.Default, "postgres", Model(Col("s")), [("s", null)]));
        Assert.Equal("DDB-311", d.Code);
        Assert.Contains("database default", d.Found);
    }

    [Fact]
    public void An_unrecognized_live_name_is_unverifiable_not_assumed_to_match()
    {
        var d = Assert.Single(CollationChecker.CheckLive(ProjectConfig.Default, "sqlserver", Model(Col("s")), [("s", "Weird_Name")]));
        Assert.Equal("DDB-311", d.Code);
    }
}
