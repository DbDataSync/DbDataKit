using System.Net;
using System.Net.Sockets;

namespace DbDataConsole.Tests;

/// <summary>
/// Ports for servers a test starts on a number it has to know before it starts them. A port the operating system just gave and took back (bind to 0) is in the range other connections take their own end from, so another test's
/// client can be holding it by the time the server binds; these come from below that range, are never given twice in the process, and are checked to be free.
/// </summary>
internal static class TestPorts
{
    private const int First = 20000;
    private const int Last = 30000;
    private static readonly object Gate = new();
    private static int next = First + Random.Shared.Next(Last - First);

    public static int Next()
    {
        lock (Gate)
        {
            for (var attempts = 0; attempts < Last - First; attempts++)
            {
                var port = next;
                next = next + 1 >= Last ? First : next + 1;
                if (IsFree(port)) return port;
            }
        }
        throw new InvalidOperationException("No free port between 20000 and 30000.");
    }

    private static bool IsFree(int port)
    {
        try
        {
            var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
            probe.Stop();
            return true;
        }
        catch (SocketException) { return false; }
    }
}
