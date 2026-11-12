// A dry run: the file is read and its rows are counted, nothing is written, the file stays in /drop, and the log lists what would have been done.
using System.Threading;
using DbDataKit.Monitoring;

var share = new FakeFileShare();
share.Put("/drop/customers-1.csv", "id,name,email,joined\n1,Ada,ada@example.test,2026-01-05\n");
var adapter = new Adapter(share);
var opened = new List<string>();
var sim = new RunSimulator<CsvPickup.PickUpCsv>(dryRun: true, slots: (slot, _, _) => { opened.Add(slot); return Task.FromResult<object>(adapter); });
await sim.RunAsync();

True(sim.Outcome == RunOutcome.Succeeded, "the dry run did not succeed: " + sim.Failure?.Message);
Equal("share", string.Join(",", opened));                                                  // the database was not opened
True(share.Exists("/drop/customers-1.csv") && !share.Exists("/done/customers-1.csv"), "a dry run must leave the file where it is");
True(sim.Log.Any(l => l.Message.StartsWith("Dry run, would: Insert", StringComparison.Ordinal)), "the log does not list the insert that would have been made");
True(sim.Log.Any(l => l.Message.Contains("move /drop/customers-1.csv", StringComparison.Ordinal)), "the log does not list the move");
sim.Dispose();

class Adapter(FakeFileShare fake) : CsvPickup.IShare
{
    public async Task<IReadOnlyList<CsvPickup.ShareFile>> ListAsync(string folder, CancellationToken ct) =>
        [.. (await fake.ListAsync(folder, ct: ct)).Select(f => new CsvPickup.ShareFile(f.Path, f.Size, f.Modified, f.Complete, f.Hash))];

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct) => fake.OpenReadAsync(path, ct);

    public Task MoveAsync(string from, string to, CancellationToken ct) => fake.MoveAsync(from, to, ct);
}
