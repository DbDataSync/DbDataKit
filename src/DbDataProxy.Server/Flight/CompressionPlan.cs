using Apache.Arrow;
using Apache.Arrow.Compression;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Server;
using Apache.Arrow.Ipc;
using DbDataProxy.Server.Configuration;
using Grpc.Core;

namespace DbDataProxy.Server.Flight;

/// <summary>The compression applied to one result stream.</summary>
public sealed record CompressionPlan(CompressionMode Mode, string Codec, int? Level)
{
    public const string ClientHeader = "x-compression";

    public static readonly CompressionPlan None = new(CompressionMode.None, "none", null);

    public override string ToString() => Mode == CompressionMode.None ? "none" : $"{Mode}:{Codec}";

    /// <summary>
    /// Picks the plan for a request: the data source's settings if present, otherwise the global ones; then, if the
    /// effective settings allow it, the client's <c>x-compression</c> header wins.
    /// </summary>
    public static CompressionPlan Resolve(CompressionOptions global, CompressionOptions? perSource, string? clientChoice)
    {
        var effective = perSource ?? global;
        if (effective.AllowClientOverride && !string.IsNullOrWhiteSpace(clientChoice))
            return FromName(clientChoice.Trim(), effective.Level);
        return FromOptions(effective);
    }

    public static CompressionPlan FromOptions(CompressionOptions o) => o.Mode switch
    {
        CompressionMode.None => None,
        CompressionMode.Grpc => Validated(CompressionMode.Grpc, o.Codec, o.Level),
        CompressionMode.Ipc => Validated(CompressionMode.Ipc, o.Codec, o.Level),
        _ => throw new ArgumentOutOfRangeException(nameof(o)),
    };

    private static CompressionPlan FromName(string name, int? level) => name.ToLowerInvariant() switch
    {
        "none" or "off" or "identity" => None,
        "gzip" => new(CompressionMode.Grpc, "gzip", level),
        "zstd" => new(CompressionMode.Ipc, "zstd", level),
        "lz4" => new(CompressionMode.Ipc, "lz4", level),
        _ => throw new RpcException(new Status(StatusCode.InvalidArgument,
            $"Unsupported {ClientHeader} value '{name}'. Use none, gzip, zstd or lz4.")),
    };

    private static CompressionPlan Validated(CompressionMode mode, string codec, int? level)
    {
        string c = codec.ToLowerInvariant();
        bool ok = mode == CompressionMode.Grpc ? c == "gzip" : c is "zstd" or "lz4";
        if (!ok) throw new InvalidOperationException($"Codec '{codec}' is not valid for compression mode {mode}.");
        return new CompressionPlan(mode, c, level);
    }

    /// <summary>
    /// Starts the response stream with this plan applied. Must be called before any batch is written.
    /// </summary>
    public async Task StartStreamAsync(FlightServerRecordBatchStreamWriter writer, Schema schema, ServerCallContext context)
    {
        switch (Mode)
        {
            case CompressionMode.Grpc:
                // The server compresses all responses with gzip when the client accepts it (see ProxyHost); this plan keeps that.
                await writer.SetupStream(schema).ConfigureAwait(false);
                break;
            case CompressionMode.Ipc:
                context.WriteOptions = new WriteOptions(WriteFlags.NoCompress);
                await writer.SetupStream(schema).ConfigureAwait(false);
                FlightIpcCompression.Apply(writer, ToIpcOptions());
                break;
            default:
                context.WriteOptions = new WriteOptions(WriteFlags.NoCompress);
                await writer.SetupStream(schema).ConfigureAwait(false);
                break;
        }
    }

    private IpcOptions ToIpcOptions() => new()
    {
        CompressionCodec = Codec == "zstd" ? CompressionCodecType.Zstd : CompressionCodecType.Lz4Frame,
        CompressionCodecFactory = SharedFactory,
        CompressionLevel = Level,
    };

    private static readonly CompressionCodecFactory SharedFactory = new();
}
