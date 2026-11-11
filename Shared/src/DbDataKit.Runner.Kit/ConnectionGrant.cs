using DbDataKit.Runner.Protocol;
using DbDataKit.Runner.Protocol.V1;
using Grpc.Core;

namespace DbDataKit.Runner.Kit;

/// <summary>
/// What the runner hands a task that opens a connection slot (plan 28c section 6, <c>ResolveConnection</c>): the connection the channel binds the slot to, how to reach it, and the login, held in memory for the run. The
/// secret members are never written to a log by this type (<see cref="ToString"/> names the connection only), and a job that keeps a grant keeps it for its own run: the runner hands out nothing after the run's token ends.
/// A job whose slot type is <see cref="ConnectionGrant"/> (or <see cref="object"/>) gets this itself from <c>Slot&lt;T&gt;.OpenAsync</c>; any other type needs a <see cref="RemoteRunOptions.SlotFactory"/> that makes the typed handle from it.
/// </summary>
public sealed class ConnectionGrant
{
    /// <summary>The slot as the manifest names it.</summary>
    public required string Slot { get; init; }

    /// <summary>The connection the channel binds the slot to (its <c>connection/1</c> name).</summary>
    public required string Connection { get; init; }

    public string Engine { get; init; } = "";

    /// <summary><c>default</c>, <c>read</c> or <c>write</c>: the login role this grant carries.</summary>
    public string Role { get; init; } = "default";

    public string? Host { get; init; }

    public int? Port { get; init; }

    public string? Database { get; init; }

    /// <summary>A connection string without credentials, when the connection has one.</summary>
    public string? ConnectionString { get; init; }

    /// <summary>The connection forbids a write login.</summary>
    public bool ReadOnly { get; init; }

    /// <summary><c>sql</c>, <c>integrated</c>, <c>none</c> or <c>token</c>.</summary>
    public string AuthMode { get; init; } = "";

    public string? User { get; init; }

    public string? Password { get; init; }

    /// <summary>The whole connection string as a secret (Build style), when the login is one.</summary>
    public string? ConnectionStringSecret { get; init; }

    public string? Token { get; init; }

    public override string ToString() => $"connection '{Connection}' for slot '{Slot}' ({Role} login, secrets withheld)";

    internal static ConnectionGrant From(ResolveConnectionReply reply, string slot) => new()
    {
        Slot = slot,
        Connection = reply.Connection,
        Engine = reply.Engine,
        Role = reply.Role,
        Host = reply.HasHost ? reply.Host : null,
        Port = reply.HasPort ? reply.Port : null,
        Database = reply.HasDatabase ? reply.Database : null,
        ConnectionString = reply.HasConnectionString ? reply.ConnectionString : null,
        ReadOnly = reply.ReadOnly,
        AuthMode = reply.AuthMode,
        User = reply.HasUser ? reply.User : null,
        Password = reply.HasPassword ? reply.Password : null,
        ConnectionStringSecret = reply.HasConnectionStringSecret ? reply.ConnectionStringSecret : null,
        Token = reply.HasToken ? reply.Token : null,
    };

    /// <summary>The opener a remote run uses when the host gives none: asks the runner for the slot's connection and its login, and makes the handle with <paramref name="factory"/> (null: the grant itself).</summary>
    internal static SlotOpener Opener(RunnerApi.RunnerApiClient client, Metadata headers, Func<ConnectionGrant, Type, object>? factory) =>
        async (slot, type, ct) =>
        {
            ConnectionGrant grant;
            try
            {
                var reply = await client.ResolveConnectionAsync(new ResolveConnectionRequest { Slot = slot, Role = "default" }, headers, cancellationToken: ct).ResponseAsync.ConfigureAwait(false);
                grant = From(reply, slot);
            }
            catch (RpcException ex)
            {
                throw new RunnerProtocolException(ex.StatusCode, ex.Status.Detail, ex);
            }

            if (factory is not null) return factory(grant, type);
            if (type.IsAssignableFrom(typeof(ConnectionGrant))) return grant;
            throw new InvalidOperationException($"The slot '{slot}' is typed {type.Name}, and the runner hands out a {nameof(ConnectionGrant)}: give the run a RemoteRunOptions.SlotFactory that makes a {type.Name} from it, or type the slot Slot<{nameof(ConnectionGrant)}>.");
        };
}
