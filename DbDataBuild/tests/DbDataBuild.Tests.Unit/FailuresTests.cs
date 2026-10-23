using System.Data.Common;
using DbDataBuild.Execution;

namespace DbDataBuild.Tests.Unit;

/// <summary>What a driver failure is called in a report: the type and an error number, never the driver's message. The classification is the shared classifier's.</summary>
public sealed class FailuresTests
{
    private sealed class StateException(string message, string? state) : DbException(message)
    {
        public override string? SqlState { get; } = state;
    }

    [Fact]
    public void A_database_exception_with_a_sqlstate_is_named_by_it()
    {
        var text = Failures.Describe(new StateException("Login failed for user 'sa'. Password was hunter2", "28000"));

        Assert.Equal("StateException (error 28000)", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("sa", text.Replace("StateException", ""));
    }

    [Fact]
    public void The_number_is_found_inside_a_wrapping_exception() =>
        Assert.Equal(" (error 08006)", Failures.Number(new InvalidOperationException("outer", new StateException("secret host prod-db-01", "08006"))));

    [Fact]
    public void A_catalog_exception_is_named_by_its_sqlstate_and_function_text_is_not_repeated() =>
        Assert.Equal(" (error IM001)", Failures.Number(new OdbcCatalogException("SQLTables", "IM001")));

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(IOException))]
    public void An_exception_with_no_engine_number_has_none(Type type)
    {
        var exception = (Exception)Activator.CreateInstance(type, "password=hunter2")!;

        Assert.Equal("", Failures.Number(exception));
        Assert.Equal(type.Name, Failures.Describe(exception));
    }

    [Fact]
    public void A_sqlstate_that_is_not_five_letters_or_digits_is_not_a_number() =>
        Assert.Equal("", Failures.Number(new StateException("x", "not a state")));
}
