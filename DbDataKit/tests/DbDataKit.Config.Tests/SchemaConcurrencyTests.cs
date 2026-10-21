namespace DbDataKit.Config.Tests;

/// <summary>
/// The schema cache must hand out finished schemas only. It used to publish an object schema before its properties were added (so
/// a type could refer back to itself), and its lock-free read let another thread take the half-built one: "Unknown key `timeout`"
/// from a binder that had seen three of the type's twelve properties.
/// </summary>
public class SchemaConcurrencyTests
{
    public class Leaf<T>
    {
        public int A { get; set; }
        public string? B { get; set; }
    }

    public class Wide<T>
    {
        public Leaf<T> P01 { get; set; } = new();
        public List<Leaf<T>> P02 { get; set; } = [];
        public Dictionary<string, Leaf<T>> P03 { get; set; } = [];
        public int P04 { get; set; }
        public string? P05 { get; set; }
        public bool P06 { get; set; }
        public double P07 { get; set; }
        public List<string> P08 { get; set; } = [];
        public Leaf<T>? P09 { get; set; }
        public int? P10 { get; set; }
        public TimeSpan? P11 { get; set; }
        public Wide<T>? P12 { get; set; }     // refers back to itself
    }

    private const int Properties = 12;

    private static IEnumerable<Type> FreshTypes() =>
        typeof(object).Assembly.GetExportedTypes()
            .Where(t => !t.IsGenericTypeDefinition && !t.IsByRefLike && !t.IsPointer && !t.IsByRef)
            .Select(t => typeof(Wide<>).MakeGenericType(t));

    [Fact]
    public async Task A_schema_read_while_another_thread_builds_it_is_always_complete()
    {
        var incomplete = new List<string>();
        var types = FreshTypes().Take(150).ToList();
        Assert.True(types.Count >= 100, "not enough distinct types to build fresh schemas from");

        foreach (var type in types)
        {
            using var start = new ManualResetEventSlim();
            var stop = false;
            var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
            {
                start.Wait();
                while (!Volatile.Read(ref stop))
                {
                    var schema = SchemaModel.ForObject(type);
                    var seen = schema.Properties.Count;
                    if (seen != Properties)
                    {
                        lock (incomplete) incomplete.Add($"{type.GenericTypeArguments[0].Name}: saw {seen} of {Properties} properties");
                        return;
                    }
                }
            })).ToArray();

            start.Set();
            var built = SchemaModel.ForObject(type);
            Volatile.Write(ref stop, true);
            await Task.WhenAll(readers);
            Assert.Equal(Properties, built.Properties.Count);
        }

        Assert.True(incomplete.Count == 0, string.Join("\n", incomplete.Take(5)));
    }

    [Fact]
    public void A_type_that_refers_back_to_itself_still_resolves_to_one_finished_schema()
    {
        var schema = SchemaModel.ForObject<Wide<SchemaConcurrencyTests>>();

        Assert.Equal(Properties, schema.Properties.Count);
        Assert.Same(schema, schema.Find("p12")!.Type);
    }

    public class Unbuildable
    {
        public Abstract Thing { get; set; } = null!;
    }

    public abstract class Abstract;

    [Fact]
    public void A_failed_build_leaves_nothing_half_built_behind()
    {
        Assert.Throws<InvalidOperationException>(() => SchemaModel.ForObject<Unbuildable>());

        // Neither the failed type nor anything built on the way to it is served from the cache afterwards.
        Assert.Throws<InvalidOperationException>(() => SchemaModel.ForObject<Unbuildable>());
        var ok = SchemaModel.ForObject<Leaf<int>>();
        Assert.Equal(2, ok.Properties.Count);
    }
}
