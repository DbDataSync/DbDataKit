using DbDataKit.Cli;
using DbDataKit.Web.Trust;
using Xunit;

namespace DbDataKit.Web.Tests;

public sealed class PairingTests : IDisposable
{
    private const string Instance = "server-instance-1";
    private readonly string _dir = Directory.CreateTempSubdirectory("pairing").FullName;
    private readonly TestClock _clock = new();
    private string ServerYaml => Path.Combine(_dir, "server.yaml");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private PairingCodes Codes() => new(Path.Combine(_dir, "pairing-codes.json"), _clock);
    private PairedClientsFile Clients() => new(ServerYaml, _clock);
    private PairingService Service() => new(Instance, "ab12cd34ef56ab12", Clients(), Codes(), _clock);

    private static PairingRequest Request(string? code, ConsoleSigningKey key, string id = "console-a", ClientKind kind = ClientKind.Console, string? name = "Operations") =>
        new(code, id, name, kind, key.PublicKey);

    // ---- the code ----

    [Fact]
    public void ACodeWorksOnce()
    {
        var codes = Codes();
        var code = codes.Create(ServerRole.Operator).Code;
        Assert.NotNull(codes.Redeem(code));
        Assert.Null(codes.Redeem(code));
    }

    [Fact]
    public void ACodeExpires()
    {
        var codes = Codes();
        var made = codes.Create(ServerRole.Viewer, lifetime: TimeSpan.FromMinutes(5));
        _clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        Assert.Null(codes.Redeem(made.Code));
        Assert.Equal(0, codes.Pending());
    }

    [Fact]
    public void ACodeStillWorksJustBeforeItExpires()
    {
        var codes = Codes();
        var made = codes.Create(ServerRole.Viewer, lifetime: TimeSpan.FromMinutes(5));
        _clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        Assert.NotNull(codes.Redeem(made.Code));
    }

    [Fact]
    public void ACodeIsAcceptedHoweverItIsTyped()
    {
        var codes = Codes();
        var code = codes.Create(ServerRole.Viewer).Code;
        Assert.NotNull(codes.Redeem("  " + code.ToLowerInvariant().Replace("-", "") + " "));
    }

    [Fact]
    public void TheCodeCarriesItsCeilingAndLabel()
    {
        var codes = Codes();
        var grant = codes.Redeem(codes.Create(ServerRole.Operator, "ops").Code);
        Assert.Equal(("ops", ServerRole.Operator), (grant!.Label, grant.Ceiling));
    }

    [Fact]
    public void OnlyAHashOfTheCodeIsKept()
    {
        var made = Codes().Create(ServerRole.Viewer);
        var file = File.ReadAllText(Path.Combine(_dir, "pairing-codes.json"));
        Assert.DoesNotContain(made.Code.Replace("-", ""), file);
    }

    [Fact]
    public void WrongCodesAreRefusedAndFiveInARowCancelEveryWaitingCode()
    {
        var codes = Codes();
        var good = codes.Create(ServerRole.Admin).Code;
        for (var i = 0; i < PairingCodes.MaxFailures; i++) Assert.Null(codes.Redeem("AAAAA-AAAAA-AAAAA-AAAAA"));
        Assert.Null(codes.Redeem(good));
    }

    [Fact]
    public void FewerWrongCodesLeaveTheGoodOneAndASuccessResetsTheCount()
    {
        var codes = Codes();
        for (var i = 0; i < PairingCodes.MaxFailures - 1; i++) Assert.Null(codes.Redeem(null));
        Assert.NotNull(codes.Redeem(codes.Create(ServerRole.Viewer).Code));
        for (var i = 0; i < PairingCodes.MaxFailures - 1; i++) Assert.Null(codes.Redeem("not a code"));
        Assert.NotNull(codes.Redeem(codes.Create(ServerRole.Viewer).Code));
    }

    [Fact]
    public void ADamagedCodeFileHoldsNoCodes()
    {
        File.WriteAllText(Path.Combine(_dir, "pairing-codes.json"), "{ not json");
        var codes = Codes();
        Assert.Null(codes.Redeem("AAAAA-AAAAA-AAAAA-AAAAA"));
        Assert.NotNull(codes.Redeem(codes.Create(ServerRole.Viewer).Code));
    }

    // ---- pairing ----

