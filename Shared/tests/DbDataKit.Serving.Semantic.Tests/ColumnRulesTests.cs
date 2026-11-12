using Apache.Arrow;
using Apache.Arrow.Types;
using DbDataKit.Semantic;

namespace DbDataKit.Serving.Semantic.Tests;

/// <summary>
/// Plan 22a GP13: the column rules of a model's <c>access</c> section, resolved per caller. A hidden column is unknown to every request kind, a masked or nulled one is answered masked or NULL and cannot be filtered or sorted by, the
/// strongest treatment wins, and the cache key holds the treatments so a cached answer is never served to a caller who sees something else.
/// </summary>
public sealed class ColumnRulesTests : IDisposable
{
    private const string Access = """
        access:
          columns:
            - { subject: interns, dataset: dim_customer, column: customer, treatment: mask }
            - { subject: contractors, dataset: dim_customer, column: customer, treatment: mask, mask: hash }
            - { subject: nulled, dataset: dim_customer, column: region, treatment: "null" }
            - { subject: guests, dataset: dim_customer, column: customer, treatment: hide }
            - { subject: guests, dataset: fact_sales, column: status, treatment: hide }
            - { subject: dana, subject_kind: person, dataset: dim_customer, column: customer, treatment: hide }
        """;

    private const string ByCustomer = """{"spec_version": "0.3", "kind": "query", "query": {"measures": ["orders.order_count"], "attributes": ["customers.customer"], "limit": 100, "order": [["orders.order_count", "desc"]]}}""";
    private const string ByRegion = """{"spec_version": "0.3", "kind": "query", "query": {"measures": ["orders.order_count"], "attributes": ["customers.region"], "limit": 100}}""";

    private static readonly ServingCaller Everyone = new("u0", "erin");
    private static readonly ServingCaller Intern = Caller("u1", "ivy", "interns");
    private static readonly ServingCaller Contractor = Caller("u2", "carl", "contractors");
    private static readonly ServingCaller Nulled = Caller("u3", "nina", "nulled");
    private static readonly ServingCaller Guest = Caller("u4", "gus", "guests");

    private static ServingCaller Caller(string id, string name, params string[] groups) => new(id, name, new HashSet<string>(groups, StringComparer.OrdinalIgnoreCase), new Dictionary<string, string>());

    private readonly DuckDbHost _host = new();
    private readonly SemanticModels _models = new();
    private readonly IServingServices _services;

