using System.Diagnostics;
using DbDataKit.Hosting.Children;

// Tiny helper child used by the Children tests. Commands: echo | printenv NAME | exit N | sleep FILE | spawn FILE | watch FILE | chatter N
var cmd = args.Length > 0 ? args[0] : "";
switch (cmd)
{
    case "echo":
    {
        var info = ChildEnvironment.TryRead() ?? throw new InvalidOperationException("no launch info");
        await using var stream = await ChildTransports.ConnectAsync(info);
        var buf = new byte[256];
        var n = await stream.ReadAsync(buf);
        var reply = System.Text.Encoding.UTF8.GetBytes("echo:" + System.Text.Encoding.UTF8.GetString(buf, 0, n));
        await stream.WriteAsync(reply);
        await stream.FlushAsync();
        return 0;
    }
    case "printenv":
        Console.Out.Write(Environment.GetEnvironmentVariable(args[1]) ?? "<null>");
        return 0;
    case "chatter":
        // Writes more than a pipe buffer holds to both streams, then reports done; blocks forever if nobody drains.
        for (var i = 0; i < int.Parse(args[1]); i++)
        {
            Console.Out.WriteLine("out line " + i);
            Console.Error.WriteLine("err line " + i);
        }

        return 0;
    case "exit":
        return int.Parse(args[1]);
    case "sleep":
        Publish(args[1], Environment.ProcessId.ToString());
        await Task.Delay(Timeout.Infinite);
        return 0;
    case "spawn":
    {
        var grand = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, [typeof(Program).Assembly.Location, "sleep", args[1] + ".grand"]) { UseShellExecute = false })!;
        Publish(args[1], Environment.ProcessId.ToString());
        await Task.Delay(Timeout.Infinite);
        return 0;
    }
    case "watch":
    {
        var info = ChildEnvironment.TryRead();
        var pid = int.Parse(Environment.GetEnvironmentVariable("WATCH_PID")!);
        var start = long.Parse(Environment.GetEnvironmentVariable("WATCH_START") ?? "0");
        _ = ParentDeathMonitor.Start(pid, start, () => Environment.Exit(42));
        Publish(args[1], "ready");
        await Task.Delay(Timeout.Infinite);
        return 0;
    }
    default:
        return 99;
}

// The tests wait for these files to exist and then read them. Written to a temporary name and moved into place, so that
// "exists" means "complete": a reader never meets a file the child still has open (which on Windows fails the read).
static void Publish(string path, string text)
{
    var temp = path + ".tmp";
    File.WriteAllText(temp, text);
    File.Move(temp, path, overwrite: true);
}