    [Fact]
    public void PairingRecordsTheClientInServerYaml()
    {
        var key = ConsoleSigningKey.Generate();
        var outcome = Service().Pair(Request(Codes().Create(ServerRole.Operator).Code, key));
        Assert.True(outcome.Ok);
        Assert.Equal(("server-instance-1", "ab12cd34ef56ab12"), (outcome.InstanceId, outcome.CertificateFingerprint));

        var found = Clients().Find("console-a")!;
        Assert.Equal(("Operations", ClientKind.Console), (found.Name, found.Kind));
        Assert.Equal(key.PublicKey, found.PublicKey);
        Assert.Equal(ServerRoles.Close(ServerRole.Operator), ServerRoles.Close(found.Ceiling));
        Assert.Contains("pairedClients:", File.ReadAllText(ServerYaml));
    }

    [Fact]
    public void PairingKeepsTheRestOfServerYaml()
    {
        File.WriteAllText(ServerYaml, "schemaVersion: 1\n# where it listens\nlisten:\n  port: 5001 # not the default\npairedClients:\n");
        Service().Pair(Request(Codes().Create(ServerRole.Viewer).Code, ConsoleSigningKey.Generate()));
        var text = File.ReadAllText(ServerYaml);
        Assert.Contains("# where it listens", text);
        Assert.Contains("port: 5001 # not the default", text);
        Assert.NotNull(Clients().Find("console-a"));
    }

    [Fact]
    public void ACodeCannotPairTwice()
    {
        var service = Service();
        var code = Codes().Create(ServerRole.Viewer).Code;
        Assert.True(service.Pair(Request(code, ConsoleSigningKey.Generate())).Ok);
        var second = service.Pair(Request(code, ConsoleSigningKey.Generate(), id: "console-b"));
        Assert.False(second.Ok);
        Assert.Null(Clients().Find("console-b"));
    }

    [Fact]
    public void AnExpiredCodeDoesNotPair()
    {
        var code = Codes().Create(ServerRole.Viewer, lifetime: TimeSpan.FromMinutes(1)).Code;
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(Service().Pair(Request(code, ConsoleSigningKey.Generate())).Ok);
        Assert.Empty(Clients().All());
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("new\nline")]
    [InlineData("this-id-is-far-too-long-to-be-an-id-this-id-is-far-too-long-to-be-an-id")]
    public void AMalformedRequestIsRefusedWithoutSpendingTheCode(string id)
    {
        var codes = Codes();
        var code = codes.Create(ServerRole.Viewer).Code;
        Assert.False(Service().Pair(Request(code, ConsoleSigningKey.Generate(), id: id)).Ok);
        Assert.False(Service().Pair(new PairingRequest(code, "console-a", "x", ClientKind.Console, new byte[5])).Ok);
        Assert.False(Service().Pair(new PairingRequest(code, "console-a", "x", (ClientKind)9, ConsoleSigningKey.Generate().PublicKey)).Ok);
        Assert.Equal(1, codes.Pending());
    }

    [Fact]
    public void PairingAgainReplacesTheEntryWithTheNewKey()
    {
        var first = ConsoleSigningKey.Generate();
        var second = ConsoleSigningKey.Generate();
        Service().Pair(Request(Codes().Create(ServerRole.Viewer).Code, first));
        Service().Pair(Request(Codes().Create(ServerRole.Operator).Code, second));
        var all = Clients().All();
        Assert.Single(all);
        Assert.Equal(second.PublicKey, all[0].PublicKey);
    }

    [Fact]
    public void ClientsOfEveryKindArePairedAndKeepTheirKind()
    {
        foreach (var kind in Enum.GetValues<ClientKind>())
            Assert.True(Service().Pair(Request(Codes().Create(ServerRole.Viewer).Code, ConsoleSigningKey.Generate(), id: "c-" + kind, kind: kind)).Ok);
        Assert.Equal([ClientKind.Console, ClientKind.Server, ClientKind.Cli], Clients().All().Select(c => c.Kind).OrderBy(k => k));
    }

    [Fact]
    public void ANameWithPunctuationSurvives()
    {
        var name = "Ops: \"east\" # [1]";
        Service().Pair(Request(Codes().Create(ServerRole.Viewer).Code, ConsoleSigningKey.Generate(), name: name));
        Assert.Equal(name, Clients().Find("console-a")!.Name);
    }

    [Fact]
    public void TheLabelOnTheCodeNamesThePairing()
    {
        Service().Pair(Request(Codes().Create(ServerRole.Viewer, "build-prod").Code, ConsoleSigningKey.Generate(), name: "whatever it calls itself"));
        Assert.Equal("build-prod", Clients().Find("console-a")!.Name);
    }

