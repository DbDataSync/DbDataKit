using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DbDataKit.Connections;
using DbDataKit.Runner;
using DbDataKit.Connections.Http;
using DbDataKit.Runner.Kit.Http;
using DbDataKit.Secrets;
using Microsoft.Extensions.Time.Testing;

namespace DbDataKit.Runner.Kit.Http.Tests;

/// <summary>A fake API: answers from a script (a function of the request and its number), keeps the requests it saw.</summary>
internal sealed class FakeApi(Func<HttpRequestMessage, int, HttpResponseMessage> script) : HttpMessageHandler
{
    public List<(HttpMethod Method, string Url, string? Body, IReadOnlyDictionary<string, string> Headers)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        Requests.Add((request.Method, request.RequestUri!.PathAndQuery, body, headers));
        return script(request, Requests.Count);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public class OutsideHttpTests
{
    private static OutsideHttp Client(FakeApi api, List<TimeSpan>? waits = null, FakeTimeProvider? time = null, OutsideHttpOptions? options = null)
    {
        time ??= new FakeTimeProvider(new DateTimeOffset(2026, 11, 11, 10, 0, 0, TimeSpan.Zero));
        var o = (options ?? new OutsideHttpOptions()) with
        {
            Time = time,
            Delay = (d, _) => { waits?.Add(d); time.Advance(d); return Task.CompletedTask; },
        };
        return new OutsideHttp(new HttpClient(api) { BaseAddress = new Uri("https://api.example.test/v1/") }, o);
    }

    [Fact]
    public async Task A_connection_of_kind_http_signs_every_request_in_and_a_failure_never_holds_the_key()
    {
        const string key = "api-key-canary-7c1d";
        var yaml = "schema: connection/2\nkind: http\nname: crm\nendpoint: https://crm.example.test/api\nsign_in: { method: api_key, key_secret: env:CRM_KEY }\n";
        var loaded = ConnectionDefinitionLoader.Load(yaml, "connections/crm.yml", outsideKinds: true);
        Assert.True(loaded.Ok, string.Join("\n", loaded.Diagnostics));
        var api = new FakeApi((request, n) => n == 1 ? FakeApi.Json("""{"ok":true}""") : new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(key) });
        var secrets = new SecretResolverChain([new EnvSecretProvider(name => name == "CRM_KEY" ? key : null)]);
        using var http = OutsideHttp.Open(loaded.Definition!, secrets, new OutsideHttpOptions { Delay = (_, _) => Task.CompletedTask }, new HttpConnectionOptions(api));
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("contacts")).Status);
        Assert.Equal(key, api.Requests[0].Headers["X-Api-Key"]);
        Assert.Equal("/api/contacts", api.Requests[0].Url);
        var ex = await Assert.ThrowsAsync<ConnectionFailureException>(() => http.GetAsync("contacts"));
        Assert.DoesNotContain(key, ex.Message);
        Assert.False(ex.Failure.IsTemporary);
    }

    [Fact]
    public async Task Three_pages_of_a_cursor_api_are_read_one_request_at_a_time()
    {
        var api = new FakeApi((request, n) => FakeApi.Json(n switch
        {
            1 => """{"items":[{"id":1},{"id":2}],"next_cursor":"abc"}""",
            2 => """{"items":[{"id":3},{"id":4}],"next_cursor":"def"}""",
            _ => """{"items":[{"id":5}],"next_cursor":null}""",
        }));
        using var http = Client(api);
        await using var pages = http.PagesAsync("orders", new CursorPaging { ItemsPath = "items" }).GetAsyncEnumerator();

        Assert.True(await pages.MoveNextAsync());
        Assert.Single(api.Requests);                                   // incremental: nothing was fetched ahead
        Assert.Equal([1L, 2L], pages.Current.Items.Select(i => i.GetProperty("id").GetInt64()).ToArray());
        Assert.Equal("abc", pages.Current.Resume);
        Assert.True(await pages.MoveNextAsync());
        Assert.Equal(2, api.Requests.Count);
        Assert.Contains("cursor=abc", api.Requests[1].Url);
        Assert.True(await pages.MoveNextAsync());
        Assert.Null(pages.Current.Resume);
        Assert.Equal(3, pages.Current.Number);
        Assert.False(await pages.MoveNextAsync());
        Assert.Equal(3, api.Requests.Count);
    }

    [Fact]
    public async Task A_run_resumes_from_the_cursor_it_kept()
    {
        var api = new FakeApi((request, n) => FakeApi.Json("""{"items":[{"id":9}],"next_cursor":null}"""));
        using var http = Client(api);
        var items = await http.ItemsAsync("orders", new CursorPaging { ItemsPath = "items" }, resumeFrom: "kept-cursor").ToListAsync();
        Assert.Single(items);
        Assert.Contains("cursor=kept-cursor", api.Requests.Single().Url);
    }

    [Fact]
    public async Task Offset_paging_stops_at_a_short_page_and_offers_the_next_offset_to_resume_from()
    {
        var api = new FakeApi((request, n) => FakeApi.Json(n == 1 ? "[1,2]" : "[3]"));
        using var http = Client(api);
        var pages = await http.PagesAsync("rows", new OffsetPaging { PageSize = 2 }, new Dictionary<string, string?> { ["kind"] = "a b" }).ToListAsync();
        Assert.Equal(2, pages.Count);
        Assert.Equal("2", pages[0].Resume);
        Assert.Null(pages[1].Resume);
        Assert.Contains("offset=0", api.Requests[0].Url);
        Assert.Contains("limit=2", api.Requests[0].Url);
        Assert.Contains("kind=a%20b", api.Requests[0].Url);
        Assert.Contains("offset=2", api.Requests[1].Url);
    }

    [Fact]
    public async Task A_link_header_is_followed_and_a_link_to_another_origin_is_refused()
    {
        var api = new FakeApi((request, n) =>
        {
            var response = FakeApi.Json(n == 1 ? """{"data":[1]}""" : """{"data":[2]}""");
            if (n == 1) response.Headers.TryAddWithoutValidation("Link", "<https://api.example.test/v1/orders?page=2>; rel=\"next\", <https://api.example.test/v1/orders?page=9>; rel=\"last\"");
            return response;
        });
        using var http = Client(api);
        var items = await http.ItemsAsync("orders", new LinkHeaderPaging { ItemsPath = "data" }).ToListAsync();
        Assert.Equal(2, items.Count);
        Assert.Equal("/v1/orders?page=2", api.Requests[1].Url);

        var evil = new FakeApi((request, n) =>
        {
            var response = FakeApi.Json("""{"data":[1]}""");
            response.Headers.TryAddWithoutValidation("Link", "<https://elsewhere.example.test/steal>; rel=next");
            return response;
        });
        using var guarded = Client(evil);
        var ex = await Assert.ThrowsAsync<ConnectionFailureException>(() => guarded.ItemsAsync("orders", new LinkHeaderPaging { ItemsPath = "data" }).ToListAsync());
        Assert.False(ex.Failure.IsTemporary);
        Assert.Single(evil.Requests);
    }

    [Fact]
    public async Task A_page_that_is_not_an_array_is_a_permanent_shape_failure_without_the_body()
    {
        var api = new FakeApi((request, n) => FakeApi.Json("""{"items":"secret-looking-text"}"""));
        using var http = Client(api);
        var ex = await Assert.ThrowsAsync<ApiShapeException>(() => http.ItemsAsync("x", new CursorPaging { ItemsPath = "items" }).ToListAsync());
        Assert.DoesNotContain("secret-looking-text", ex.Message);
        Assert.False(ex.IsTemporary);
    }

    [Fact]
    public async Task A_429_waits_as_the_retry_after_header_says_and_then_goes_on()
    {
        var api = new FakeApi((request, n) =>
        {
            if (n > 1) return FakeApi.Json("""{"ok":true}""");
            var limited = new HttpResponseMessage((HttpStatusCode)429);
            limited.Headers.TryAddWithoutValidation("Retry-After", "3");
            return limited;
        });
        var waits = new List<TimeSpan>();
        using var http = Client(api, waits);
        var response = await http.GetAsync("x");
        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal([TimeSpan.FromSeconds(3)], waits);
        Assert.Equal(2, api.Requests.Count);
    }

    [Fact]
    public async Task A_retry_after_date_is_read_against_the_clock()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 11, 10, 0, 0, TimeSpan.Zero));
        var api = new FakeApi((request, n) =>
        {
            if (n > 1) return FakeApi.Json("{}");
            var limited = new HttpResponseMessage((HttpStatusCode)429);
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(time.GetUtcNow().AddSeconds(7));
            return limited;
        });
        var waits = new List<TimeSpan>();
        using var http = Client(api, waits, time);
        await http.GetAsync("x");
        Assert.Equal([TimeSpan.FromSeconds(7)], waits);
    }

    [Fact]
    public async Task A_wait_longer_than_the_limit_is_not_waited_for_and_fails_as_temporary_for_the_runners_retry()
    {
        var api = new FakeApi((request, n) =>
        {
            var limited = new HttpResponseMessage((HttpStatusCode)429);
            limited.Headers.TryAddWithoutValidation("Retry-After", "3600");
            return limited;
        });
        var waits = new List<TimeSpan>();
        using var http = Client(api, waits);
        var ex = await Assert.ThrowsAsync<ConnectionFailureException>(() => http.GetAsync("x"));
        Assert.True(ex.Failure.IsTemporary);
        Assert.Equal(ConnectionFailureKind.RateLimited, ex.Failure.Kind);
        Assert.Empty(waits);
        Assert.Single(api.Requests);
        Assert.True(TaskFailure.From(ex.Failure).Retryable);
    }

    [Fact]
    public async Task A_500_is_retried_with_a_back_off_and_goes_on_when_the_system_recovers()
    {
        var api = new FakeApi((request, n) => n < 3 ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("stack trace with secret-token") } : FakeApi.Json("""{"ok":true}"""));
        var waits = new List<TimeSpan>();
        using var http = Client(api, waits);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("x")).Status);
        Assert.Equal([TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1)], waits);
        Assert.Equal(3, api.Requests.Count);
    }

    [Fact]
    public async Task A_500_that_does_not_recover_ends_as_a_temporary_failure_with_no_part_of_the_response()
    {
        var api = new FakeApi((request, n) => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("secret-token in the body") });
        using var http = Client(api, options: new OutsideHttpOptions { MaxAttempts = 3 });
        var ex = await Assert.ThrowsAsync<ConnectionFailureException>(() => http.GetAsync("x"));
        Assert.Equal(3, api.Requests.Count);
        Assert.True(ex.Failure.IsTemporary);
        Assert.Contains("http-502", ex.Message);
        Assert.DoesNotContain("secret-token", ex.Message);
        Assert.Equal(TaskFailureKind.Temporary, TaskFailure.From(ex.Failure).Kind);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task A_client_error_is_permanent_and_is_sent_once(HttpStatusCode status)
    {
        var api = new FakeApi((request, n) => new HttpResponseMessage(status));
        var waits = new List<TimeSpan>();
        using var http = Client(api, waits);
        var ex = await Assert.ThrowsAsync<ConnectionFailureException>(() => http.GetAsync("missing"));
        Assert.False(ex.Failure.IsTemporary);
        Assert.Single(api.Requests);
        Assert.Empty(waits);
        Assert.Equal(TaskFailureKind.Permanent, TaskFailure.From(ex.Failure).Kind);
    }

    [Fact]
    public async Task A_network_failure_is_retried_and_classified_as_the_network()
    {
        var api = new FakeApi((request, n) => throw new HttpRequestException("down", new SocketException((int)SocketError.ConnectionRefused)));
        using var http = Client(api, options: new OutsideHttpOptions { MaxAttempts = 2 });
        var ex = await Assert.ThrowsAsync<ConnectionFailureException>(() => http.GetAsync("x"));
        Assert.Equal(ConnectionFailureKind.Network, ex.Failure.Kind);
        Assert.Equal(2, api.Requests.Count);
    }

    [Fact]
    public async Task A_post_is_sent_again_only_with_an_idempotency_key_and_the_key_is_the_same()
    {
        var api = new FakeApi((request, n) => n == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : FakeApi.Json("""{"id":1}"""));
        using var http = Client(api);
        await Assert.ThrowsAsync<ConnectionFailureException>(() => http.SendJsonAsync(HttpMethod.Post, "tickets", new { title = "a" }));
        Assert.Single(api.Requests);                                   // no key: a repeated create would be a duplicate

        var keyed = new FakeApi((request, n) => n == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : FakeApi.Json("""{"id":1}"""));
        using var http2 = Client(keyed);
        var response = await http2.SendJsonAsync(HttpMethod.Post, "tickets", new { title = "a" }, idempotencyKey: "run-1/ticket-7");
        Assert.Equal(1, response.Json().GetProperty("id").GetInt32());
        Assert.Equal(2, keyed.Requests.Count);
        Assert.All(keyed.Requests, r => Assert.Equal("run-1/ticket-7", r.Headers["Idempotency-Key"]));
        Assert.All(keyed.Requests, r => Assert.Equal("""{"title":"a"}""", r.Body));   // the body is made again for each try
    }

    [Fact]
    public async Task A_rate_limit_makes_the_third_request_wait_for_the_period_and_a_429_holds_every_caller()
    {
        var api = new FakeApi((request, n) => n == 3 ? new HttpResponseMessage((HttpStatusCode)429) { Headers = { { "Retry-After", "10" } } } : FakeApi.Json("{}"));
        var waits = new List<TimeSpan>();
        var options = new OutsideHttpOptions { Connection = "limited-" + Guid.NewGuid(), Limit = (2, TimeSpan.FromSeconds(1)) };
        using var http = Client(api, waits, options: options);
        await http.GetAsync("a");
        await http.GetAsync("b");
        Assert.Empty(waits);
        await http.GetAsync("c");                                      // the third in the same second: waits out the period, then meets the 429 and waits 10 s as it says, then goes on
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10)], waits.Take(2).ToArray());
        Assert.Equal(4, api.Requests.Count);
    }

    [Fact]
    public async Task A_download_is_a_stream_classified_before_its_body()
    {
        var api = new FakeApi((request, n) => n == 1 ? new HttpResponseMessage(HttpStatusCode.BadGateway) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("file content"u8.ToArray()) });
        using var http = Client(api);
        await using var stream = await http.OpenReadAsync("files/1");
        using var reader = new StreamReader(stream);
        Assert.Equal("file content", await reader.ReadToEndAsync());
        Assert.Equal(2, api.Requests.Count);
    }

    [Fact]
    public async Task A_response_larger_than_the_buffer_limit_is_refused_and_asked_to_be_streamed()
    {
        var api = new FakeApi((request, n) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2048]) });
        using var http = Client(api, options: new OutsideHttpOptions { MaxBufferedBytes = 1024 });
        var ex = await Assert.ThrowsAsync<ApiShapeException>(() => http.GetAsync("big"));
        Assert.Contains("OpenReadAsync", ex.Message);
    }
}

internal static class AsyncExtensions
{
    public static async Task<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source) list.Add(item);
        return list;
    }
}
