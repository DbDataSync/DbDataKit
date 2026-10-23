#r "nuget: Microsoft.Data.Sqlite, 10.0.13"
using Microsoft.Data.Sqlite;

string answer;
using (var connection = new SqliteConnection("Data Source=:memory:"))
{
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "select sqlite_version(), 40 + 2";
    using var reader = command.ExecuteReader();
    reader.Read();
    answer = $"sqlite={reader.GetString(0)} sum={reader.GetInt32(1)}";
}
answer
