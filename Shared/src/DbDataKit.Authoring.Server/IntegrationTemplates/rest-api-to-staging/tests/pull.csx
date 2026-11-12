// The job against the recorded answers of the API (tests/recordings/orders.json) and a staging table in memory: no network, no database.
// A test is a script: it passes when it finishes and fails when it throws.
using DbDataKit.Monitoring;
using DbDataKit.Runner.Kit.Http;

var recorded = RecordedHttp.FromFile(Path.Combine(Project, "tests", "recordings", "orders.json"));
var warehouse = new MemoryWarehouse();
var api = new OutsideHttp(recorded.Client("https://api.example.test/v1/"), new OutsideHttpOptions { Delay = (_, _) => Task.CompletedTask });

// the slots open to what the runner would bind; here, a client over the recording and a gate over the memory table
var sim = new RunSimulator<RestPull.PullOrders>(slots: (slot, _, _) => Task.FromResult<object>(slot == "api" ? api : warehouse.Gate("pull-orders")));
await sim.RunAsync();

True(sim.Outcome == RunOutcome.Succeeded, "the run did not succeed: " + sim.Failure?.Message);
Equal(2L, sim.Counted(RestPull.PullOrdersCounters.Pages));
Equal(3L, sim.Counted(RestPull.PullOrdersCounters.Items));
Equal(3L, sim.Counted(RestPull.PullOrdersCounters.Landed));
Equal(3L, warehouse.TotalRows);
Equal(2L, warehouse.RowsUnder("orders-first"));                     // a page's key is the cursor it was asked with
Equal(1L, warehouse.RowsUnder("orders-p2"));
recorded.Verify();                                                    // every request had a recorded answer and every answer was used
sim.Dispose();
