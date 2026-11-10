using DbDataBuild.Execution;

namespace DbDataBuild.Tests.Unit;

/// <summary>Plan 23a FQ09: the evaluator's one door to a target is the read login's, and only for the engines it has a dialect for.</summary>
public class QualityAccessTests
{
    [Fact]
    public void The_evaluator_is_given_a_connection_of_the_read_login_only()
    {
        var env = (string name) => name is "DBDATAKIT_BUILD_WAREHOUSE_READ" or "DBDATAKIT_BUILD_WAREHOUSE_WRITE" ? "Host=localhost;Database=x" : null;
        var (read, _) = LoginSettings.FromEnvironment("warehouse", "postgres", Login.Read, env);
        var (write, _) = LoginSettings.FromEnvironment("warehouse", "postgres", Login.Write, env);

        Assert.NotNull(QualityAccess.ReadOnlyOpener(read!));
        Assert.Throws<ArgumentException>(() => QualityAccess.ReadOnlyOpener(write!));
    }

    [Theory]
    [InlineData("sqlserver", true)]
    [InlineData("postgres", true)]
    [InlineData("fabric", false)]
    [InlineData("duckdb", false)]
    [InlineData("odbc", false)]
    [InlineData("flightsql", false)]
    public void Checks_are_evaluated_on_the_engines_the_evaluator_has_a_dialect_for(string engine, bool supported) => Assert.Equal(supported, QualityAccess.Supports(engine));

    [Fact]
    public void The_door_is_the_only_public_way_to_a_connection_and_the_openers_of_the_login_stay_internal()
    {
        var public_ = typeof(LoginSettings).GetMethods().Where(m => typeof(System.Threading.Tasks.Task).IsAssignableFrom(m.ReturnType) && m.ReturnType.ToString().Contains("DbConnection"));
        Assert.Empty(public_);
        var door = typeof(QualityAccess).GetMethod(nameof(QualityAccess.ReadOnlyOpener))!;
        Assert.Equal(typeof(LoginSettings), door.GetParameters().Single().ParameterType);       // a login, which the method refuses unless it is the read one
    }
}
