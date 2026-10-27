using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DbDataKit.Semantic.Ssas;
using DbDataKit.Tools.XmlaForwarder;

namespace DbDataKit.Semantic.Tests;

/// <summary>
/// Plan 19, J20: records a session against a real Analysis Services with the XMLA forwarder (<c>DbDataKit/tools/DbDataKit.Tools.XmlaForwarder</c>). Not a test of
/// the engine and it does nothing unless <c>DBDATA_TEST_SSAS_RECORD</c> is set together with <c>DBDATA_TEST_SSAS</c> (the Windows job in <c>dbdatakit-ci.yml</c> when started by hand
/// with <c>ssas_record</c>): the forwarder listens on a free port in front of the instance, the ADOMD.NET client of <see cref="SsasExecutor"/> connects through it
/// and runs a few DAX queries, and the folder named by <c>DBDATA_XMLA_RECORDING_DIR</c> (default <c>xmla-recording</c>) holds the recording for the
/// workflow to upload. What it asserts is that bytes crossed in both directions on at least one connection and that the queries returned what the model holds;
/// whether the XML of the messages is readable in the recording (the protocol may compress it) is written to the output, not asserted.
/// </summary>
public class SsasRecordingTests
{
    private static readonly string? Ssas = Environment.GetEnvironmentVariable("DBDATA_TEST_SSAS");

    [Fact]
    public async Task ASessionThroughTheForwarderIsRecorded()
    {
        if (Ssas is null || Environment.GetEnvironmentVariable("DBDATA_TEST_SSAS_RECORD") is null) return;
        var instance = Regex.Match(Ssas, @"Data Source=([^:;\\]+):(\d+)", RegexOptions.IgnoreCase);
        Assert.True(instance.Success, $"DBDATA_TEST_SSAS must name the instance by host and port (Data Source=host:port); it is '{Ssas}'.");
        var folder = Environment.GetEnvironmentVariable("DBDATA_XMLA_RECORDING_DIR") is { Length: > 0 } dir ? dir : "xmla-recording";

        // the model is deployed straight to the server, as the conformance run does; only the queries go through the forwarder
        SsasExecutor.DeployModel(Ssas, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "conformance", "ssas", "model.tmsl.json")));

        string version, budget;
        int connections;
        using (var recorder = new SessionRecorder(folder))
        {
            var forwarder = TcpForwarder.Start(IPAddress.Loopback, 0, instance.Groups[1].Value, int.Parse(instance.Groups[2].Value), recorder);
            try
            {
                var through = $"Data Source=localhost:{forwarder.Port};Catalog=qis_budget";
                version = SsasExecutor.Probe(through, "EVALUATE SELECTCOLUMNS ( INFO.VIEW.TABLES (), \"n\", [Name] )");
                budget = SsasExecutor.Probe(through, "EVALUATE SUMMARIZECOLUMNS ( 'Customer'[Region], \"b\", [Budget Amount] )");
            }
            finally
            {
                // ADOMD may keep its connection in a pool, so the forwarder is stopped (which closes the connections and writes the text views) before the files are read
                await forwarder.DisposeAsync();
            }
            connections = recorder.Connections;
        }

        Assert.DoesNotContain("ERROR", version);
        Assert.DoesNotContain("ERROR", budget);
        Assert.True(connections >= 1, "no connection came through the forwarder");
        long client = 0, server = 0;
        var readable = new List<string>();
        for (var n = 1; n <= connections; n++)
        {
            var c = File.ReadAllBytes(Path.Combine(folder, SessionRecorder.StreamFile(n, Direction.Client)));
            var s = File.ReadAllBytes(Path.Combine(folder, SessionRecorder.StreamFile(n, Direction.Server)));
            (client, server) = (client + c.Length, server + s.Length);
            readable.AddRange(StreamText.Runs(c).Where(r => r.Contains("EVALUATE", StringComparison.OrdinalIgnoreCase)));
        }
        Assert.True(client > 0 && server > 0, $"expected bytes both ways, got {client} from the client and {server} from the server");
        var summary = new StringBuilder()
            .Append($"recorded {connections} connection(s), {client} bytes from the client, {server} bytes from the server, in {Path.GetFullPath(folder)}\n")
            .Append(readable.Count > 0 ? $"the DAX of a request is readable in the client stream ({readable.Count} run(s))" : "no DAX text found in the client stream: the messages are probably compressed or framed in a way the text view does not read");
        Console.WriteLine(summary);
        File.WriteAllText(Path.Combine(folder, "summary.txt"), summary.ToString());
    }
}
