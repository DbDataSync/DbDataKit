using System.Text;
using DbDataKit.Monitoring;
using DbDataKit.Runner.Kit.Http;

namespace DbDataKit.Runner.Kit.Testing.Tests;

// Jobs under test against the outside world in memory (plan 27a IX15): an API read page by page from a recording, and a folder on a fake share.

public sealed record OrdersPullInput(RecordedHttp Http);

public sealed class OrdersProgress : Progress
{
    [State] public string? Cursor { get; set; }
}

public static class OrdersCounters
{
    public static readonly Counter Items = new("items");
}

/// <summary>Reads an API's orders with a cursor, counting them and keeping the cursor of the last page.</summary>
[RunnerTask("orders-pull")]
public sealed class OrdersPull : RunnerJob<OrdersPullInput, OrdersProgress, NoSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        using var http = new OutsideHttp(Input.Http.Client("https://api.example.test/v1/"), new OutsideHttpOptions { Delay = (_, _) => Task.CompletedTask });
        await foreach (var page in http.PagesAsync("orders", new CursorPaging { ItemsPath = "items" }, resumeFrom: Progress.Cursor, ct: ct))
        {
            Count(OrdersCounters.Items, page.Items.Count);
            Progress.Cursor = page.Resume;
        }
    }
}

public sealed record SharePullInput(FakeFileShare Share);

public static class ShareCounters
{
    public static readonly Counter Rows = new("rows");
}

/// <summary>Picks up the finished files of a folder once each (the file ledger), counts their lines and moves them aside.</summary>
[RunnerTask("share-pull")]
public sealed class SharePull : RunnerJob<SharePullInput, NoProgress, NoSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        var share = Input.Share;
        foreach (var file in await share.ListAsync("/drop", ct: ct))
        {
            if (!file.Complete) continue;                                              // a writer is still at it
            var claim = await Files.ClaimAsync("share", file.Path, file.Size, file.Modified, file.Hash!, ct);
            if (claim is null) continue;                                               // this content came before, under whatever name
            var text = Encoding.UTF8.GetString(await share.ReadAsync(file.Path, ct));
            Count(ShareCounters.Rows, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            await claim.CompleteAsync(ct: ct);
            await share.MoveAsync(file.Path, "/done" + file.Path["/drop".Length..], ct);
        }
    }
}

public class OutsideWorldTests
{
    private const string Page1 = """{"items":[{"id":1},{"id":2}],"next_cursor":"b"}""";
    private const string Page2 = """{"items":[{"id":3}],"next_cursor":null}""";

    private static HttpRecording Orders() => new HttpRecording()
        .Add("GET", "https://api.example.test/v1/orders", json: Page1)
        .Add("GET", "https://api.example.test/v1/orders?cursor=b", json: Page2);

    [Fact]
    public async Task A_job_reads_its_pages_from_a_recording_with_no_network()
    {
        var recorded = new RecordedHttp(Orders());
        using var sim = new RunSimulator<OrdersPull>(new OrdersPullInput(recorded));

        await sim.RunAsync();

        Assert.Equal(RunOutcome.Succeeded, sim.Outcome);
        Assert.Equal(3, sim.Counted(OrdersCounters.Items));
        Assert.Equal(["GET https://api.example.test/v1/orders", "GET https://api.example.test/v1/orders?cursor=b"], recorded.Served);
        recorded.Verify();
    }

    [Fact]
    public async Task A_request_with_no_recording_fails_the_test_naming_the_request_even_when_the_job_swallows_it()
    {
        var recording = new HttpRecording().Add("GET", "https://api.example.test/v1/orders", json: Page1);       // page 2 was never recorded
        var recorded = new RecordedHttp(recording);
        using var sim = new RunSimulator<OrdersPull>(new OrdersPullInput(recorded));

        await sim.RunAsync();

        Assert.NotEqual(RunOutcome.Succeeded, sim.Outcome);
        Assert.Equal(["GET https://api.example.test/v1/orders?cursor=b"], recorded.Misses);
        var failure = Assert.Throws<RecordingException>(() => recorded.Verify());
        Assert.Contains("No recording for GET https://api.example.test/v1/orders?cursor=b", failure.Message);
    }

