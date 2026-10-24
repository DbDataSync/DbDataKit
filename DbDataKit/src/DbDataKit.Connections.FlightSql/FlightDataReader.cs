using System.Runtime.CompilerServices;
using Apache.Arrow;
using Apache.Arrow.Compression;
using Apache.Arrow.Flight;
using Apache.Arrow.Ipc;
using Google.Protobuf;
using Grpc.Core;

namespace DbDataKit.Connections.FlightSql;

/// <summary>
/// Reads a <c>DoGet</c> stream into record batches — **including compressed ones**.
/// <para>
/// <c>FlightSqlClient.DoGetAsync</c> cannot: the Flight client decodes each message with an Arrow stream
/// reader built without a codec factory, so a batch whose buffers are LZ4- or Zstd-compressed (which is
/// what a compression-minded proxy sends) throws as soon as it is read, whatever packages are installed.
/// So the call is made here, on the channel's raw call invoker, and each Flight message is re-framed into
/// the Arrow IPC stream format — which is exactly what a Flight message is, split into header and body —
/// and read with <see cref="ArrowStreamReader"/> given <see cref="CompressionCodecFactory"/>.
/// </para>
/// <para>
/// The wire messages are small enough to write by hand (a ticket is one bytes field, a
/// <c>FlightData</c> reads three), and the generated gRPC types are internal to the Arrow package, so the
/// call is described with its own marshallers rather than reaching for them.
/// </para>
/// </summary>
internal static class FlightDataReader
{
    private static readonly CompressionCodecFactory Codecs = new();

    private static readonly Method<byte[], FlightDataParts> DoGet = new(
        MethodType.ServerStreaming, "arrow.flight.protocol.FlightService", "DoGet",
        Marshallers.Create<byte[]>(
            (bytes, context) =>
            {
                context.SetPayloadLength(bytes.Length);
                context.Complete(bytes);
            },
            context => context.PayloadAsNewBuffer()),
        Marshallers.Create<FlightDataParts>(
            (_, _) => throw new NotSupportedException("A client never sends FlightData on DoGet."),
            context => FlightDataParts.Parse(context.PayloadAsNewBuffer())));

    public static async IAsyncEnumerable<RecordBatch> ReadAsync(
        CallInvoker invoker, FlightTicket ticket, Metadata headers, DateTime? deadline,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var call = invoker.AsyncServerStreamingCall(
            DoGet, null, new CallOptions(headers, deadline, cancellationToken), TicketMessage(ticket));

        var stream = new IpcStreamAssembler();
        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            if (stream.Add(call.ResponseStream.Current) is { } batch)
                yield return batch;
        }
    }

    /// <summary><c>message Ticket { bytes ticket = 1; }</c></summary>
    internal static byte[] TicketMessage(FlightTicket ticket)
    {
        using var buffer = new MemoryStream();
        var output = new CodedOutputStream(buffer);
        output.WriteTag(1, WireFormat.WireType.LengthDelimited);
        output.WriteBytes(ticket.Ticket);
        output.Flush();
        return buffer.ToArray();
    }

    /// <summary>The two fields of <c>FlightData</c> that matter for reading: the Arrow IPC message
    /// header (field 2) and its body (field 1000). The descriptor and application metadata are skipped.</summary>
    internal sealed record FlightDataParts(ByteString Header, ByteString Body)
    {
        public static FlightDataParts Parse(byte[] payload)
        {
            var input = new CodedInputStream(payload);
            var header = ByteString.Empty;
            var body = ByteString.Empty;

            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (WireFormat.GetTagFieldNumber(tag))
                {
                    case 2:
                        header = input.ReadBytes();
                        break;
                    case 1000:
                        body = input.ReadBytes();
                        break;
                    default:
                        input.SkipLastField();
                        break;
                }
            }

            return new FlightDataParts(header, body);
        }
    }

    /// <summary>
    /// Turns the messages of one Flight stream into record batches.
    /// <para>
    /// A Flight message is an Arrow IPC message already split into its header and body, so framing one is
    /// just putting the IPC envelope back on: a continuation marker, the header's length, the header
    /// padded to 8 bytes, then the body. The first message is the schema. Each record batch is read from
    /// <c>schema + any dictionaries so far + that batch</c> as a stream of its own — a dictionary message
    /// reads as a stream that ends with no batch, which is how it is told apart, and joins the prefix so
    /// every later batch can resolve it.
    /// </para>
    /// </summary>
    internal sealed class IpcStreamAssembler
    {
        private byte[]? _prefix;

        public RecordBatch? Add(FlightDataParts message)
        {
            if (message.Header.IsEmpty)
                return null; // application metadata only

            var framed = Frame(message);
            if (_prefix is null)
            {
                _prefix = framed; // the schema
                return null;
            }

            var stream = new byte[_prefix.Length + framed.Length];
            Buffer.BlockCopy(_prefix, 0, stream, 0, _prefix.Length);
            Buffer.BlockCopy(framed, 0, stream, _prefix.Length, framed.Length);

            using var reader = new ArrowStreamReader(stream, Codecs);
            var batch = reader.ReadNextRecordBatch();
            if (batch is null)
                _prefix = stream; // a dictionary

            return batch;
        }

        private static byte[] Frame(FlightDataParts message)
        {
            var header = message.Header.Span;
            var padded = (header.Length + 7) & ~7;
            var framed = new byte[8 + padded + message.Body.Length];

            BitConverter.TryWriteBytes(framed.AsSpan(0, 4), 0xFFFFFFFFu);
            BitConverter.TryWriteBytes(framed.AsSpan(4, 4), padded);
            header.CopyTo(framed.AsSpan(8));
            message.Body.Span.CopyTo(framed.AsSpan(8 + padded));
            return framed;
        }
    }
}
