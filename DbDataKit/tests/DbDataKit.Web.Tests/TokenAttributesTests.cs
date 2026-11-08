using System.Text;
using System.Text.Json;
using DbDataKit.Web.Trust;
using Xunit;

namespace DbDataKit.Web.Tests;

/// <summary>Plan 22a GP04, D4: the console's token to a server carries the person's directory attributes (the claim <c>attrs</c>, multi-valued) and a reader that does not know the claim is not affected by it.</summary>
public class TokenAttributesTests
{
    private const string Server = "store-1", Console = "console-1";
    private readonly TestClock _clock = new();
    private readonly ConsoleSigningKey _key = ConsoleSigningKey.Generate();

    private (ConsoleTokenIssuer Issuer, ConsoleTokenValidator Validator) Make() =>
        (new ConsoleTokenIssuer(Console, _key, _clock),
         new ConsoleTokenValidator(Server, new Consoles(new PairedClient(Console, "Console", _key.PublicKey, ServerRole.Viewer | ServerRole.Author, ClientKind.Console)), new MemoryReplayCache(_clock), _clock));

    private static JsonElement ClaimsOf(string token) => JsonSerializer.Deserialize<JsonElement>(System.Buffers.Text.Base64Url.DecodeFromChars(token.Split('.')[1]));

    [Fact]
    public void The_attributes_come_back_from_a_validated_token_with_every_value()
    {
        var (issuer, validator) = Make();
        var attributes = new Dictionary<string, IReadOnlyList<string>> { ["region"] = ["east", "west"], ["department"] = ["sales"] };

        var result = validator.Validate(issuer.Issue(new ConsoleTokenRequest(Server, "user-7", "Ada", ServerRole.Viewer, Attributes: attributes)));

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(["east", "west"], result.Token!.Attributes["region"]);
        Assert.Equal(["sales"], result.Token.Attributes["department"]);
    }

    [Fact]
    public void A_person_with_no_attributes_has_no_claim_and_a_reader_of_the_other_claims_is_not_affected_by_one()
    {
        var (issuer, validator) = Make();
        var none = issuer.Issue(new ConsoleTokenRequest(Server, "user-7", "Ada", ServerRole.Viewer));
        var empty = issuer.Issue(new ConsoleTokenRequest(Server, "user-7", "Ada", ServerRole.Viewer, Attributes: new Dictionary<string, IReadOnlyList<string>> { ["region"] = [] }));
        var some = issuer.Issue(new ConsoleTokenRequest(Server, "user-7", "Ada", ServerRole.Viewer, Attributes: new Dictionary<string, IReadOnlyList<string>> { ["region"] = ["east"] }));

        Assert.False(ClaimsOf(none).TryGetProperty("attrs", out _));
        Assert.False(ClaimsOf(empty).TryGetProperty("attrs", out _));
        Assert.True(ClaimsOf(some).TryGetProperty("attrs", out _));
        Assert.Empty(validator.Validate(none).Token!.Attributes);
        // a release before the claim reads sub, name, roles and projects and nothing else: those are the same with and without it
        foreach (var name in new[] { "iss", "aud", "sub", "name", "kind", "roles", "projects" })
            Assert.Equal(ClaimsOf(none).GetProperty(name).ToString(), ClaimsOf(some).GetProperty(name).ToString());
    }

    [Fact]
    public void A_claim_that_is_not_an_object_of_lists_of_strings_is_refused_not_read_as_none()
    {
        foreach (var bad in new[] { "\"east\"", "[\"east\"]", "{\"region\":\"east\"}", "{\"region\":[1]}" })
        {
            var (issuer, validator) = Make();
            var parts = issuer.Issue(new ConsoleTokenRequest(Server, "u", null, ServerRole.Viewer)).Split('.');
            var claims = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(System.Buffers.Text.Base64Url.DecodeFromChars(parts[1]))!;
            claims["attrs"] = JsonSerializer.Deserialize<JsonElement>(bad);
            var signingInput = parts[0] + "." + System.Buffers.Text.Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(claims));
            var forged = signingInput + "." + System.Buffers.Text.Base64Url.EncodeToString(_key.Sign(Encoding.ASCII.GetBytes(signingInput)));

            var result = validator.Validate(forged);

            Assert.Equal(TokenFailure.MissingClaim, result.Failure);
        }
    }
}
