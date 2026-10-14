using Apache.Arrow;
using DbDataProxy.Tests.Support;
using Grpc.Core;

namespace DbDataProxy.Tests.Flight;

public class CompressionTests(ProxyFixture proxy) : IClassFixture<ProxyFixture>
{
    private const string Sql = "SELECT id, name, score, day FROM numbers ORDER BY id";

    private async Task<(long Bytes, List<RecordBatch> Batches)> MeasureAsync(string? compression)
    {
        using var client = proxy.Connect();
        await client.AuthenticateAsync("alice", ProxyFixture.AlicePassword);
        client.Counter.Reset();
        var (_, batches) = await client.QueryAsync(Sql, compression: compression);
        return (client.Counter.ResponseBytes, batches);
    }

    private static void AssertSameData(List<RecordBatch> expected, List<RecordBatch> actual)
    {
        Assert.Equal(expected.Sum(b => b.Length), actual.Sum(b => b.Length));
        var a = actual[0];
        Assert.Equal(0, ((Int32Array)a.Column("id")).GetValue(0));
        Assert.Equal("name-1", ((StringArray)a.Column("name")).GetString(1));
        Assert.Equal(1.5, ((DoubleArray)a.Column("score")).GetValue(1));
        var last = actual[^1];
        Assert.Equal(9999, ((Int32Array)last.Column("id")).GetValue(last.Length - 1));
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("zstd")]
    [InlineData("lz4")]
    public async Task Compressed_results_are_smaller_on_the_wire_and_decode_to_the_same_data(string codec)
    {
        var (plainBytes, plain) = await MeasureAsync("none");
        var (compressedBytes, compressed) = await MeasureAsync(codec);

        AssertSameData(plain, compressed);
        Assert.True(compressedBytes < plainBytes * 0.8, $"{codec}: {compressedBytes} bytes vs {plainBytes} uncompressed");
    }

    [Fact]
    public async Task Unknown_compression_codec_is_rejected()
    {
        using var client = proxy.Connect();
        await client.AuthenticateAsync("alice", ProxyFixture.AlicePassword);
        var ex = await Rpc.ExpectAsync(() => client.QueryAsync(Sql, compression: "brotli"));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }
}
