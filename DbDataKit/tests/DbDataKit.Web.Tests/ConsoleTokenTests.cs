using System.Text;
using System.Text.Json;
using DbDataKit.Web.Trust;
using Xunit;

namespace DbDataKit.Web.Tests;

internal sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

internal sealed class Consoles(params PairedClient[] list) : IPairedClients
{
    public PairedClient? Find(string id) => list.FirstOrDefault(c => c.Id == id);
}

public class ConsoleTokenTests
{
    private const string Server = "server-instance-1", ConsoleId = "console-a";
    private readonly TestClock _clock = new();
    private readonly ConsoleSigningKey _key = ConsoleSigningKey.Generate();

    private (ConsoleTokenIssuer Issuer, ConsoleTokenValidator Validator, MemoryReplayCache Replay) Make(ServerRole ceiling = ServerRole.Admin | ServerRole.Author | ServerRole.Approver, string instance = Server)
    {
        var replay = new MemoryReplayCache(_clock);
        var consoles = new Consoles(new PairedClient(ConsoleId, "Operations", _key.PublicKey, ceiling));
        return (new ConsoleTokenIssuer(ConsoleId, _key, _clock), new ConsoleTokenValidator(instance, consoles, replay, _clock), replay);
    }

    private static ConsoleTokenRequest Request(ServerRole roles = ServerRole.Viewer, IReadOnlyDictionary<string, ServerRole>? projects = null, bool isConsole = false) =>
        new(Server, "user-7", "Ada Lovelace", roles, projects, isConsole);

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>A token with exactly the given header and claims, signed by <paramref name="signer"/>: for the cases a real issuer would never produce.</summary>
    private string Forge(object header, object claims, ConsoleSigningKey? signer = null)
    {
        var input = B64(JsonSerializer.SerializeToUtf8Bytes(header)) + "." + B64(JsonSerializer.SerializeToUtf8Bytes(claims));
        return input + "." + B64((signer ?? _key).Sign(Encoding.ASCII.GetBytes(input)));
    }

    private Dictionary<string, object?> Claims(Action<Dictionary<string, object?>>? change = null)
    {
        var now = _clock.Now.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object?>
        {
            ["iss"] = ConsoleId, ["aud"] = Server, ["sub"] = "user-7", ["name"] = "Ada", ["kind"] = "user",
            ["roles"] = new[] { "viewer" }, ["jti"] = Guid.NewGuid().ToString("N"), ["iat"] = now, ["exp"] = now + 300,
        };
        change?.Invoke(claims);
        return claims;
    }

    private static readonly object Header = new { alg = "EdDSA", typ = "dbdata-console" };

    // ---- the good token ----

    [Fact]
    public void A_token_from_a_paired_console_is_accepted_and_says_who_and_what()
    {
        var (issuer, validator, _) = Make();
        var token = issuer.Issue(Request(ServerRole.Operator, new Dictionary<string, ServerRole> { ["retail"] = ServerRole.Author }));
        var result = validator.Validate(token);

        Assert.True(result.Ok, result.Detail);
        var t = result.Token!;
        Assert.Equal(ConsoleId, t.Client.Id);
        Assert.Equal("user-7", t.SubjectId);
        Assert.Equal("Ada Lovelace", t.SubjectName);
        Assert.False(t.IsConsole);
        Assert.Equal(ServerRole.Operator | ServerRole.Viewer, t.Roles);                     // an Operator can read
        Assert.Equal(ServerRole.Author | ServerRole.Viewer, t.Projects["retail"]);
        Assert.Equal(_clock.Now.AddSeconds(300), t.ExpiresAt);
    }

    [Fact]
    public void A_console_polling_for_itself_is_marked_as_the_console()
    {
        var (issuer, validator, _) = Make();
        var result = validator.Validate(issuer.Issue(new ConsoleTokenRequest(Server, ConsoleId, null, ServerRole.Viewer, IsConsole: true)));
        Assert.True(result.Ok);
        Assert.True(result.Token!.IsConsole);
    }

    [Fact]
    public void Two_tokens_for_the_same_call_differ()
    {
        var (issuer, _, _) = Make();
        Assert.NotEqual(issuer.Issue(Request()), issuer.Issue(Request()));
    }

