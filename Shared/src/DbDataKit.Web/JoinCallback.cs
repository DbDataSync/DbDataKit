using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace DbDataKit.Web.Trust;

/// <summary>
/// The console's callback over HTTPS: it calls the joining server's <see cref="ManagementApi.JoinCallbackPath"/> with the certificate the join named
/// pinned, and accepts the answer only when it is signed with the key the join named, over this join's id, this console and the address it reached.
/// </summary>
public sealed class HttpJoinCallback(TimeSpan? timeout = null) : IJoinCallback
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<string?> ConfirmAsync(JoinCallbackCall call, CancellationToken ct)
    {
        var seen = "";
        using var http = new HttpClient(PairingClient.PinnedSocket(call.CertificateFingerprint, f => seen = f)) { Timeout = timeout ?? TimeSpan.FromSeconds(15) };
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(new Uri(call.Address, ManagementApi.JoinCallbackPath),
                new JoinCallbackRequest(call.JoinId, call.ConsoleId, Thumbprints.Format(call.ConsoleFingerprint)), Json, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return seen.Length > 0 && seen != call.CertificateFingerprint
                ? $"it presented the certificate {Thumbprints.Format(seen)}, not {Thumbprints.Format(call.CertificateFingerprint)} as the join said"
                : $"it did not answer ({ex.GetType().Name})";
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode) return $"it answered {(int)response.StatusCode} (it is not waiting for this join)";
            JoinCallbackAnswer? answer;
            try { answer = await response.Content.ReadFromJsonAsync<JoinCallbackAnswer>(Json, ct); }
            catch (JsonException) { return "its answer was not understood"; }
            var signature = answer?.Signature is { } s ? Base64Url.Decode(s) : null;
            if (answer?.JoinId != call.JoinId || signature is null
                || !ConsoleSigningKey.Verify(call.ServerPublicKey, JoinProof.Callback(call.JoinId, call.ConsoleId, call.ConsoleFingerprint, call.AddressText, call.CertificateFingerprint), signature))
                return "its answer was not signed with the key the join named";
            return null;
        }
    }
}

/// <summary>
/// The joins a server is waiting on, for its callback endpoint to confirm (<see cref="JoinCallbackEndpoint.MapJoinCallback"/>). <see cref="JoinClient"/>
/// adds one for the length of a join; a callback for any other id is not confirmed.
/// </summary>
public sealed class PendingJoins
{
    private sealed record Pending(string ConsoleFingerprint, string Address, string CertificateFingerprint, ConsoleSigningKey Key);

    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    /// <summary>Waits for a callback for <paramref name="joinId"/> until the result is disposed.</summary>
    public IDisposable Begin(string joinId, string consoleFingerprint, string address, string certificateFingerprint, ConsoleSigningKey key)
    {
        if (!_pending.TryAdd(joinId, new Pending(consoleFingerprint, address, certificateFingerprint, key))) throw new InvalidOperationException("A join with this id is already waiting.");
        return new Ending(() => _pending.TryRemove(joinId, out _));
    }

    /// <summary>The signed confirmation of a callback, or null when no join with its id waits for that console.</summary>
    public JoinCallbackAnswer? Answer(JoinCallbackRequest request)
    {
        if (request.JoinId is not { } id || request.ConsoleId is not { } consoleId || !_pending.TryGetValue(id, out var pending)) return null;
        if (Thumbprints.Parse(request.ConsoleThumbprint) != pending.ConsoleFingerprint) return null;
        var signature = pending.Key.Sign(JoinProof.Callback(id, consoleId, pending.ConsoleFingerprint, pending.Address, pending.CertificateFingerprint));
        return new JoinCallbackAnswer(id, Base64Url.Encode(signature));
    }

    private sealed class Ending(Action end) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) end();
        }
    }
}

/// <summary>The joining server's side of the callback: <c>POST</c> <see cref="ManagementApi.JoinCallbackPath"/> answers for a join it is waiting on and with <c>404</c> otherwise.</summary>
public static class JoinCallbackEndpoint
{
    private const int MaxBody = 4096;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapJoinCallback(this IEndpointRouteBuilder app, PendingJoins pending)
    {
        app.MapPost(ManagementApi.JoinCallbackPath, async (HttpContext http) =>
        {
            http.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize = MaxBody;
            JoinCallbackRequest? request;
            try { request = await http.Request.ReadFromJsonAsync<JoinCallbackRequest>(Json, http.RequestAborted); }
            catch (Exception ex) when (ex is JsonException or BadHttpRequestException or InvalidOperationException) { return Results.Json(new { error = "the request is not valid JSON" }, Json, statusCode: 400); }
            return request is not null && pending.Answer(request) is { } answer
                ? Results.Json(answer, Json)
                : Results.Json(new { error = "no join with this id is waiting" }, Json, statusCode: StatusCodes.Status404NotFound);
        });
        return app;
    }
}