    [Fact]
    public void ParallelPairingsAllLand()
    {
        var keys = Enumerable.Range(0, 8).Select(_ => ConsoleSigningKey.Generate()).ToArray();
        var codes = keys.Select(_ => Codes().Create(ServerRole.Viewer).Code).ToArray();
        Parallel.For(0, keys.Length, i => Assert.True(new PairingService(Instance, "x", new PairedClientsFile(ServerYaml), Codes(), _clock).Pair(Request(codes[i], keys[i], id: "c" + i)).Ok));
        Assert.Equal(8, Clients().All().Count);
    }

    // ---- the tokens of a paired client ----

    private ConsoleTokenValidator Validator(PairedClientsFile clients) => new(Instance, clients, new MemoryReplayCache(_clock), _clock);

    private static string Token(ConsoleSigningKey key, ServerRole roles, TestClock clock, string id = "console-a") =>
        new ConsoleTokenIssuer(id, key, clock).Issue(new ConsoleTokenRequest(Instance, "user-7", "Ada", roles));

    [Fact]
    public void ThePairedClientsTokensAreAcceptedUpToTheCeilingAndNoFurther()
    {
        var key = ConsoleSigningKey.Generate();
        Service().Pair(Request(Codes().Create(ServerRole.Operator).Code, key));
        var validator = Validator(Clients());
        Assert.True(validator.Validate(Token(key, ServerRole.Operator, _clock)).Ok);
        Assert.Equal(TokenFailure.AboveCeiling, validator.Validate(Token(key, ServerRole.Admin, _clock)).Failure);
    }

    [Fact]
    public void UnpairingTakesEffectOnTheNextCallEvenFromAnotherProcess()
    {
        var key = ConsoleSigningKey.Generate();
        Service().Pair(Request(Codes().Create(ServerRole.Viewer).Code, key));
        var running = Validator(Clients()); // the server, with its list already read
        Assert.True(running.Validate(Token(key, ServerRole.Viewer, _clock)).Ok);

        Assert.True(Clients().Remove("console-a")); // 'server unpair' in another process

        var after = running.Validate(Token(key, ServerRole.Viewer, _clock));
        Assert.Equal(TokenFailure.UnknownIssuer, after.Failure);
    }

    [Fact]
    public void ARePairedClientIsTrustedWithTheNewKeyOnlyOnTheNextCall()
    {
        var oldKey = ConsoleSigningKey.Generate();
        var newKey = ConsoleSigningKey.Generate();
        Service().Pair(Request(Codes().Create(ServerRole.Viewer).Code, oldKey));
        var running = Validator(Clients());
        Assert.True(running.Validate(Token(oldKey, ServerRole.Viewer, _clock)).Ok);
        Service().Pair(Request(Codes().Create(ServerRole.Viewer).Code, newKey));
        Assert.Equal(TokenFailure.BadSignature, running.Validate(Token(oldKey, ServerRole.Viewer, _clock)).Failure);
        Assert.True(running.Validate(Token(newKey, ServerRole.Viewer, _clock)).Ok);
    }

    [Fact]
    public void UnpairingOneClientLeavesTheOtherWorking()
    {
        var a = ConsoleSigningKey.Generate();
        var b = ConsoleSigningKey.Generate();
        Service().Pair(Request(Codes().Create(ServerRole.Viewer).Code, a, id: "a"));
        Service().Pair(Request(Codes().Create(ServerRole.Viewer).Code, b, id: "b"));
        Clients().Remove("a");
        var validator = Validator(Clients());
        Assert.False(validator.Validate(Token(a, ServerRole.Viewer, _clock, "a")).Ok);
        Assert.True(validator.Validate(Token(b, ServerRole.Viewer, _clock, "b")).Ok);
    }

    [Fact]
    public void RemovingAClientThatIsNotPairedSaysSo()
    {
        Service().Pair(Request(Codes().Create(ServerRole.Viewer).Code, ConsoleSigningKey.Generate()));
        Assert.False(Clients().Remove("nobody"));
        Assert.Single(Clients().All());
    }

    // ---- a damaged list is never trusted ----

