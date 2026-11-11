// Helper child for the limits tests. Commands: idle | alloc MEGABYTES (touches that much memory in 8 MB steps, 50 ms apart, then idles).
var cmd = args.Length > 0 ? args[0] : "idle";
var held = new List<byte[]>();
if (cmd == "alloc")
{
    var megabytes = int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
    for (var done = 0; done < megabytes; done += 8)
    {
        var block = new byte[8 * 1024 * 1024];
        Array.Fill(block, (byte)1); // touch every page so it is resident
        held.Add(block);
        Console.Out.WriteLine($"held {done + 8}");
        Console.Out.Flush();
        Thread.Sleep(50);
    }
}

Console.Out.WriteLine("ready");
Console.Out.Flush();
GC.KeepAlive(held);
Thread.Sleep(TimeSpan.FromMinutes(2));
return 0;
