#r "nuget: Npgsql, 10.0.3"
using Npgsql;

// No server is contacted: building a connection string and a data source only proves the assembly graph loads.
var builder = new NpgsqlConnectionStringBuilder { Host = "localhost", Database = "x", Username = "u" };
var source = NpgsqlDataSource.Create(builder.ConnectionString);
var text = $"npgsql {typeof(NpgsqlConnection).Assembly.GetName().Version} data source for {builder.Host}/{builder.Database}";
source.Dispose();
text
