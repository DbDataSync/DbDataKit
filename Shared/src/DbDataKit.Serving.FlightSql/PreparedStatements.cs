using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace DbDataKit.Serving.FlightSql;

/// <summary>Server-side record of a prepared statement: the text, the source it was prepared against, and the parameter rows the client last bound.</summary>
public sealed record PreparedStatement(string Owner, string DataSource, string Sql, DateTimeOffset LastUsed, IReadOnlyList<IReadOnlyList<ServingParameter>>? Parameters = null);

public sealed class PreparedStatementStore(TimeProvider time)
{
    private static readonly TimeSpan IdleLifetime = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, PreparedStatement> _statements = new();

    /// <summary>
    /// A new handle: 32 hex characters as ASCII bytes. Not raw random bytes: the Apache .NET Flight SQL client keeps the handle as a string, so bytes that are not valid UTF-8 come back changed and the statement is "unknown".
    /// </summary>
    public byte[] Add(string owner, string dataSource, string sql)
    {
        byte[] handle = System.Text.Encoding.ASCII.GetBytes(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
        _statements[Convert.ToHexString(handle)] = new PreparedStatement(owner, dataSource, sql, time.GetUtcNow());
        return handle;
    }

    public PreparedStatement? Get(byte[] handle, string owner)
    {
        string key = Convert.ToHexString(handle);
        if (!_statements.TryGetValue(key, out var statement)) return null;
        if (!string.Equals(statement.Owner, owner, StringComparison.OrdinalIgnoreCase)) return null;
        _statements[key] = statement with { LastUsed = time.GetUtcNow() };
        return statement;
    }

    public void Bind(byte[] handle, string owner, IReadOnlyList<IReadOnlyList<ServingParameter>> parameters)
    {
        string key = Convert.ToHexString(handle);
        if (_statements.TryGetValue(key, out var s) && string.Equals(s.Owner, owner, StringComparison.OrdinalIgnoreCase))
            _statements[key] = s with { Parameters = parameters, LastUsed = time.GetUtcNow() };
    }

    public void Remove(byte[] handle, string owner)
    {
        string key = Convert.ToHexString(handle);
        if (_statements.TryGetValue(key, out var s) && string.Equals(s.Owner, owner, StringComparison.OrdinalIgnoreCase))
            _statements.TryRemove(key, out _);
    }

    public void Sweep()
    {
        var cutoff = time.GetUtcNow() - IdleLifetime;
        foreach (var (key, statement) in _statements)
            if (statement.LastUsed < cutoff) _statements.TryRemove(key, out _);
    }
}
