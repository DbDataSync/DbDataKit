using Apache.Arrow;
using Apache.Arrow.Compression;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Google.Protobuf;
using static DbDataKit.Connections.FlightSql.FlightDataReader;

namespace DbDataSync.Drivers.ArrowFlightSql.Tests;

/// <summary>
/// Reading what a compression-minded server sends: LZ4- and Zstd-compressed record batches, dictionary
/// and run-end encoding. The messages are built the way Flight carries them — an Arrow IPC stream split
/// into header and body per message — and fed through the same assembler the driver reads a DoGet with.
/// </summary>
public sealed class FlightDataReaderTests
{
    private static readonly Schema TextSchema = new Schema.Builder()
        .Field(new Field("id", Int64Type.Default, nullable: false))
        .Field(new Field("name", StringType.Default, nullable: true))
        .Build();

    /// <summary>Repetitive on purpose: it has to actually compress for the test to mean anything.</summary>
    private static RecordBatch TextBatch(long firstId, int rows)
    {
        var id = new Int64Array.Builder();
        var name = new StringArray.Builder();
        for (var i = 0; i < rows; i++)
        {
            id.Append(firstId + i);
            if (i % 7 == 0) name.AppendNull(); else name.Append($"customer-name-{(firstId + i) % 5}");
        }

        return new RecordBatch(TextSchema, [id.Build(), name.Build()], rows);
    }

    private static byte[] Serialize(Schema schema, IEnumerable<RecordBatch> batches, CompressionCodecType? codec)
    {
        var buffer = new MemoryStream();
        var options = new IpcOptions { CompressionCodec = codec, CompressionCodecFactory = new CompressionCodecFactory() };
        using (var writer = new ArrowStreamWriter(buffer, schema, leaveOpen: true, options))
        {
            writer.WriteStart();
            foreach (var batch in batches)
                writer.WriteRecordBatch(batch);
            writer.WriteEnd();
        }

        return buffer.ToArray();
    }

    /// <summary>An IPC stream cut into its messages: each is <c>[FFFFFFFF][header length][header][body]</c>,
    /// and the body's length is in the header's flatbuffer (<c>Message.bodyLength</c>, field 3).</summary>
    private static List<FlightDataParts> Messages(byte[] stream)
    {
        var parts = new List<FlightDataParts>();
        var at = 0;
        while (at + 8 <= stream.Length)
        {
            var length = BitConverter.ToInt32(stream, at + 4);
            if (length == 0)
                break; // end of stream

            var header = stream.AsMemory(at + 8, length);
            var bodyLength = BodyLength(header.Span);
            parts.Add(new FlightDataParts(
                ByteString.CopyFrom(header.Span), ByteString.CopyFrom(stream, at + 8 + length, (int)bodyLength)));
            at += 8 + length + (int)bodyLength;
        }

        return parts;
    }

    private static long BodyLength(ReadOnlySpan<byte> message)
    {
        var table = BitConverter.ToInt32(message);
        var vtable = table - BitConverter.ToInt32(message[table..]);

        // A field past the end of the vtable is absent, i.e. its default — a schema message has no body.
        var slot = 4 + 3 * 2;
        if (slot + 2 > BitConverter.ToUInt16(message[vtable..]))
            return 0;

        var offset = BitConverter.ToUInt16(message[(vtable + slot)..]);
        return offset == 0 ? 0 : BitConverter.ToInt64(message[(table + offset)..]);
    }

    private static List<RecordBatch> Read(IEnumerable<FlightDataParts> messages)
    {
        var assembler = new IpcStreamAssembler();
        return messages.Select(assembler.Add).OfType<RecordBatch>().ToList();
    }

    // ---- Compression ----

    [Theory]
    [InlineData(null)]
    [InlineData(CompressionCodecType.Lz4Frame)]
    [InlineData(CompressionCodecType.Zstd)]
    public void Reads_Batches_Whatever_Their_Compression(CompressionCodecType? codec)
    {
        var sent = new[] { TextBatch(0, 3000), TextBatch(3000, 2000), TextBatch(5000, 1) };

        var received = Read(Messages(Serialize(TextSchema, sent, codec)));

        Assert.Equal([3000, 2000, 1], received.Select(b => b.Length));
        for (var b = 0; b < sent.Length; b++)
        {
            for (var row = 0; row < sent[b].Length; row += 97)
            {
                Assert.Equal(ArrowValues.ValueAt(sent[b].Column(0), row), ArrowValues.ValueAt(received[b].Column(0), row));
                Assert.Equal(ArrowValues.ValueAt(sent[b].Column(1), row), ArrowValues.ValueAt(received[b].Column(1), row));
            }
        }
    }

    [Theory]
    [InlineData(CompressionCodecType.Lz4Frame)]
    [InlineData(CompressionCodecType.Zstd)]
    public void The_Test_Data_Really_Is_Compressed_And_Needs_The_Codec(CompressionCodecType codec)
    {
        var plain = Serialize(TextSchema, [TextBatch(0, 3000)], null);
        var compressed = Serialize(TextSchema, [TextBatch(0, 3000)], codec);

        // Smaller on the wire — otherwise a pass above would prove nothing...
        Assert.True(compressed.Length < plain.Length / 2, $"{compressed.Length} vs {plain.Length}");

        // ...and unreadable without a codec, which is what the stock Flight client's reader does.
        using var withoutCodec = new ArrowStreamReader(compressed);
        Assert.ThrowsAny<Exception>(() => withoutCodec.ReadNextRecordBatch());
    }

