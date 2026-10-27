using Xunit;

namespace DbDataKit.Transfer.Tests;

/// <summary>
/// The gate invariant, once, for every product that writes to a database (plan 13, B06; it began as DbDataBuild's `GateInvariantTests`): driver commands, bulk routes and transactions are created and run only in the files
/// that implement the gate's executors and the read side, so a write cannot reach a database without passing <see cref="WriteGate"/>. The scan is <see cref="SourceInvariants"/>; each product's own tests run the same
/// scan on its own source (DbDataBuild's `GateInvariantTests` do), and this one runs it on every product in the repository from the shared suite, so a product that adds a driver call outside its allowed files fails here too.
/// </summary>
public class GateInvariantTests
{
    /// <summary>A product's source folder under the repository root and the files (or folders) that may touch a command object.</summary>
    public sealed record Product(string Name, string SourceFolder, string[] Allowed)
    {
        public override string ToString() => Name;
    }

    public static TheoryData<Product> Products => new()
    {
        new Product("DbDataKit transfer packages", "DbDataKit/src", [
            "DbDataKit.Transfer/Gate/AdoWriteExecutor.cs", "DbDataKit.Transfer.Conformance/ConformanceRunner.cs", "DbDataKit.Transfer.SqlServer/SqlServerBulkWriter.cs",
            "DbDataKit.Transfer.Postgres/PostgresBulkWriter.cs", "DbDataKit.Transfer/Gate/SourceInvariants.cs"]),
        // Build: the write executor lives in MutationGate.cs (the Build front of the shared gate), reads in ReadSession.cs; the in-memory DuckDB describer and sample runner have no target connection
        new Product("DbDataBuild", "DbDataBuild/src", [
            "DbDataBuild.Execution/MutationGate.cs", "DbDataBuild.Execution/ReadSession.cs", "DbDataBuild.Targets.DuckDb/", "DbDataBuild.Sample/"]),
    };

    private static string? RepositoryRoot() => SourceInvariants.FindRoot(AppContext.BaseDirectory, "DbDataKit.slnx");

    [Theory, MemberData(nameof(Products))]
    public void Driver_commands_are_created_only_in_the_files_that_implement_the_gate_and_the_reads(Product product)
    {
        var root = RepositoryRoot();
        Assert.SkipWhen(root is null || !Directory.Exists(Path.Combine(root, product.SourceFolder)), $"{product.Name}: its source is not beside the shared libraries here");
        var source = Path.Combine(root!, product.SourceFolder);
        var packages = product.SourceFolder.StartsWith("DbDataKit/", StringComparison.Ordinal)
            ? Directory.EnumerateDirectories(source, "DbDataKit.Transfer*").ToList()
            : [source];
        var offenders = new List<string>();
        foreach (var package in packages)
        {
            var allowed = product.SourceFolder.StartsWith("DbDataKit/", StringComparison.Ordinal)
                ? product.Allowed.Where(a => a.StartsWith(Path.GetFileName(package) + "/", StringComparison.Ordinal)).Select(a => a[(Path.GetFileName(package).Length + 1)..])
                : product.Allowed;
            offenders.AddRange(SourceInvariants.FindOffenders(package, SourceInvariants.DriverCalls, allowed).Select(f => $"{Path.GetFileName(package)}/{f}"));
        }
        Assert.True(offenders.Count == 0, $"{product.Name}: writes must go through the gate. Driver calls found in: " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_scan_finds_a_violation_in_a_product_source_tree()
    {
        var dir = Directory.CreateTempSubdirectory("gate-inv").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "App.Execution"));
            File.WriteAllText(Path.Combine(dir, "App.Execution", "MutationGate.cs"), "class G { void M(System.Data.Common.DbConnection c) { c.CreateCommand().ExecuteNonQuery(); } }");
            File.WriteAllText(Path.Combine(dir, "App.Execution", "Sneaky.cs"), "class S { void M(System.Data.Common.DbConnection c) { c.CreateCommand().ExecuteNonQuery(); } }");
            Assert.Equal(["App.Execution/Sneaky.cs"], SourceInvariants.FindOffenders(dir, SourceInvariants.DriverCalls, ["App.Execution/MutationGate.cs"]));
        }
        finally { Directory.Delete(dir, true); }
    }
}
