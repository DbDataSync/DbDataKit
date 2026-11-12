// The job against a fake share (a folder in memory that an outside system fills) and a staging table in memory: no share, no database.
using System.Threading;
using System.Text;
using DbDataKit.Monitoring;

const string Header = "id,name,email,joined\n";

var share = new FakeFileShare();
var warehouse = new MemoryWarehouse();
var adapter = new Adapter(share);

// one simulator is one chain of attempts: the file ledger it keeps is what makes a file with known content run once, so every run below is an attempt of it
var sim = new RunSimulator<CsvPickup.PickUpCsv>(slots: (slot, _, _) => Task.FromResult<object>(slot == "share" ? adapter : warehouse.Gate("pick-up-csv")));

// 1. a finished file is landed once and moved aside; a file its writer has not finished is left alone
share.Put("/drop/customers-1.csv", Header + "1,Ada,ada@example.test,2026-01-05\n2,Bob,bob@example.test,2026-02-11\n");
share.Begin("/drop/customers-2.csv", Encoding.UTF8.GetBytes(Header + "3,Cy,"));
await sim.RunAsync();
True(sim.Outcome == RunOutcome.Succeeded, "the first run did not succeed: " + sim.Failure?.Message);
Equal(1L, sim.CountedIn(CsvPickup.PickUpCsvCounters.Files, 1));
Equal(2L, sim.CountedIn(CsvPickup.PickUpCsvCounters.Rows, 1));
Equal(2L, warehouse.TotalRows);
True(share.Exists("/done/customers-1.csv") && !share.Exists("/drop/customers-1.csv"), "the landed file was not moved to /done");
True(share.Exists("/drop/customers-2.csv"), "a file still being written must stay where it is");

// 2. the writer finishes, and the same content under another name arrives too: each content is landed once
share.Finish("/drop/customers-2.csv", Encoding.UTF8.GetBytes(Header + "3,Cy,cy@example.test,2026-03-01\n"));
share.Put("/drop/customers-1-copy.csv", Header + "1,Ada,ada@example.test,2026-01-05\n2,Bob,bob@example.test,2026-02-11\n");
await sim.RunAsync();
True(sim.Outcome == RunOutcome.Succeeded, "the second run did not succeed: " + sim.Failure?.Message);
Equal(1L, sim.CountedIn(CsvPickup.PickUpCsvCounters.Files, 2));
Equal(1L, sim.CountedIn(CsvPickup.PickUpCsvCounters.Skipped, 2));
Equal(3L, warehouse.TotalRows);

// 3. a record that does not fit its column is collected, never landed, and never printed
share.Put("/drop/customers-3.csv", Header + "4,Di,di@example.test,not-a-date\n5,Ed,ed@example.test,2026-04-01\n");
await sim.RunAsync();
True(sim.Outcome == RunOutcome.Succeeded, "the third run did not succeed: " + sim.Failure?.Message);
Equal(1L, sim.CountedIn(CsvPickup.PickUpCsvCounters.Rejected, 3));
Equal(4L, warehouse.TotalRows);
sim.Dispose();

class Adapter(FakeFileShare fake) : CsvPickup.IShare
{
    public async Task<IReadOnlyList<CsvPickup.ShareFile>> ListAsync(string folder, CancellationToken ct) =>
        [.. (await fake.ListAsync(folder, ct: ct)).Select(f => new CsvPickup.ShareFile(f.Path, f.Size, f.Modified, f.Complete, f.Hash))];

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct) => fake.OpenReadAsync(path, ct);

    public Task MoveAsync(string from, string to, CancellationToken ct) => fake.MoveAsync(from, to, ct);
}
