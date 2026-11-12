# DbDataKit.Runner.Kit.Http

The HTTP client of an integration (plan 27a IX10). It sits on the signed-in `HttpClient` of a connection of kind `http` (`DbDataKit.Connections.Http`: API key, basic, OAuth 2 client credentials, client certificate; secrets are references) and adds what a job that pulls from or pushes to an API needs, so none writes it again.

```csharp
using var http = OutsideHttp.Open(definition, secrets, new OutsideHttpOptions { Limit = (5, TimeSpan.FromSeconds(1)) });   // the one place the client is made
// from a connection file: OutsideHttp.OpenYaml(yaml, "connections/crm.yml", secrets) (the typed loader of HTTP connections; a file of another kind is refused)

await foreach (var page in http.PagesAsync("orders", new CursorPaging { ItemsPath = "items" }, resumeFrom: Progress.OrdersCursor))
{
    foreach (var item in page.Items) { /* ... */ }
    Progress.OrdersCursor = page.Resume;          // a [State] member: the next run continues where this one stopped
}
```

A test, or any code that has its own client, uses `new OutsideHttp(httpClient, options)`.

## What a call does

1. **Waits for the rate limit** of its connection: `Limit = (permits, period)` is shared by every task of the process that uses the connection (`RateLimits`, keyed by the connection's name), never refused, only delayed. The runner's concurrency limit per connection (plan 28) is what spans processes.
2. **Sends**, and classifies what went wrong with the connection's own classification (`HttpFailures`, the `ConnectionFailure` a database gives too), so the runner's retry reads it: 429 rate limited, 5xx unavailable, 408, a network failure and a timeout are **temporary**; 401, 403, 404, 410 and the other 4xx are **permanent**.
3. **Retries a temporary failure inside the call** (4 attempts in all by default, back-off 0.5 s, 1 s, 2 s). A **429 waits as `Retry-After` says** (seconds or a date) and holds the whole connection while it waits (`RateLimit.Hold`: the answer is about the system, not about one call). A wait longer than `MaxWait` (2 minutes) is not waited for: the call fails as temporary at once and the runner's retry of the task, on its own schedule, comes back later. What still fails after the attempts is thrown as `ConnectionFailureException` (`IsTemporary` true, `TaskFailure.From(...).Retryable` true).
4. **Is careful with what it repeats.** GET, HEAD, OPTIONS, PUT and DELETE are repeated; a POST or PATCH only when it carries an idempotency key (`SendJsonAsync(..., idempotencyKey:)` sends it in the `Idempotency-Key` header and the same key on every try) or `RetryUnsafeMethods` says so. A repeated create is a duplicate.
5. **Never shows what it should not.** A failure carries the fixed text and the code `http-<status>`: no body, header, address or secret.

## Paging

`PagesAsync(path, style, query, resumeFrom)` gives `ApiPage`s **one request at a time, as the caller asks** (a caller that stops after the first page has made one request); `ItemsAsync` flattens them. Styles: `OffsetPaging` (`offset` and `limit`, the last page is the short one), `CursorPaging` (the cursor in the body at a dotted path, the last page has none), `LinkHeaderPaging` (`rel="next"`; **a link to another origin is refused**, so the connection's credentials never go anywhere else). `ApiPage.Resume` is what to give as `resumeFrom` to continue after that page: keep it as a `[State]` member of the job's progress. A page that is not the expected array is an `ApiShapeException` (permanent, no part of the body in the message).

## Bodies and downloads

`SendAsync`/`GetAsync` read the response whole up to `MaxBufferedBytes` (64 MiB; a larger one is refused with the advice to stream); `OpenReadAsync` gives a download as a stream, classified and retried until the headers are in, for a streaming step (`DbDataKit.Runner.Kit`) and the readers of `DbDataKit.Runner.Kit.Formats`.

## Dry run

`OutsideHttpOptions.DryRun` (a job gives `Run.DryRun`) makes the client send **reads only** (GET, HEAD, OPTIONS): any other request is not sent, is added to `OutsideHttp.WouldSend` and told to `OnWouldSend` (a job gives `Run.WouldDo`) as one line with the method, the address **without its query** (a query can carry a key), the size of the body and the idempotency key, never the body or a header, and is answered with 204 and no body. A download opened with `OpenReadAsync` is a GET and goes out. A job that sends through a client of its own is not guarded.