    public ColumnRulesTests()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "conformance", "catalog.yaml"))
            .Replace("\nmeasures:\n", "\nmeasures:\n  orders.statuses:\n    type: integer\n    source: sqlserver_sales\n    agg: count_distinct\n    of: {table: fact_sales, column: status}\n", StringComparison.Ordinal);
        var catalog = CatalogLoader.LoadText(text.TrimEnd() + "\n" + Access, "sales.catalog.yaml");
        _models.Add(catalog, new Dictionary<string, string> { ["sqlserver_sales"] = "warehouse" });
        _models.MaskKey = () => [1, 2, 3, 4, 5, 6, 7, 8];
        _services = new TestServices(_host).WithSemantic(_models);
    }

    public void Dispose() => _host.Dispose();

    private Task<IServingAnswer> Run(ServingCaller caller, string json) => _services.Queries.RunAsync(new ServingContext(caller), new ServingRequest.Sql("semantic:sales_catalog", json));

    private static async Task<List<string>> Rows(IServingAnswer answer)
    {
        await using var _ = answer;
        var rows = new List<string>();
        await foreach (var batch in answer.ReadBatchesAsync())
            for (var r = 0; r < batch.Length; r++)
                rows.Add(string.Join("|", Enumerable.Range(0, batch.ColumnCount).Select(c => batch.Column(c) switch
                {
                    StringArray a => a.IsNull(r) ? "<null>" : a.GetString(r),
                    Int64Array a => a.GetValue(r)!.Value.ToString(),
                    DoubleArray a => a.GetValue(r)!.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                    var other => other.GetType().Name,
                })));
        return rows;
    }

    private async Task<List<string>> Describe(ServingCaller caller) =>
        (await _services.Catalog.DescribeAsync(new ServingContext(caller), [], source: "semantic:sales_catalog").ToListAsync()).Select(o => $"{o.Kind.ToString().ToLowerInvariant()}|{string.Join('.', o.Path.Skip(1))}|{(o.Properties?.TryGetValue("access", out var a) == true ? a : "")}").ToList();

    [Fact]
    public async Task ACallerWithNoColumnRuleSeesEveryColumnAsItIs()
    {
        var rows = await Rows(await Run(Everyone, ByCustomer));
        Assert.Contains(rows, r => r.StartsWith("C1|"));
        Assert.Equal(3, rows.Count);
        Assert.Contains("column|customers.customer|", await Describe(Everyone));
        Assert.Equal("-", SemanticColumnAccess.Fingerprint(_models.Find("sales_catalog")!, Everyone));
    }

    [Fact]
    public async Task AHiddenColumnIsMissingFromTheDescriptionAndUnknownToEveryRequestKind()
    {
        var described = await Describe(Guest);
        Assert.DoesNotContain(described, d => d.Contains("customers.customer", StringComparison.Ordinal));
        Assert.DoesNotContain(described, d => d.Contains("orders.status", StringComparison.Ordinal));
        Assert.Contains("column|customers.region|", described);                       // what the rules leave is described as it is
        Assert.Contains(described, d => d.StartsWith("measure|orders.net_revenue", StringComparison.Ordinal));

        // grouped, filtered, sorted, a members listing, semantic SQL: all say what an unknown column says, and none says that it exists
        var requests = new[]
        {
            ByCustomer,
            """{"spec_version": "0.3", "kind": "query", "query": {"measures": ["orders.order_count"], "filters": [{"attribute": "customers.customer", "operator": "equals", "values": ["C1"]}], "limit": 10}}""",
            """{"kind": "members", "attribute": "customers.customer"}""",
            "SELECT customers.customer, MEASURE(orders.order_count) FROM sales_catalog GROUP BY ALL",
        };
        var unknown = await Assert.ThrowsAsync<ServingInvalidException>(() => Run(Guest, """{"spec_version": "0.3", "kind": "query", "query": {"measures": ["orders.order_count"], "attributes": ["customers.no_such_column"], "limit": 10}}"""));
        foreach (var request in requests)
        {
            var refused = await Assert.ThrowsAsync<ServingInvalidException>(() => Run(Guest, request));
            Assert.DoesNotContain("hidden", refused.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("access", refused.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("unknown", unknown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, (await Rows(await Run(Guest, ByRegion))).Count);   // the other columns still answer
    }

    [Fact]
    public async Task AMeasureOverAHiddenColumnIsHiddenAndAnAggregateOverItIsRefusedWithoutSayingItExists()
    {
        var described = await Describe(Guest);
        Assert.DoesNotContain(described, d => d.Contains("orders.statuses", StringComparison.Ordinal));
        Assert.DoesNotContain(described, d => d.Contains("orders.completed_revenue", StringComparison.Ordinal));   // it filters on the hidden status
        Assert.Contains(await Describe(Everyone), d => d.Contains("orders.statuses", StringComparison.Ordinal));

        var refused = await Assert.ThrowsAsync<ServingInvalidException>(() => Run(Guest, """{"spec_version": "0.3", "kind": "query", "query": {"measures": ["orders.statuses"], "attributes": ["customers.region"], "limit": 10}}"""));
        Assert.Contains("orders.statuses", refused.Message, StringComparison.Ordinal);
        Assert.Equal(refused.Message.Replace("orders.statuses", "orders.nothing", StringComparison.Ordinal),
            (await Assert.ThrowsAsync<ServingInvalidException>(() => Run(Guest, """{"spec_version": "0.3", "kind": "query", "query": {"measures": ["orders.nothing"], "attributes": ["customers.region"], "limit": 10}}"""))).Message);
    }

    [Fact]
    public async Task ANulledColumnIsDescribedWithItsTypeAndAnsweredAsNull()
    {
        Assert.Contains("column|customers.region|null", await Describe(Nulled));
        var rows = await Rows(await Run(Nulled, ByRegion));
        Assert.Single(rows);                                                          // one group: the nulled values are one value
        Assert.StartsWith("<null>|", rows.Single(), StringComparison.Ordinal);
        var answer = await Run(Nulled, ByRegion);
        Assert.IsType<StringType>(answer.Schema.GetFieldByName("customers_region").DataType);
        await answer.DisposeAsync();
    }

    [Fact]
    public async Task AFixedMaskIsOneConstantSoTheLengthIsHiddenAndAGroupByGivesOneGroup()
    {
        Assert.Contains("column|customers.customer|mask:fixed", await Describe(Intern));
        var rows = await Rows(await Run(Intern, ByCustomer));
        var raw = await Rows(await Run(Everyone, ByCustomer));
        Assert.Single(rows);
        Assert.StartsWith("********|", rows.Single(), StringComparison.Ordinal);
        Assert.Equal(raw.Sum(r => long.Parse(r.Split('|')[1])), long.Parse(rows.Single().Split('|')[1]));
    }

    [Fact]
    public async Task AHashMaskIsDeterministicNeverTheValueAndStillGroups()
    {
        Assert.Contains("column|customers.customer|mask:hash", await Describe(Contractor));
        var first = await Rows(await Run(Contractor, ByCustomer));
        var second = await Rows(await Run(Contractor, ByCustomer));
        var raw = await Rows(await Run(Everyone, ByCustomer));
        Assert.Equal(first.OrderBy(x => x), second.OrderBy(x => x));                  // deterministic
        Assert.Equal(raw.Count, first.Count);                                          // it groups as the real values do
        Assert.All(first, r => Assert.Matches("^#[0-9a-f]{16}\\|", r));
        Assert.DoesNotContain(first, r => r.Contains("C1", StringComparison.Ordinal));
        Assert.Equal(first.Select(r => r.Split('|')[0]).Distinct().Count(), first.Count);

        _models.MaskKey = () => [9, 9, 9];
        var otherKey = await Rows(await Run(Contractor, ByCustomer));
        Assert.NotEqual(first.Select(r => r.Split('|')[0]).Order(), otherKey.Select(r => r.Split('|')[0]).Order());    // keyed: another install, another mask

        var members = await Rows(await Run(Contractor, """{"kind": "members", "attribute": "customers.customer"}"""));
        Assert.All(members, m => Assert.StartsWith("#", m, StringComparison.Ordinal));
        await Assert.ThrowsAsync<ServingInvalidException>(() => Run(Contractor, """{"kind": "members", "attribute": "customers.customer", "search": "C1"}"""));
        Assert.Equal(["********"], await Rows(await Run(Intern, """{"kind": "members", "attribute": "customers.customer"}""")));
    }

    [Fact]
    public async Task ACallerCannotFilterSortOrJoinByAMaskedOrNulledColumn()
    {
        var byFilter = """{"spec_version": "0.3", "kind": "query", "query": {"measures": ["orders.order_count"], "attributes": ["customers.region"], "filters": [{"attribute": "customers.customer", "operator": "equals", "values": ["C1"]}], "limit": 10}}""";
        var byOrder = """{"spec_version": "0.3", "kind": "query", "query": {"measures": ["orders.order_count"], "attributes": ["customers.customer"], "order": [["customers.customer", "asc"]], "limit": 10}}""";
        foreach (var caller in new[] { Intern, Contractor })
        {
            var filtered = await Assert.ThrowsAsync<ServingInvalidException>(() => Run(caller, byFilter));
            Assert.Contains("customers.customer", filtered.Message, StringComparison.Ordinal);
            await Assert.ThrowsAsync<ServingInvalidException>(() => Run(caller, byOrder));
            await Assert.ThrowsAsync<ServingInvalidException>(() => Run(caller, "SELECT customers.customer, MEASURE(orders.order_count) FROM sales_catalog WHERE customers.customer = 'C1' GROUP BY ALL"));
            await Assert.ThrowsAsync<ServingInvalidException>(() => Run(caller, "SELECT customers.customer, MEASURE(orders.order_count) FROM sales_catalog GROUP BY ALL ORDER BY customers.customer"));
        }

        await Assert.ThrowsAsync<ServingInvalidException>(() => Run(Nulled, """{"spec_version": "0.3", "kind": "query", "query": {"measures": ["orders.order_count"], "filters": [{"attribute": "customers.region", "operator": "equals", "values": ["West"]}], "limit": 10}}"""));
        Assert.Equal(3, (await Rows(await Run(Everyone, byOrder))).Count);                  // the same request is fine for a caller with no rule
    }

    [Fact]
    public async Task TheStrongestTreatmentWinsAndAPersonRuleNamesThePerson()
    {
        // hide beats mask, mask:fixed beats mask:hash, a person is named by id or name
        var both = Caller("u5", "bea", "interns", "guests");
        Assert.DoesNotContain(await Describe(both), d => d.Contains("customers.customer", StringComparison.Ordinal));
        var maskBoth = Caller("u6", "mo", "contractors", "interns");
        Assert.Contains("column|customers.customer|mask:fixed", await Describe(maskBoth));
        Assert.DoesNotContain(await Describe(Caller("u7", "Dana")), d => d.Contains("customers.customer", StringComparison.Ordinal));
        Assert.DoesNotContain(await Describe(Caller("dana", "somebody")), d => d.Contains("customers.customer", StringComparison.Ordinal));
        Assert.Contains("column|customers.customer|", await Describe(Caller("u8", "danae")));
    }

    [Fact]
    public async Task TwoCallersWithDifferentColumnRulesGetTwoDescriptionsTwoAnswersAndTwoFingerprints()
    {
        var model = _models.Find("sales_catalog")!;
        var fingerprints = new[] { Everyone, Intern, Contractor, Nulled, Guest }.Select(c => SemanticColumnAccess.Fingerprint(model, c)).ToList();
        Assert.Equal(fingerprints.Count, fingerprints.Distinct().Count());
        Assert.Equal(SemanticColumnAccess.Fingerprint(model, Intern), SemanticColumnAccess.Fingerprint(model, Caller("someone-else", "else", "INTERNS")));   // alike callers share

        Assert.NotEqual(await Describe(Everyone), await Describe(Intern));
        Assert.NotEqual(await Rows(await Run(Everyone, ByCustomer)), await Rows(await Run(Intern, ByCustomer)));
    }

    [Fact]
    public async Task TheCacheKeepsTheAnswersOfCallersWithDifferentTreatmentsApart()
    {
        var model = _models.Find("sales_catalog")!;
        var cache = new MemoryServingCache();
        var cached = new CachingServingQueries(_services.Queries, cache, new RulesScope(c => SemanticColumnAccess.Fingerprint(model, c.Caller)));
        async Task<(List<string> Rows, string? Cache)> Ask(ServingCaller caller)
        {
            var answer = await cached.RunAsync(new ServingContext(caller), new ServingRequest.Sql("semantic:sales_catalog", ByCustomer));
            var kind = answer.Provenance.Cache;
            var rows = await Rows(answer);
            return (rows, kind);
        }

        var raw = await Ask(Everyone);
        var masked = await Ask(Intern);
        var rawAgain = await Ask(Caller("u9", "another", "staff"));          // a caller with no column rule shares the entry of the one with none
        var maskedAgain = await Ask(Caller("u10", "ivan", "interns"));       // and so do callers with the same treatment
        var hashed = await Ask(Contractor);

        Assert.NotEqual(raw.Rows, masked.Rows);
        Assert.Null(raw.Cache);
        Assert.Null(masked.Cache);                                           // a miss: it was not served from the entry made for another treatment
        Assert.Equal("hit", rawAgain.Cache);
        Assert.Equal(raw.Rows, rawAgain.Rows);
        Assert.Equal("hit", maskedAgain.Cache);
        Assert.Equal(masked.Rows, maskedAgain.Rows);
        Assert.Null(hashed.Cache);
        Assert.NotEqual(masked.Rows, hashed.Rows);
    }

    private sealed class RulesScope(Func<ServingContext, string> access) : IServingCachePolicy
    {
        public ServingCachePlan? PlanFor(ServingContext context, ServingRequest request) => new(TimeSpan.FromMinutes(5), ServingCacheScope.Rules, "1", null, access(context));
    }
}
