using DbDataKit.Config;

namespace DbDataKit.Serving.FlightSql;

public enum CompressionMode
{
    None,
    /// <summary>gRPC message compression (gzip/deflate). Works with any gRPC client that advertises support.</summary>
    Grpc,
    /// <summary>Arrow data compression (zstd/lz4): each column buffer inside the Arrow stream is compressed. Best ratio for columnar data; clients must read compressed Arrow data.</summary>
    Arrow,
}

[ConfigSchema(Naming = KeyNaming.Verbatim)]
public sealed class CompressionOptions
{
    public CompressionMode Mode { get; set; } = CompressionMode.Arrow;

    /// <summary>For <see cref="CompressionMode.Arrow"/>: "zstd" or "lz4". For <see cref="CompressionMode.Grpc"/>: "gzip" or "deflate".</summary>
    public string Codec { get; set; } = "zstd";

    /// <summary>Codec-specific level; null uses the codec default.</summary>
    public int? Level { get; set; }

    /// <summary>
    /// Lets a client override the mode per request with the "x-compression" header
    /// (values: none, gzip, deflate, zstd, lz4).
    /// </summary>
    public bool AllowClientOverride { get; set; } = true;
}