    [Fact]
    public async Task An_entry_nobody_asked_for_fails_the_check_and_a_secret_in_a_query_is_not_printed()
    {
        var recorded = new RecordedHttp(Orders());
        Assert.Equal([1, 2], recorded.Unused);
        Assert.Contains("1, 2", Assert.Throws<RecordingException>(() => recorded.Verify()).Message);
        recorded.Verify(allUsed: false);

        using var client = recorded.Client("https://api.example.test/v1/");
        var ex = await Assert.ThrowsAsync<RecordingException>(() => client.GetAsync("things?api_key=key-canary-5521&page=9"));
        Assert.DoesNotContain("key-canary-5521", ex.Message);
        Assert.Contains("api_key=***", ex.Message);
        Assert.Contains("page=9", ex.Message);
    }

    [Fact]
    public async Task A_failing_answer_is_replayed_in_order_so_the_clients_retry_is_tested()
    {
        var recording = new HttpRecording()
            .Add("GET", "https://api.example.test/v1/orders", status: 503)
            .Add("GET", "https://api.example.test/v1/orders", json: Page2);
        var recorded = new RecordedHttp(recording);
        using var sim = new RunSimulator<OrdersPull>(new OrdersPullInput(recorded));

        await sim.RunAsync();

        Assert.Equal(RunOutcome.Succeeded, sim.Outcome);
        Assert.Equal(1, sim.Counted(OrdersCounters.Items));
        recorded.Verify();
    }

    [Fact]
    public async Task A_request_is_matched_on_its_body_and_a_repeat_entry_answers_every_time()
    {
        var recording = HttpRecording.Parse("""
            { "schema": "http-recording/1", "entries": [
              { "request": { "method": "POST", "url": "https://api.example.test/v1/orders", "json": { "id": 7, "qty": 2 } }, "response": { "status": 201, "json": { "ok": true } } },
              { "request": { "method": "GET", "url": "https://api.example.test/v1/ping?b=2&a=1" }, "response": { "status": 200, "body": "pong" }, "repeat": true } ] }
            """);
        var recorded = new RecordedHttp(recording);
        using var client = recorded.Client("https://api.example.test/v1/");

        Assert.Equal(System.Net.HttpStatusCode.Created, (await client.PostAsync("orders", new StringContent("""{"qty":2,  "id":7}""", Encoding.UTF8, "application/json"))).StatusCode);
        await Assert.ThrowsAsync<RecordingException>(() => client.PostAsync("orders", new StringContent("""{"id":8,"qty":2}""", Encoding.UTF8, "application/json")));
        Assert.Equal("pong", await client.GetStringAsync("ping?a=1&b=2"));
        Assert.Equal("pong", await client.GetStringAsync("ping?a=1&b=2"));
        Assert.Single(recorded.Misses);
    }

    [Fact]
    public void A_recording_file_round_trips_and_one_with_a_credential_in_it_is_refused_naming_the_entry()
    {
        var path = Path.Combine(Path.GetTempPath(), "http-recording-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, Orders().ToJson());
            var loaded = HttpRecording.Load(path);
            Assert.Equal(2, loaded.Entries.Count);
            Assert.Equal(Orders().ToJson(), loaded.ToJson());
            Assert.Equal(["GET https://api.example.test/v1/orders"], Served(new RecordedHttp(loaded)));
        }
        finally
        {
            File.Delete(path);
        }

        var header = Assert.Throws<RecordingException>(() => HttpRecording.Parse("""{"schema":"http-recording/1","entries":[{"request":{"url":"https://a.test/x"},"response":{"headers":{"Set-Cookie":"s=1"}}}]}""", "crm.json"));
        Assert.Contains("Entry 1 of the recording 'crm.json'", header.Message);
        Assert.Contains("Set-Cookie", header.Message);
        var query = Assert.Throws<RecordingException>(() => HttpRecording.Parse("""{"schema":"http-recording/1","entries":[{"request":{"url":"https://a.test/x"},"response":{}},{"request":{"url":"https://a.test/y?token=abc"},"response":{}}]}""", "crm.json"));
        Assert.Contains("Entry 2", query.Message);
        Assert.DoesNotContain("abc", query.Message);
        var old = Assert.Throws<RecordingException>(() => HttpRecording.Parse("""{"schema":"http-recording/2","entries":[]}""", "old.json"));
        Assert.Contains("'old.json'", old.Message);
        Assert.Contains("http-recording/2", old.Message);
    }

