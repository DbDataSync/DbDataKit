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

        /// <summary>The client's compatibility generation and program version (24a UC04); null from a client that predates them.</summary>
        public int? Generation { get; set; }
        public string? ProgramVersion { get; set; }
    }

    /// <summary>Maps the pairing exchange. <paramref name="generation"/> is the compatibility generation this server states in the hello and checks the client against (<see cref="SuiteCompatibility"/>; tests replace it) and <paramref name="programVersion"/> the program version it states (the running program's by default).</summary>
    public static IEndpointRouteBuilder MapPairing(this IEndpointRouteBuilder app, PairingService service, int generation = SuiteCompatibility.Generation, string? programVersion = null)
    {
        app.MapGet(ManagementApi.PairingPath, () => Results.Json(new { instanceId = service.InstanceId, apiVersion = ManagementApi.Version, generation, programVersion = programVersion ?? SuiteCompatibility.ProgramVersion }, Json));
        app.MapPost(ManagementApi.PairingPath, async (HttpContext http) =>
        {
            http.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize = MaxBody;
            Body? body;
            try { body = await http.Request.ReadFromJsonAsync<Body>(Json, http.RequestAborted); }
            catch (Exception ex) when (ex is JsonException or BadHttpRequestException or InvalidOperationException) { return Results.Json(new { error = "the request is not valid JSON" }, Json, statusCode: 400); }
            if (body is null || !Enum.TryParse<ClientKind>(body.Kind, ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
                return Results.Json(new { error = "the request needs a client kind of console, server or cli" }, Json, statusCode: 400);

            // a client more than one generation from this server is refused before its code is spent, naming both versions and the side to upgrade (24a UC04)
            if (SuiteCompatibility.Incompatibility(body.Generation, body.ProgramVersion, generation, programVersion, peerRole: "console") is { } apart)
                return Results.Json(new { error = apart }, Json, statusCode: StatusCodes.Status409Conflict);

            var publicKey = body.PublicKey is { } encoded ? Base64Url.Decode(encoded) : null;
            PairingOutcome outcome;
            try { outcome = service.Pair(new PairingRequest(body.Proof, body.ClientId, body.Name, kind, publicKey, body.Generation)); }
            catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, Json, statusCode: StatusCodes.Status503ServiceUnavailable); }   // the server cannot yet say which certificate it presents
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
                generation,
                programVersion = programVersion ?? SuiteCompatibility.ProgramVersion,
                name = outcome.Client!.Name,
                ceiling = ServerRoles.Names(outcome.Client.Ceiling),
            }, Json);
        });
        return app;
    }
}
