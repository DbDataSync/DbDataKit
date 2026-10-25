using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Web.Trust;

/// <summary>
/// The HTTPS side of pairing (plan 13, W12): <c>GET /management/v1/pairing</c> says which server this is and which API version it speaks (nothing secret, so a client can see the certificate before it
/// sends anything), and <c>POST</c> takes a <see cref="PairingRequest"/> and answers with the server's identity and its proof. A refusal is a <c>403</c> that does not say whether the code was wrong, expired or used.
/// </summary>
public static class PairingEndpoint
{
    private const int MaxBody = 4096;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public sealed class Body
    {
        public string? Proof { get; set; }
        public string? ClientId { get; set; }
        public string? Name { get; set; }
        public string? Kind { get; set; }
        public string? PublicKey { get; set; }
    }

    public static IEndpointRouteBuilder MapPairing(this IEndpointRouteBuilder app, PairingService service)
    {
        app.MapGet(ManagementApi.PairingPath, () => Results.Json(new { instanceId = service.InstanceId, apiVersion = ManagementApi.Version }, Json));
        app.MapPost(ManagementApi.PairingPath, async (HttpContext http) =>
        {
            http.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize = MaxBody;
            Body? body;
            try { body = await http.Request.ReadFromJsonAsync<Body>(Json, http.RequestAborted); }
            catch (Exception ex) when (ex is JsonException or BadHttpRequestException or InvalidOperationException) { return Results.Json(new { error = "the request is not valid JSON" }, Json, statusCode: 400); }
            if (body is null || !Enum.TryParse<ClientKind>(body.Kind, ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
                return Results.Json(new { error = "the request needs a client kind of console, server or cli" }, Json, statusCode: 400);

            var publicKey = body.PublicKey is { } encoded ? Base64Url.Decode(encoded) : null;
            var outcome = service.Pair(new PairingRequest(body.Proof, body.ClientId, body.Name, kind, publicKey));
            if (!outcome.Ok)
            {
                http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("DbDataKit.Web.Pairing").LogWarning("A pairing was refused: {Reason}", outcome.Refusal);
                return Results.Json(new { error = "the pairing was not accepted" }, Json, statusCode: StatusCodes.Status403Forbidden);
            }
            return Results.Json(new
            {
                instanceId = outcome.InstanceId,
                certificateFingerprint = outcome.CertificateFingerprint,
                serverProof = outcome.ServerProof,
                apiVersion = ManagementApi.Version,
                name = outcome.Client!.Name,
                ceiling = ServerRoles.Names(outcome.Client.Ceiling),
            }, Json);
        });
        return app;
    }
}
