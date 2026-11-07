using System.Globalization;
using DbDataKit.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DbDataProxy.Server.State;

/// <summary>The state already has a running instance and is SQLite, which one instance at a time may use.</summary>
public sealed class SecondInstanceException(string message) : InvalidOperationException(message);

/// <summary>
/// Each running instance's row in the state (plan 20 O02), refreshed while it runs and removed when it stops. A group of instances shares a SQL Server or PostgreSQL state; SQLite is one instance's, so an instance that finds
/// another live one on a SQLite state refuses to start, saying so. A row is live while it was refreshed in the last <see cref="Stale"/> and, on this machine, its process is still running; a crashed instance's row is not.
/// </summary>
public sealed class ServingInstanceLease(StateDatabase state, ILogger<ServingInstanceLease> log, TimeProvider? clock = null) : IHostedLifecycleService, IDisposable
{
    public static readonly TimeSpan Stale = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly string _key = Guid.NewGuid().ToString("N");
    private ITimer? _timer;
    private bool _registered;

    public string InstanceKey => _key;

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var others = Live(now).Where(i => i.Key != _key).ToList();
        if (state.Dialect.Engine == StateEngineIds.Sqlite && others.Count > 0)
        {
            var other = others[0];
            throw new SecondInstanceException(
                $"Another instance (process {other.ProcessId} on {other.Host}, started {other.Started:u}) is running against this state database, which is SQLite: one instance at a time may use it. " +
                "Stop that one, or run a group of instances against a SQL Server or PostgreSQL state (state: { engine: mssql|postgres, connection: ... } in server.yaml).");
        }
        Write(now, insert: true);
        _registered = true;
        if (others.Count > 0) log.LogInformation("{Count} other instance(s) share this state.", others.Count);
        _timer = _clock.CreateTimer(_ => Heartbeat(), null, Every, Every);
        return Task.CompletedTask;
    }

    private void Heartbeat()
    {
        try { Write(_clock.GetUtcNow(), insert: false); }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException or TimeoutException)
        {
            log.LogWarning("This instance's row in the state could not be refreshed ({Reason}).", ex.GetType().Name);
        }
    }

    private sealed record Row(string Key, string Host, int ProcessId, DateTimeOffset Started, DateTimeOffset Heartbeat);

    private List<Row> Live(DateTimeOffset now)
    {
        var rows = state.Retry(() =>
        {
            using var connection = state.OpenConnection();
            using var cmd = state.Command("serving", connection, "SELECT instance_key, host, process_id, started_at, heartbeat_at FROM {table:serving_instance};");
            using var reader = cmd.ExecuteReader();
            var list = new List<Row>();
            while (reader.Read())
                list.Add(new Row(reader.GetString(0), reader.GetString(1), Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture), reader.Time(3), reader.Time(4)));
            return list;
        });
        return [.. rows.Where(r => now - r.Heartbeat < Stale && (r.Host != Environment.MachineName || Running(r.ProcessId)))];
    }

    private static bool Running(int processId)
    {
        try { using var process = System.Diagnostics.Process.GetProcessById(processId); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private void Write(DateTimeOffset now, bool insert) => state.Retry(() =>
    {
        using var connection = state.OpenConnection();
        if (insert)
        {
            using var cmd = state.Command("serving", connection, "INSERT INTO {table:serving_instance} (instance_key, host, process_id, started_at, heartbeat_at) VALUES ($key, $host, $pid, $at, $at);");
            cmd.Bind(state, "key", _key);
            cmd.Bind(state, "host", Environment.MachineName);
            cmd.Bind(state, "pid", Environment.ProcessId);
            cmd.Bind(state, "at", now);
            cmd.ExecuteNonQuery();
            // rows of instances that are gone (a crash) are dropped by whoever starts next
            using var stale = state.Command("serving", connection, "DELETE FROM {table:serving_instance} WHERE heartbeat_at < $stale;");
            stale.Bind(state, "stale", now - Stale - Stale);
            stale.ExecuteNonQuery();
        }
        else
        {
            using var cmd = state.Command("serving", connection, "UPDATE {table:serving_instance} SET heartbeat_at = $at WHERE instance_key = $key;");
            cmd.Bind(state, "at", now);
            cmd.Bind(state, "key", _key);
            cmd.ExecuteNonQuery();
        }
    });

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        Release();
        return Task.CompletedTask;
    }

    /// <summary>Removes this instance's row (once); also done on disposal, so a host that failed part way through starting (a port taken) does not leave a live-looking row behind for the next start.</summary>
    private void Release()
    {
        _timer?.Dispose();
        _timer = null;
        if (!_registered) return;
        _registered = false;
        try
        {
            state.Retry(() =>
            {
                using var connection = state.OpenConnection();
                using var cmd = state.Command("serving", connection, "DELETE FROM {table:serving_instance} WHERE instance_key = $key;");
                cmd.Bind(state, "key", _key);
                cmd.ExecuteNonQuery();
            });
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
        {
            log.LogWarning("This instance's row in the state could not be removed ({Reason}); it lapses in {Seconds} seconds.", ex.GetType().Name, Stale.TotalSeconds);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => Release();


}
