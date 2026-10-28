using System.Reflection;
using System.Runtime.Loader;
using DbDataKit.Scripting;

namespace DbDataKit.Scripting.Tests;

public class BinderTests
{
    [Fact]
    public async Task A_bound_closure_is_found_by_name_in_the_default_context_and_binding_it_again_is_not_a_conflict()
    {
        using var temp = new Feed();
        var closure = await RealPackages.ResolveAsync(temp, "Npgsql");

        Assert.Empty(DefaultContextBinder.Bind(closure));
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("Npgsql"));

        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(assembly));
        Assert.NotNull(assembly.GetType("Npgsql.NpgsqlConnection"));
        Assert.Empty(DefaultContextBinder.Bind(closure));
    }

    [Fact]
    public void A_second_closure_with_another_file_for_the_same_assembly_is_a_conflict_the_caller_sees()
    {
        var first = new Closure();
        first.Managed["Some.Library.ForTheBinder"] = Path.Combine(Path.GetTempPath(), "a", "Some.Library.ForTheBinder.dll");
        var second = new Closure();
        second.Managed["Some.Library.ForTheBinder"] = Path.Combine(Path.GetTempPath(), "b", "Some.Library.ForTheBinder.dll");

        Assert.Empty(DefaultContextBinder.Bind(first));
        Assert.Equal(["Some.Library.ForTheBinder"], DefaultContextBinder.Bind(second));
    }
}
