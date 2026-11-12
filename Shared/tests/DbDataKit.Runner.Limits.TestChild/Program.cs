// Helper child for the limits tests. Commands: idle | grandchild MEGABYTES (starts a child of its own that allocates, prints its pid as `grandchild <pid>` and stays small itself) | alloc MEGABYTES (touches that much memory in 8 MB steps, 50 ms apart, then idles).
var cmd = args.Length > 0 ? args[0] : "idle";
var held = new List<byte[]>();
if (cmd == "grandchild")
{
    var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
    start.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("alloc");
    start.ArgumentList.Add(args[1]);
    var grandchild = System.Diagnostics.Process.Start(start)!;
    Console.Out.WriteLine($"grandchild {grandchild.Id}");
    Console.Out.Flush();
}

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