    [Fact]
    public void AnEntryThatIsNotValidIsSkippedAndNamed()
    {
        var good = ConsoleSigningKey.Generate();
        File.WriteAllText(ServerYaml,
            "schemaVersion: 1\npairedClients:\n" +
            "  - {id: short-key, name: x, kind: console, publicKey: AAAA, ceiling: [viewer]}\n" +
            "  - {id: no-ceiling, name: x, kind: console, publicKey: " + Convert.ToBase64String(good.PublicKey).TrimEnd('=').Replace('+', '-').Replace('/', '_') + "}\n" +
            "  - {id: odd-kind, name: x, kind: robot, publicKey: " + Convert.ToBase64String(good.PublicKey).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ", ceiling: [viewer]}\n");
        var clients = Clients();
        Assert.Empty(clients.All());
        Assert.Equal(3, clients.Problems.Count);
        Assert.Contains(clients.Problems, p => p.Contains("short-key"));
    }

    [Fact]
    public void NoServerYamlMeansNoClients()
    {
        var clients = Clients();
        Assert.Null(clients.Find("anyone"));
        Assert.Empty(clients.Problems);
    }

    // ---- the verbs ----

    private (int Exit, string Out, string Error, CliContext Context) Run(string verb, params string[] args)
    {
        var verbs = PairingVerbs.Create(_ => new PairingFiles(ServerYaml));
        var (o, e) = (new StringWriter(), new StringWriter());
        var context = new CliContext(o, e, TextReader.Null);
        var exit = verbs.Single(v => v.Name == verb).Run(args, context).GetAwaiter().GetResult();
        return (exit, o.ToString(), e.ToString(), context);
    }

    [Fact]
    public void ServerPairPrintsACodeThatPairs()
    {
        var run = Run("pair", "--max-role", "viewer,operator", "--name", "ops");
        Assert.Equal(0, run.Exit);
        var code = (string)run.Context.Data["code"]!;
        Assert.Contains(code, run.Out);
        Assert.Equal(["viewer", "operator"], ((IReadOnlyList<string>)run.Context.Data["max_role"]!).Order().Reverse());

        var outcome = new PairingService(Instance, "x", Clients(), Codes()).Pair(Request(code, ConsoleSigningKey.Generate()));
        Assert.True(outcome.Ok);
        Assert.Equal("ops", outcome.Client!.Name);
    }

    [Theory]
    [InlineData("--max-role", "root")]
    [InlineData("--minutes", "0")]
    [InlineData("--minutes", "soon")]
    [InlineData("--nope", "1")]
    public void ServerPairRefusesBadOptions(string option, string value)
    {
        var run = Run("pair", option, value);
        Assert.Equal(CliExit.Usage, run.Exit);
        Assert.Equal(0, Codes().Pending());
    }

    [Fact]
    public void ServerClientsListsThePairingsAndUnpairRemovesOneByName()
    {
        var service = Service();
        service.Pair(Request(Codes().Create(ServerRole.Viewer).Code, ConsoleSigningKey.Generate(), id: "ops-1", name: "Operations"));
        service.Pair(Request(Codes().Create(ServerRole.Admin).Code, ConsoleSigningKey.Generate(), id: "build-1", kind: ClientKind.Server, name: "build-prod"));

        var list = Run("clients");
        Assert.Contains("Operations", list.Out);
        Assert.Contains("server", list.Out);
        Assert.Equal(2, ((IEnumerable<object>)list.Context.Data["clients"]!).Count());

        var unpair = Run("unpair", "operations");
        Assert.Equal(0, unpair.Exit);
        Assert.Null(Clients().Find("ops-1"));
        Assert.NotNull(Clients().Find("build-1"));
    }

    [Fact]
    public void ServerUnpairOfAnUnknownClientFails()
    {
        var run = Run("unpair", "ghost");
        Assert.Equal(CliExit.Failure, run.Exit);
        Assert.Contains("ghost", run.Error);
    }

    [Fact]
    public void ServerUnpairByNameRefusesWhenTwoClientsShareIt()
    {
        var service = Service();
        service.Pair(Request(Codes().Create(ServerRole.Viewer).Code, ConsoleSigningKey.Generate(), id: "a", name: "Ops"));
        service.Pair(Request(Codes().Create(ServerRole.Viewer).Code, ConsoleSigningKey.Generate(), id: "b", name: "Ops"));
        var run = Run("unpair", "Ops");
        Assert.Equal(CliExit.Failure, run.Exit);
        Assert.Equal(2, Clients().All().Count);
        Assert.Equal(0, Run("unpair", "a").Exit);
    }

    [Fact]
    public void TheVerbsAreAcceptedByARouter()
    {
        var router = new NounRouter("test", [new Noun("server", "x", PairingVerbs.Create(_ => new PairingFiles(ServerYaml)))]);
        Assert.NotNull(router);
    }
}
