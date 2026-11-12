// A dry run: the API is read, nothing is written (the staging slot is never opened), and the log lists what would have been written.
using DbDataKit.Monitoring;
using DbDataKit.Runner.Kit.Http;

var recorded = RecordedHttp.FromFile(Path.Combine(Project, "tests", "recordings", "orders.json"));
var warehouse = new MemoryWarehouse();
var api = new OutsideHttp(recorded.Client("https://api.example.test/v1/"), new OutsideHttpOptions { Delay = (_, _) => Task.CompletedTask });
var opened = new List<string>();
var sim = new RunSimulator<RestPull.PullOrders>(dryRun: true, slots: (slot, _, _) => { opened.Add(slot); return Task.FromResult<object>(api); });
await sim.RunAsync();

True(sim.Outcome == RunOutcome.Succeeded, "the dry run did not succeed: " + sim.Failure?.Message);
Equal("api", string.Join(",", opened));                                                    // the database was not opened
Equal(0L, warehouse.TotalRows);
Equal(3L, sim.Counted(RestPull.PullOrdersCounters.Items));                                // it read every page
Equal(0L, sim.Counted(RestPull.PullOrdersCounters.Landed));
True(sim.Progress<RestPull.OrdersProgress>().Cursor is null, "a dry run keeps no progress");
True(sim.Log.Any(l => l.Message.StartsWith("Dry run, would: Insert", StringComparison.Ordinal)), "the log does not list the inserts that would have been made");
recorded.Verify();
sim.Dispose();
