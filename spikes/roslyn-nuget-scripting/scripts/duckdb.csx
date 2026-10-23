#r "nuget: DuckDB.NET.Data.Full, 1.5.6"
using DuckDB.NET.Data;

// A top-level `using var` is not allowed in a script, hence the explicit blocks.
string answer;
using (var connection = new DuckDBConnection("Data Source=:memory:"))
{
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "select 42 as answer, version() as engine";
    using var reader = command.ExecuteReader();
    reader.Read();
    answer = $"answer={reader.GetInt32(0)} duckdb={reader.GetString(1)}";
}
answer + " ALC=" + System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(DuckDBConnection).Assembly)?.Name
