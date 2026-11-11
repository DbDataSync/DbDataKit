using System.Net;
using DbDataKit.Tools.XmlaForwarder;

const string Usage = """
    dbdata-xmla-forwarder: records what clients and an Analysis Services server say to each other.

      dbdata-xmla-forwarder --target <host:port> --out <folder> [--listen <port>] [--bind <address>] [--max-seconds <n>]

      --target       the Analysis Services instance (for example localhost:2383)
      --out          the folder the recording is written to (created if it is missing)
      --listen       the port to listen on (default 2384; 0 chooses a free one and prints it)
      --bind         the address to listen on (default 127.0.0.1; use 0.0.0.0 to record a client on another machine)
      --max-seconds  stop after this long (default: run until Ctrl+C)

    Point the client (Excel's connection dialog, an ADOMD connection string) at <this machine>:<listen port> instead of the server. The traffic is copied
    unchanged. The recording holds everything that was sent, credentials included: keep it private.
    """;

string? target = null, output = null, bind = "127.0.0.1";
var listen = 2384;
int? maxSeconds = null;
for (var i = 0; i < args.Length; i++)
{
    string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    try
    {
        switch (args[i])
        {
            case "--target": target = Value(); break;
            case "--out": output = Value(); break;
            case "--listen": listen = int.Parse(Value()); break;
            case "--bind": bind = Value(); break;
            case "--max-seconds": maxSeconds = int.Parse(Value()); break;
            case "-h" or "--help": Console.WriteLine(Usage); return 0;
            default: throw new ArgumentException($"unknown option {args[i]}");
        }
    }
    catch (Exception e) when (e is ArgumentException or FormatException)
    {
        Console.Error.WriteLine(e.Message + "\n\n" + Usage);
        return 2;
    }
}

if (target is null || output is null || !TargetParser.TryParse(target, out var host, out var port) || !IPAddress.TryParse(bind, out var address))
{
    Console.Error.WriteLine("--target (host:port) and --out are required, and --bind must be an IP address.\n\n" + Usage);
    return 2;
}

using var recorder = new SessionRecorder(output);
await using var forwarder = TcpForwarder.Start(address, listen, host, port, recorder);
Console.WriteLine($"Forwarding {address}:{forwarder.Port} to {host}:{port}; recording to {recorder.Folder}. Ctrl+C stops.");

using var done = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Cancel(); };
if (maxSeconds is { } seconds) done.CancelAfter(TimeSpan.FromSeconds(seconds));
try { await Task.Delay(Timeout.Infinite, done.Token); } catch (OperationCanceledException) { /* asked to stop */ }

await forwarder.DisposeAsync();
Console.WriteLine($"Stopped: {recorder.Connections} connection(s) recorded in {recorder.Folder}.");
return 0;
