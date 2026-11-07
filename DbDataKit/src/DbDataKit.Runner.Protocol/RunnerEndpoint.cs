using System.IO.Pipes;
using System.Net.Sockets;
using Grpc.Net.Client;

namespace DbDataKit.Runner.Protocol;

/// <summary>The names the runner and a task agree on (plan 28a DR3): the run token travels in the metadata of every call, and a child process is given both values in its environment, never in argv.</summary>
public static class RunnerProtocolNames
{
    /// <summary>The metadata key that carries the run token.</summary>
    public const string TokenHeader = "x-dbdatakit-run-token";

    /// <summary>The environment variable that holds the endpoint (<see cref="RunnerEndpoint.ToString"/>).</summary>
    public const string EndpointVariable = "DBDATAKIT_RUNNER_ENDPOINT";

    /// <summary>The environment variable that holds the run token.</summary>
    public const string TokenVariable = "DBDATAKIT_RUNNER_TOKEN";

    /// <summary>The environment variable that holds the path of the package assembly a task host loads (plan 28a R06). Not a secret.</summary>
    public const string AssemblyVariable = "DBDATAKIT_TASK_ASSEMBLY";

    /// <summary>The environment variable that holds the name of the task a task host runs: the name in the job's <c>[RunnerTask]</c>. Not a secret.</summary>
    public const string TaskVariable = "DBDATAKIT_TASK_NAME";

    /// <summary>The environment variable that holds the task's scratch directory (also its working directory and its temp directory): the one place it may write, removed after the run.</summary>
    public const string ScratchVariable = "DBDATAKIT_SCRATCH_DIR";

    /// <summary>The environment variable that holds the root of the package's materialized version: read-only to the task.</summary>
    public const string PackageDirectoryVariable = "DBDATAKIT_PACKAGE_DIR";

    /// <summary>The largest chunk a stream carries in either direction (plan 28b: streams hold one chunk at a time).</summary>
    public const int MaxChunkBytes = 1024 * 1024;
}

public enum RunnerEndpointKind
{
    /// <summary>A Unix domain socket (Linux and macOS): the address is the socket's path.</summary>
    UnixSocket,
    /// <summary>A named pipe (Windows): the address is the pipe's name, without the <c>\\.\pipe\</c> prefix.</summary>
    NamedPipe,
}

/// <summary>Where the runner listens: a Unix domain socket or a named pipe, never a TCP port. Written <c>unix:/path/to/runner.sock</c> or <c>npipe:name</c>.</summary>
public sealed record RunnerEndpoint(RunnerEndpointKind Kind, string Address)
{
    public static RunnerEndpoint Unix(string path) => new(RunnerEndpointKind.UnixSocket, path);

    public static RunnerEndpoint Pipe(string name) => new(RunnerEndpointKind.NamedPipe, name);

    public override string ToString() => Kind == RunnerEndpointKind.UnixSocket ? "unix:" + Address : "npipe:" + Address;

    public static RunnerEndpoint Parse(string text)
    {
        if (text is not null && text.StartsWith("unix:", StringComparison.Ordinal) && text.Length > 5) return Unix(text[5..]);
        if (text is not null && text.StartsWith("npipe:", StringComparison.Ordinal) && text.Length > 6) return Pipe(text[6..]);
        throw new FormatException($"'{text}' is not a runner endpoint: write unix:/path/to/runner.sock or npipe:name.");
    }

    /// <summary>The endpoint and token a runner put in the environment of the task process.</summary>
    public static (RunnerEndpoint Endpoint, string Token) FromEnvironment()
    {
        var endpoint = Environment.GetEnvironmentVariable(RunnerProtocolNames.EndpointVariable);
        var token = Environment.GetEnvironmentVariable(RunnerProtocolNames.TokenVariable);
        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(token))
            throw new InvalidOperationException($"The runner's endpoint and run token are not in the environment: {RunnerProtocolNames.EndpointVariable} and {RunnerProtocolNames.TokenVariable} must be set by the runner that started this process.");
        return (Parse(endpoint), token);
    }

    /// <summary>A gRPC channel to the endpoint over cleartext HTTP/2 on the local socket. The caller disposes it.</summary>
    public GrpcChannel CreateChannel()
    {
        var endpoint = this;
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            ConnectCallback = async (_, cancellationToken) =>
            {
                if (endpoint.Kind == RunnerEndpointKind.UnixSocket)
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint.Address), cancellationToken).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }

                var pipe = new NamedPipeClientStream(".", endpoint.Address, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
                    return pipe;
                }
                catch
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            },
        };
        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = true,
            MaxReceiveMessageSize = 64 * 1024 * 1024,
            MaxSendMessageSize = 64 * 1024 * 1024,
        });
    }
}
