using System.Reflection;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Server;
using Apache.Arrow.Ipc;

namespace DbDataProxy.Server.Flight;

/// <summary>
/// Apache.Arrow.Flight 23.0.0 builds its internal <see cref="ArrowStreamWriter"/> with default options and exposes no way
/// to set Arrow data compression, even though the underlying writer supports it. This swaps the writer's options after the
/// stream is set up. It relies on private members, so it is verified at startup (<see cref="EnsureSupported"/>) and by
/// tests; if a package update moves those members the server refuses to enable Arrow data compression instead of silently
/// sending uncompressed data.
/// </summary>
internal static class FlightIpcCompression
{
    private static readonly FieldInfo? FlightDataStreamField =
        typeof(FlightRecordBatchStreamWriter).GetField("_flightDataStream", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo? OptionsField =
        typeof(ArrowStreamWriter).GetField("_options", BindingFlags.NonPublic | BindingFlags.Instance);

    public static bool IsSupported =>
        FlightDataStreamField is not null && OptionsField is not null && typeof(ArrowStreamWriter).IsAssignableFrom(FlightDataStreamField.FieldType);

    public static void EnsureSupported()
    {
        if (!IsSupported)
            throw new NotSupportedException(
                "This Apache.Arrow.Flight version does not expose the members needed for Arrow data compression. " +
                "Use gRPC compression (Compression:Mode=Grpc) or None.");
    }

    /// <summary>Applies <paramref name="options"/> to a writer whose stream has already been set up.</summary>
    public static void Apply(FlightServerRecordBatchStreamWriter writer, IpcOptions options)
    {
        EnsureSupported();
        object? stream = FlightDataStreamField!.GetValue(writer)
            ?? throw new InvalidOperationException("Flight stream was not set up before enabling compression.");
        OptionsField!.SetValue(stream, options);
    }
}