    private static IReadOnlyList<string> Served(RecordedHttp recorded)
    {
        using var client = recorded.Client();
        client.GetAsync("https://api.example.test/v1/orders").GetAwaiter().GetResult();
        return recorded.Served;
    }

    // ---- the share

    private static readonly DateTimeOffset Noon = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_file_that_is_still_being_written_is_not_picked_up_and_a_file_is_taken_once_whatever_it_is_called()
    {
        var share = new FakeFileShare(new FixedTime(Noon));
        share.Begin("/drop/a.csv", "id,1\n"u8.ToArray());
        using var sim = new RunSimulator<SharePull>(new SharePullInput(share));

        await sim.RunAsync();
        Assert.Equal(0, share.Calls("read"));                                           // listed, not read
        Assert.True(share.Exists("/drop/a.csv"));

        share.Finish("/drop/a.csv", "id,1\nid,2\n"u8.ToArray());
        await sim.RunAsync();
        Assert.Equal(2, sim.Counted(ShareCounters.Rows));
        Assert.Equal(["/done/a.csv"], share.Paths);

        share.Put("/drop/a-again.csv", "id,1\nid,2\n");                                  // the same bytes under another name
        await sim.RunAsync();
        Assert.Equal(2, sim.Counted(ShareCounters.Rows));                               // counted once in all attempts
        Assert.Equal(1, share.Calls("read"));
    }

    [Fact]
    public async Task A_read_that_fails_on_the_share_is_a_temporary_failure_and_the_retry_goes_through()
    {
        var share = new FakeFileShare(new FixedTime(Noon)).Put("/drop/b.csv", "x\ny\nz\n");
        share.FailNext("read", new IOException("the connection dropped"), times: 1, path: "/drop/b.csv");
        using var sim = new RunSimulator<SharePull>(new SharePullInput(share));

        await sim.RunAsync();
        Assert.NotEqual(RunOutcome.Succeeded, sim.Outcome);
        Assert.True(sim.Failure!.Retryable);

        await sim.RunAsync();
        Assert.Equal(RunOutcome.Succeeded, sim.Outcome);
        Assert.Equal(3, sim.Counted(ShareCounters.Rows));
        Assert.Equal(["list /drop", "read /drop/b.csv", "list /drop", "read /drop/b.csv", "move /drop/b.csv to /done/b.csv"], share.Log);
    }

    [Fact]
    public async Task A_missing_file_is_a_permanent_failure_and_the_listing_hides_nothing_about_size_and_time()
    {
        var share = new FakeFileShare(new FixedTime(Noon)).Put("/drop/c.csv", "1\n", Noon.AddHours(-1));
        var listed = Assert.Single(await share.ListAsync("/drop"));
        Assert.Equal((2L, Noon.AddHours(-1), true), (listed.Size, listed.Modified, listed.Complete));
        Assert.Equal(FakeFileShare.Hash("1\n"u8.ToArray()), listed.Hash);
        await Assert.ThrowsAsync<FileNotFoundException>(() => share.ReadAsync("/drop/none.csv"));
        await share.WriteAsync("out/report.csv", "r\n"u8.ToArray());
        Assert.Equal("r\n", share.Text("/out/report.csv"));
        Assert.Empty(await share.ListAsync("/drop/sub"));
        Assert.Single(await share.ListAsync("/", recursive: true), f => f.Path == "/out/report.csv");
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
