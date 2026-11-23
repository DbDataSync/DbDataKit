using System.Reflection;

namespace DbDataKit.Deploy.Tests;

/// <summary>A control service that deploys builds composes this library, so it must not bring the Workspace's build kinds, the programs they start or the app bundler with it.</summary>
public sealed class DependencyTests
{
    [Theory]
    [InlineData("DbDataKit.Authoring.Server")]   // the kinds of project, their builds and the child programs
    [InlineData("DbDataKit.Apps.Bundling")]   // Bun
    [InlineData("DbDataKit.Apps.Build")]
    public void The_library_does_not_reach_the_build_side(string assembly)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal);
        void Walk(Assembly from)
        {
            foreach (var name in from.GetReferencedAssemblies().Where(n => n.Name?.StartsWith("DbDataKit.", StringComparison.Ordinal) == true))
                if (reached.Add(name.Name!)) Walk(Assembly.Load(name));
        }
        Walk(typeof(DeployException).Assembly);

        Assert.Contains("DbDataKit.Runner.Client", reached);
        Assert.DoesNotContain(assembly, reached);
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, assembly + ".dll")), $"{assembly}.dll is in the output of a project that references only the deploy library.");
    }
}
