using DbDataKit.Serving;
using Xunit;

namespace DbDataKit.Serving.Tests;

/// <summary>
/// Plan 24a UC15, the key half of the cache's safety suite: for every request kind and every scope, each thing that can change the rows a caller gets (the access fingerprint, the channel, the preview, the published version, the limits asked for, the
/// source, the text, the parameters) is part of the key, so no module that asks the core for a cached answer can be handed another caller's, another channel's or an older rules table's. The rows of the same questions through every module, with the rules table
/// refreshed between runs, are <c>ModuleAccessConformanceTests</c> in Gateway.
/// </summary>
public sealed class CacheKeyCompletenessTests
{
    public static IEnumerable<object[]> Scopes => Enum.GetValues<ServingCacheScope>().Select(s => new object[] { s });

    private static ServingCaller Caller(string id = "u1") => new(id, id);

    private static ServingContext Context(string channel = "prod", string? preview = null, string caller = "u1") => new(Caller(caller), channel, preview);

    private static ServingCachePlan Plan(ServingCacheScope scope, string? access = "fp-1", string? version = "3") => new(TimeSpan.FromMinutes(5), scope, version, ["d"], access);

    private static IEnumerable<ServingRequest> Requests =>
    [
        new ServingRequest.Sql("main", "select 1"),
        new ServingRequest.Semantic("semantic:m", """{"kind":"query"}"""),
        new ServingRequest.SemanticSql("semantic:m", "SELECT 1"),
        new ServingRequest.Mdx("semantic:m", "SELECT 1 ON COLUMNS FROM [m]"),
    ];

    public static IEnumerable<object[]> KindsAndScopes => Requests.SelectMany(r => Enum.GetValues<ServingCacheScope>().Select(s => new object[] { r, s }));

    [Theory, MemberData(nameof(KindsAndScopes))]
    public void The_fingerprint_the_channel_the_preview_the_version_and_the_limits_are_part_of_every_key(ServingRequest request, ServingCacheScope scope)
    {
        var key = ServingCacheKey.Build(Context(), request, Plan(scope));

        Assert.NotEqual(key, ServingCacheKey.Build(Context(), request, Plan(scope, access: "fp-2")));
        Assert.NotEqual(key, ServingCacheKey.Build(Context(channel: "stage"), request, Plan(scope)));
        Assert.NotEqual(key, ServingCacheKey.Build(Context(preview: "draft-1"), request, Plan(scope)));
        Assert.NotEqual(key, ServingCacheKey.Build(Context(), request, Plan(scope, version: "4")));
        Assert.NotEqual(key, ServingCacheKey.Build(Context(), request with { Asked = new ServingLimits(MaxRows: 10) }, Plan(scope)));
        Assert.NotEqual(key, ServingCacheKey.Build(Context(), request with { Source = "other" }, Plan(scope)));
        Assert.Equal(key, ServingCacheKey.Build(Context(), request, Plan(scope)));
    }

    [Theory, MemberData(nameof(KindsAndScopes))]
    public void A_caller_with_no_rules_and_one_with_some_never_share_in_any_scope(ServingRequest request, ServingCacheScope scope)
    {
        // the host's fingerprint of "no rule applies" is "-" (or none); any real fingerprint is another key
        var none = ServingCacheKey.Build(Context(), request, Plan(scope, access: "-"));
        Assert.NotEqual(none, ServingCacheKey.Build(Context(), request, Plan(scope, access: "fp-1")));
    }

    [Theory, MemberData(nameof(Scopes))]
    public void Callers_are_apart_in_scope_caller_and_share_only_where_the_scope_and_the_fingerprint_say(ServingCacheScope scope)
    {
        var request = new ServingRequest.Semantic("semantic:m", """{"kind":"query"}""");
        var a = ServingCacheKey.Build(Context(caller: "a"), request, Plan(scope));
        var b = ServingCacheKey.Build(Context(caller: "b"), request, Plan(scope));

        if (scope == ServingCacheScope.Caller) Assert.NotEqual(a, b);
        else Assert.Equal(a, b);
    }

    [Fact]
    public void A_text_or_a_parameter_that_differs_is_another_key_in_every_kind_that_has_one()
    {
        var plan = Plan(ServingCacheScope.Rules);
        string Key(ServingRequest request) => ServingCacheKey.Build(Context(), request, plan);

        Assert.NotEqual(Key(new ServingRequest.Sql("main", "select 1")), Key(new ServingRequest.Sql("main", "select 2")));
        Assert.NotEqual(Key(new ServingRequest.Sql("main", "select ?", [new ServingParameter(null, "int64", "1")])), Key(new ServingRequest.Sql("main", "select ?", [new ServingParameter(null, "int64", "2")])));
        Assert.NotEqual(Key(new ServingRequest.Semantic("semantic:m", """{"a":1}""")), Key(new ServingRequest.Semantic("semantic:m", """{"a":2}""")));
        Assert.NotEqual(Key(new ServingRequest.SemanticSql("semantic:m", "SELECT 1", null, [new ServingParameter("p", "string", "x")])), Key(new ServingRequest.SemanticSql("semantic:m", "SELECT 1", null, [new ServingParameter("p", "string", "y")])));
        Assert.NotEqual(Key(new ServingRequest.Mdx("semantic:m", "a")), Key(new ServingRequest.Mdx("semantic:m", "b")));
        // the same text as another kind of request is never the same entry
        Assert.NotEqual(Key(new ServingRequest.Sql("semantic:m", "SELECT 1")), Key(new ServingRequest.SemanticSql("semantic:m", "SELECT 1")));
    }

    [Fact]
    public void Scope_rules_with_no_fingerprint_falls_back_to_every_attribute_so_it_is_never_wider_than_a_rule_could_be()
    {
        var plan = Plan(ServingCacheScope.Rules, access: null);
        var request = new ServingRequest.Semantic("semantic:m", """{"kind":"query"}""");
        ServingContext With(params string[] regions) => new(new ServingCaller("u", "u") { Attributes = new Dictionary<string, IReadOnlyList<string>> { ["region"] = regions } });

        Assert.Equal(ServingCacheKey.Build(With("east"), request, plan), ServingCacheKey.Build(With("east"), request, plan));
        Assert.NotEqual(ServingCacheKey.Build(With("east"), request, plan), ServingCacheKey.Build(With("west"), request, plan));
        Assert.NotEqual(ServingCacheKey.Build(With("east"), request, plan), ServingCacheKey.Build(With(), request, plan));
    }
}