    [Fact]
    public void The_token_is_three_url_safe_parts_with_an_EdDSA_header_naming_the_key()
    {
        var (issuer, _, _) = Make();
        var parts = issuer.Issue(Request()).Split('.');
        Assert.Equal(3, parts.Length);
        Assert.All(parts, p => Assert.Matches("^[A-Za-z0-9_-]+$", p));
        using var header = JsonDocument.Parse(Convert.FromBase64String(parts[0].Replace('-', '+').Replace('_', '/') + new string('=', (4 - parts[0].Length % 4) % 4)));
        Assert.Equal("EdDSA", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal(_key.Fingerprint, header.RootElement.GetProperty("kid").GetString());
    }

    // ---- the keys ----

    [Fact]
    public void A_key_made_from_its_seed_is_the_same_key_and_has_a_stable_fingerprint()
    {
        var again = ConsoleSigningKey.FromSeed(_key.ExportSeed());
        Assert.Equal(_key.PublicKey, again.PublicKey);
        Assert.Equal(_key.Fingerprint, again.Fingerprint);
        Assert.Matches("^[0-9a-f]{16}$", _key.Fingerprint);
        Assert.Equal(32, _key.PublicKey.Length);
        Assert.NotEqual(_key.PublicKey, ConsoleSigningKey.Generate().PublicKey);
        Assert.Throws<ArgumentException>(() => ConsoleSigningKey.FromSeed(new byte[16]));
    }

    [Fact]
    public void A_signature_verifies_only_for_its_message_and_key()
    {
        var message = "hello"u8.ToArray();
        var signature = _key.Sign(message);
        Assert.True(ConsoleSigningKey.Verify(_key.PublicKey, message, signature));
        Assert.False(ConsoleSigningKey.Verify(_key.PublicKey, "hellO"u8, signature));
        Assert.False(ConsoleSigningKey.Verify(ConsoleSigningKey.Generate().PublicKey, message, signature));
        Assert.False(ConsoleSigningKey.Verify(_key.PublicKey, message, signature[..63]));
        Assert.False(ConsoleSigningKey.Verify(new byte[5], message, signature));
    }

    // ---- hostile tokens ----

    [Fact]
    public void A_token_signed_by_another_key_is_refused_even_with_the_right_issuer()
    {
        var (_, validator, _) = Make();
        var forged = new ConsoleTokenIssuer(ConsoleId, ConsoleSigningKey.Generate(), _clock).Issue(Request());
        Assert.Equal(TokenFailure.BadSignature, validator.Validate(forged).Failure);
    }

    [Fact]
    public void A_token_from_a_console_that_is_not_paired_is_refused()
    {
        var (_, validator, _) = Make();
        var stranger = new ConsoleTokenIssuer("console-b", _key, _clock).Issue(Request());
        Assert.Equal(TokenFailure.UnknownIssuer, validator.Validate(stranger).Failure);
    }

    [Fact]
    public void A_token_for_another_server_is_refused()
    {
        var (issuer, validator, _) = Make();
        var result = validator.Validate(issuer.Issue(new ConsoleTokenRequest("some-other-server", "u", null, ServerRole.Viewer)));
        Assert.Equal(TokenFailure.WrongAudience, result.Failure);
    }

    [Fact]
    public void An_expired_token_is_refused_after_the_skew_and_accepted_within_it()
    {
        var (issuer, validator, _) = Make();
        var token = issuer.Issue(Request(), lifetimeSeconds: 60);
        _clock.Advance(TimeSpan.FromSeconds(60 + ConsoleTokenValidator.SkewSeconds - 1));
        Assert.True(validator.Validate(token).Ok);                                                // a clock a little behind is forgiven

        var (issuer2, validator2, _) = Make();
        var token2 = issuer2.Issue(Request(), lifetimeSeconds: 60);
        _clock.Advance(TimeSpan.FromSeconds(60 + ConsoleTokenValidator.SkewSeconds + 1));
        Assert.Equal(TokenFailure.Expired, validator2.Validate(token2).Failure);
    }

    [Fact]
    public void A_token_issued_in_the_future_is_refused()
    {
        var (_, validator, _) = Make();
        var future = Forge(Header, Claims(c => { c["iat"] = _clock.Now.AddMinutes(10).ToUnixTimeSeconds(); c["exp"] = _clock.Now.AddMinutes(14).ToUnixTimeSeconds(); }));
        Assert.Equal(TokenFailure.NotYetValid, validator.Validate(future).Failure);
    }

    [Fact]
    public void A_token_that_lives_too_long_or_expires_before_it_was_issued_is_refused()
    {
        var (_, validator, _) = Make();
        var now = _clock.Now.ToUnixTimeSeconds();
        Assert.Equal(TokenFailure.LifetimeTooLong, validator.Validate(Forge(Header, Claims(c => c["exp"] = now + 3600))).Failure);
        Assert.Equal(TokenFailure.LifetimeTooLong, validator.Validate(Forge(Header, Claims(c => c["exp"] = now - 5))).Failure);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConsoleTokenIssuer(ConsoleId, _key, _clock).Issue(Request(), lifetimeSeconds: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConsoleTokenIssuer(ConsoleId, _key, _clock).Issue(Request(), lifetimeSeconds: 601));
    }

    [Fact]
    public void A_token_is_accepted_once()
    {
        var (issuer, validator, _) = Make();
        var token = issuer.Issue(Request());
        Assert.True(validator.Validate(token).Ok);
        Assert.Equal(TokenFailure.Replayed, validator.Validate(token).Failure);
    }

    [Fact]
    public void A_refused_token_does_not_use_up_its_id()
    {
        var (issuer, _, replay) = Make();
        var token = issuer.Issue(Request());
        var consoles = new Consoles(new PairedClient(ConsoleId, "Operations", _key.PublicKey, ServerRole.Admin));
        var wrongServer = new ConsoleTokenValidator("another-instance", consoles, replay, _clock);
        Assert.Equal(TokenFailure.WrongAudience, wrongServer.Validate(token).Failure);
        Assert.Equal(0, replay.Count);                                                           // nothing was remembered
        Assert.True(new ConsoleTokenValidator(Server, consoles, replay, _clock).Validate(token).Ok);
    }

    [Fact]
    public void A_replay_cache_forgets_a_token_when_it_could_no_longer_be_valid_and_refuses_a_flood()
    {
        var replay = new MemoryReplayCache(_clock, maxEntries: 3);
        var until = _clock.Now.AddMinutes(5);
        Assert.True(replay.TryAdd("c", "t1", until));
        Assert.False(replay.TryAdd("c", "t1", until));
        Assert.True(replay.TryAdd("d", "t1", until));                                            // the same id from another console is another token
        Assert.True(replay.TryAdd("c", "t2", until));
        Assert.False(replay.TryAdd("c", "t3", until));                                           // full: refused, not forgotten
        _clock.Advance(TimeSpan.FromMinutes(6));
        Assert.True(replay.TryAdd("c", "t3", _clock.Now.AddMinutes(5)));                         // the old ones expired and made room
        Assert.Equal(1, replay.Count);
    }

    [Theory]
    [InlineData(ServerRole.Admin, ServerRole.Viewer)]
    [InlineData(ServerRole.Operator, ServerRole.Viewer)]
    [InlineData(ServerRole.Approver, ServerRole.Author | ServerRole.Viewer)]
    [InlineData(ServerRole.Admin, ServerRole.Operator)]
    public void A_token_that_claims_more_than_the_consoles_ceiling_is_refused(ServerRole claimed, ServerRole ceiling)
    {
        var (issuer, validator, _) = Make(ceiling);
        var result = validator.Validate(issuer.Issue(Request(claimed)));
        Assert.Equal(TokenFailure.AboveCeiling, result.Failure);
        Assert.Contains("Operations", result.Detail);
    }

    [Fact]
    public void A_project_role_above_the_ceiling_is_refused_too_and_a_claim_within_it_is_accepted()
    {
        var (issuer, validator, _) = Make(ServerRole.Operator | ServerRole.Author);
        Assert.Equal(TokenFailure.AboveCeiling, validator.Validate(issuer.Issue(Request(ServerRole.Viewer, new Dictionary<string, ServerRole> { ["p"] = ServerRole.Approver }))).Failure);
        Assert.True(validator.Validate(issuer.Issue(Request(ServerRole.Operator, new Dictionary<string, ServerRole> { ["p"] = ServerRole.Author }))).Ok);
    }

    [Fact]
    public void Changing_the_claims_after_signing_breaks_the_signature()
    {
        var (issuer, validator, _) = Make(ServerRole.Viewer);
        var parts = issuer.Issue(Request(ServerRole.Viewer)).Split('.');
        var claims = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1].Replace('-', '+').Replace('_', '/') + new string('=', (4 - parts[1].Length % 4) % 4)));
        var promoted = B64(Encoding.UTF8.GetBytes(claims.Replace("\"viewer\"", "\"admin\"")));
        Assert.Equal(TokenFailure.BadSignature, validator.Validate(parts[0] + "." + promoted + "." + parts[2]).Failure);
    }

    [Fact]
    public void A_truncated_or_zeroed_signature_is_refused()
    {
        var (issuer, validator, _) = Make();
        var parts = issuer.Issue(Request()).Split('.');
        Assert.Equal(TokenFailure.BadSignature, validator.Validate(parts[0] + "." + parts[1] + "." + parts[2][..40]).Failure);
        Assert.Equal(TokenFailure.BadSignature, validator.Validate(parts[0] + "." + parts[1] + "." + B64(new byte[64])).Failure);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("HS256")]
    [InlineData("RS256")]
    [InlineData("eddsa")]
    [InlineData("")]
    public void Only_EdDSA_is_an_algorithm(string alg)
    {
        var (_, validator, _) = Make();
        Assert.Equal(TokenFailure.UnsupportedAlgorithm, validator.Validate(Forge(new { alg, typ = "dbdata-console" }, Claims())).Failure);
        Assert.Equal(TokenFailure.UnsupportedAlgorithm, validator.Validate(Forge(new { typ = "dbdata-console" }, Claims())).Failure);
    }

    [Theory]
    [InlineData("iss")]
    [InlineData("aud")]
    [InlineData("sub")]
    [InlineData("jti")]
    [InlineData("iat")]
    [InlineData("exp")]
    [InlineData("roles")]
    public void A_token_missing_a_claim_is_refused_by_name(string missing)
    {
        var (_, validator, _) = Make();
        var result = validator.Validate(Forge(Header, Claims(c => c.Remove(missing))));
        Assert.False(result.Ok);
        Assert.Contains(result.Failure, new[] { TokenFailure.MissingClaim, TokenFailure.UnknownIssuer });       // without iss there is no key to check against
    }

    [Theory]
    [InlineData("roles", "viewer")]                         // not an array
    [InlineData("roles", 5)]
    public void Roles_that_are_not_a_list_of_strings_are_refused(string claim, object value)
    {
        var (_, validator, _) = Make();
        Assert.Equal(TokenFailure.MissingClaim, validator.Validate(Forge(Header, Claims(c => c[claim] = value))).Failure);
    }

    [Theory]
    [InlineData("superuser")]
    [InlineData("viewer,admin")]
    [InlineData("none")]
    [InlineData("99")]
    [InlineData("")]
    public void A_role_that_is_not_one_is_refused_not_ignored(string role)
    {
        var (_, validator, _) = Make();
        Assert.Equal(TokenFailure.MissingClaim, validator.Validate(Forge(Header, Claims(c => c["roles"] = new[] { "viewer", role }))).Failure);
    }

    [Fact]
    public void Times_that_are_not_numbers_or_are_absurd_are_refused()
    {
        var (_, validator, _) = Make();
        Assert.Equal(TokenFailure.MissingClaim, validator.Validate(Forge(Header, Claims(c => c["exp"] = "later"))).Failure);
        Assert.Equal(TokenFailure.MissingClaim, validator.Validate(Forge(Header, Claims(c => c["iat"] = -1))).Failure);
        Assert.Equal(TokenFailure.MissingClaim, validator.Validate(Forge(Header, Claims(c => c["exp"] = long.MaxValue))).Failure);
        Assert.Equal(TokenFailure.MissingClaim, validator.Validate(Forge(Header, Claims(c => c["jti"] = "x"))).Failure);        // an id too short to be unique
    }

    [Fact]
    public void Malformed_tokens_are_refused_without_a_crash()
    {
        var (issuer, validator, _) = Make();
        var good = issuer.Issue(Request());
        var parts = good.Split('.');
        string[] corpus =
        [
            "", " ", ".", "..", "...", "a", "a.b", "a.b.c.d", "!!!.???.***", good + "=", good + "+", parts[0] + "." + parts[1] + "." + parts[2] + "\n",
            parts[0] + "." + parts[1] + ".", "." + parts[1] + "." + parts[2], parts[0] + ".." + parts[2],
            B64("[]"u8.ToArray()) + "." + B64("[]"u8.ToArray()) + "." + B64(new byte[64]),
            B64("{"u8.ToArray()) + "." + B64("}"u8.ToArray()) + "." + B64(new byte[64]),
            B64("\"text\""u8.ToArray()) + "." + parts[1] + "." + parts[2],
            B64([0xff, 0xfe, 0x00]) + "." + parts[1] + "." + parts[2],
            new string('A', 9000),
            good + good,
        ];
        foreach (var token in corpus)
        {
            var result = validator.Validate(token);
            Assert.False(result.Ok, $"accepted: {token[..Math.Min(40, token.Length)]}");
        }
        Assert.Equal(TokenFailure.Malformed, validator.Validate(null).Failure);
        Assert.Equal(TokenFailure.Malformed, validator.Validate(new string('A', ConsoleTokenValidator.MaximumTokenLength + 1)).Failure);
    }

    [Fact]
    public void A_deeply_nested_claim_does_not_take_the_validator_down()
    {
        var (_, validator, _) = Make();
        var nest = string.Concat(Enumerable.Repeat("[", 5000)) + string.Concat(Enumerable.Repeat("]", 5000));
        var input = B64(JsonSerializer.SerializeToUtf8Bytes(Header)) + "." + B64(Encoding.UTF8.GetBytes("{\"iss\":\"console-a\",\"deep\":" + nest + "}"));
        var token = input + "." + B64(_key.Sign(Encoding.ASCII.GetBytes(input)));
        Assert.False(validator.Validate(token).Ok);                                                 // refused (a depth limit or a missing claim), never an exception
    }

    // ---- roles ----

    [Fact]
    public void Roles_imply_what_the_table_of_the_plan_says()
    {
        Assert.Equal(ServerRole.Admin | ServerRole.Operator | ServerRole.Viewer, ServerRoles.Close(ServerRole.Admin));
        Assert.Equal(ServerRole.Author | ServerRole.Viewer, ServerRoles.Close(ServerRole.Author));
        Assert.Equal(ServerRole.Approver | ServerRole.Viewer, ServerRoles.Close(ServerRole.Approver));
        Assert.False(ServerRoles.Allows(ServerRole.Admin, ServerRole.Approver));                  // an administrator does not approve plans
        Assert.False(ServerRoles.Allows(ServerRole.Author, ServerRole.Approver));
        Assert.True(ServerRoles.Allows(ServerRole.Admin, ServerRole.Viewer));
        Assert.False(ServerRoles.Allows(ServerRole.None, ServerRole.Viewer));
        Assert.False(ServerRoles.Allows(ServerRole.Admin, ServerRole.None));
    }

    [Fact]
    public void The_lower_of_a_users_role_and_a_ceiling_is_what_both_allow()
    {
        Assert.Equal(ServerRole.Operator | ServerRole.Viewer, ServerRoles.Lower(ServerRole.Admin, ServerRole.Operator));
        Assert.Equal(ServerRole.Viewer, ServerRoles.Lower(ServerRole.Author, ServerRole.Approver));
        Assert.Equal(ServerRole.None, ServerRoles.Lower(ServerRole.Viewer, ServerRole.None));
        Assert.Equal(ServerRole.Admin, ServerRoles.Above(ServerRole.Admin, ServerRole.Operator));
    }

    [Fact]
    public void Role_names_round_trip_and_garbage_does_not_parse()
    {
        Assert.True(ServerRoles.TryParse(["Viewer", "operator"], out var roles));
        Assert.Equal(ServerRole.Viewer | ServerRole.Operator, roles);
        Assert.Equal(["viewer", "operator"], ServerRoles.Names(roles));
        Assert.True(ServerRoles.TryParse([], out var none));
        Assert.Equal(ServerRole.None, none);
        Assert.False(ServerRoles.TryParse(["root"], out _));
    }
}
