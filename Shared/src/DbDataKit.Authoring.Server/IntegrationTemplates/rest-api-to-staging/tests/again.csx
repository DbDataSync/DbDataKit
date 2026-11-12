// Running the same pages again writes nothing: a page's key is in the table. And a 503 on the way is retried by the client, not by the job.
using DbDataKit.Monitoring;
using DbDataKit.Runner.Kit.Http;

var warehouse = new MemoryWarehouse();

async Task<RunSimulator<RestPull.PullOrders>> Run(RecordedHttp recorded)
{
    var api = new OutsideHttp(recorded.Client("https://api.example.test/v1/"), new OutsideHttpOptions { Delay = (_, _) => Task.CompletedTask });
    var sim = new RunSimulator<RestPull.PullOrders>(slots: (slot, _, _) => Task.FromResult<object>(slot == "api" ? api : warehouse.Gate("pull-orders")));
    await sim.RunAsync();
    return sim;
}

var first = await Run(RecordedHttp.FromFile(Path.Combine(Project, "tests", "recordings", "orders.json")));
True(first.Outcome == RunOutcome.Succeeded, "the first run did not succeed");
Equal(3L, warehouse.TotalRows);

// the API answers 503 once on the first page, then as recorded: the second run still succeeds and lands nothing new
var recording = HttpRecording.Load(Path.Combine(Project, "tests", "recordings", "orders.json"));
var flaky = new HttpRecording().Add("GET", "https://api.example.test/v1/orders", status: 503);
foreach (var entry in recording.Entries)
    flaky.Add(entry.Method, entry.Url, entry.Status, entry.Body);
var flakyApi = new RecordedHttp(flaky);
var second = await Run(flakyApi);
True(second.Outcome == RunOutcome.Succeeded, "the second run did not succeed: " + second.Failure?.Message);
Equal(0L, second.Counted(RestPull.PullOrdersCounters.Landed));
Equal(3L, second.Counted(RestPull.PullOrdersCounters.Items));
Equal(3L, warehouse.TotalRows);
flakyApi.Verify();
first.Dispose();
second.Dispose();