    // ---- Encodings ----

    [Fact]
    public void Resolves_Dictionary_Encoded_Columns_Across_Batches()
    {
        var type = new DictionaryType(Int8Type.Default, StringType.Default, ordered: false);
        var schema = new Schema.Builder().Field(new Field("status", type, nullable: true)).Build();
        var dictionary = new StringArray.Builder().Append("open").Append("shipped").Append("closed").Build();

        RecordBatch Batch(params sbyte[] codes)
        {
            var indices = new Int8Array.Builder();
            foreach (var code in codes) indices.Append(code);
            return new RecordBatch(schema, [new DictionaryArray(type, indices.Build(), dictionary)], codes.Length);
        }

        var received = Read(Messages(Serialize(schema, [Batch(0, 1, 2, 1), Batch(2, 2, 0)], CompressionCodecType.Zstd)));

        var values = received.SelectMany(b => Enumerable.Range(0, b.Length).Select(i => ArrowValues.ValueAt(b.Column(0), i)));
        Assert.Equal(["open", "shipped", "closed", "shipped", "closed", "closed", "open"], values);
    }

    [Fact]
    public void Expands_Run_End_Encoded_Columns_To_One_Value_Per_Row()
    {
        // `A A A B B C C C C` as run ends [3, 5, 9] and values [A, B, C].
        var runEnds = new Int32Array.Builder().Append(3).Append(5).Append(9).Build();
        var values = new StringArray.Builder().Append("A").Append("B").Append("C").Build();
        var column = new RunEndEncodedArray(runEnds, values);

        Assert.Equal(
            ["A", "A", "A", "B", "B", "C", "C", "C", "C"],
            Enumerable.Range(0, 9).Select(i => ArrowValues.ValueAt(column, i)));

        Assert.Equal("varchar", ArrowValues.NativeTypeName(column.Data.DataType));
        Assert.Equal(typeof(string), ArrowValues.ClrType(column.Data.DataType));
    }

    [Fact]
    public void A_Null_Run_Reads_As_Null()
    {
        var runEnds = new Int32Array.Builder().Append(2).Append(4).Build();
        var values = new Int64Array.Builder().Append(7).AppendNull().Build();
        var column = new RunEndEncodedArray(runEnds, values);

        Assert.Equal([7L, 7L, null, null], Enumerable.Range(0, 4).Select(i => ArrowValues.ValueAt(column, i)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(CompressionCodecType.Zstd)]
    public void Run_End_Encoded_Columns_Survive_The_Wire(CompressionCodecType? codec)
    {
        var runEnds = new Int32Array.Builder().Append(3).Append(5).Build();
        var values = new StringArray.Builder().Append("A").Append("B").Build();
        var column = new RunEndEncodedArray(runEnds, values);
        var schema = new Schema.Builder().Field(new Field("c", column.Data.DataType, true)).Build();

        var received = Read(Messages(Serialize(schema, [new RecordBatch(schema, [column], 5)], codec))).Single();

        Assert.Equal(["A", "A", "A", "B", "B"], Enumerable.Range(0, 5).Select(i => ArrowValues.ValueAt(received.Column(0), i)));
    }

    // ---- Framing ----

    [Fact]
    public void A_Message_With_No_Header_Is_Skipped()
    {
        var messages = Messages(Serialize(TextSchema, [TextBatch(0, 10)], null));
        var assembler = new IpcStreamAssembler();

        Assert.Null(assembler.Add(new FlightDataParts(ByteString.Empty, ByteString.Empty)));
        Assert.Null(assembler.Add(messages[0]));
        Assert.Null(assembler.Add(new FlightDataParts(ByteString.Empty, ByteString.Empty)));
        Assert.Equal(10, assembler.Add(messages[1])!.Length);
    }

    [Fact]
    public void Parses_FlightData_Skipping_Fields_It_Does_Not_Need()
    {
        using var buffer = new MemoryStream();
        var output = new CodedOutputStream(buffer);
        output.WriteTag(1, WireFormat.WireType.LengthDelimited); // flight_descriptor
        output.WriteBytes(ByteString.CopyFromUtf8("descriptor"));
        output.WriteTag(2, WireFormat.WireType.LengthDelimited); // data_header
        output.WriteBytes(ByteString.CopyFrom(1, 2, 3));
        output.WriteTag(3, WireFormat.WireType.LengthDelimited); // app_metadata
        output.WriteBytes(ByteString.CopyFromUtf8("meta"));
        output.WriteTag(1000, WireFormat.WireType.LengthDelimited); // data_body
        output.WriteBytes(ByteString.CopyFrom(9, 8, 7, 6));
        output.Flush();

        var parts = FlightDataParts.Parse(buffer.ToArray());

        Assert.Equal(new byte[] { 1, 2, 3 }, parts.Header.ToByteArray());
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, parts.Body.ToByteArray());
    }

    [Fact]
    public void A_Ticket_Is_One_Length_Delimited_Bytes_Field()
    {
        var message = TicketMessage(new Apache.Arrow.Flight.FlightTicket(new byte[] { 5, 6, 7 }));
        Assert.Equal(new byte[] { 0x0A, 3, 5, 6, 7 }, message);
    }
}
