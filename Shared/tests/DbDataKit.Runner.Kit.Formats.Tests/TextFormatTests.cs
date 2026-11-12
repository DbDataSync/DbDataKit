using System.Text;
using DbDataKit.Runner.Kit.Formats;

namespace DbDataKit.Runner.Kit.Formats.Tests;

internal static class Helpers
{
    public static MemoryStream Bytes(string text) => new(new UTF8Encoding(false).GetBytes(text));

    public static async Task<List<Row>> ToListAsync(this IAsyncEnumerable<Row> rows)
    {
        var list = new List<Row>();
        await foreach (var row in rows) list.Add(row);
        return list;
    }

    public static string Text(this MemoryStream s) => Encoding.UTF8.GetString(s.ToArray());

    public static object?[][] Values(this IEnumerable<Row> rows) => [.. rows.Select(r => r.Values.ToArray())];
}

public class DelimitedTests
{
    [Fact]
    public async Task Csv_round_trips_quotes_line_breaks_unicode_and_the_difference_between_empty_and_missing()
    {
        string?[][] rows = [["1", "plain", "a,b"], ["2", "say \"hi\"", "two\nlines"], ["3", "", null], ["4", "héllo wörld ✓", "tab\there"], ["5", "\r\nstarts with break", "x"]];
        var options = DelimitedOptions.Csv with { EmptyAsNull = true };
        var stream = new MemoryStream();
        await using (var writer = await DelimitedWriter.CreateAsync(stream, ["id", "name", "note"], options))
        {
            foreach (var r in rows) await writer.WriteAsync(r);
            await writer.FinishAsync();
            Assert.Equal(5, writer.Written);
        }

        stream.Position = 0;
        var read = await DelimitedReader.ReadAsync(stream, options).ToListAsync();
        Assert.Equal(5, read.Count);
        Assert.Equal(["id", "name", "note"], read[0].Columns);
        for (var i = 0; i < rows.Length; i++) Assert.Equal(rows[i], read[i].Values.Cast<string?>().ToArray());
        Assert.Null(read[2]["note"]);
        Assert.Equal("", read[2]["name"]);
        Assert.Equal(1, read[0].Number);
        Assert.Equal([2L, 3L, 5L], read.Take(3).Select(r => r.Line).ToArray());   // the header is line 1; record 2 holds a line break, so record 3 starts on line 5
    }

    [Fact]
    public async Task Tsv_has_no_quoting_and_refuses_to_write_a_tab_in_a_value()
    {
        var stream = new MemoryStream();
        await using (var writer = await DelimitedWriter.CreateAsync(stream, ["a", "b"], DelimitedOptions.Tsv))
        {
            await writer.WriteAsync(["x", "has \"quote\""]);
            var ex = await Assert.ThrowsAsync<FileFormatException>(async () => await writer.WriteAsync(["x\ty", "z"]));
            Assert.Contains("column a", ex.Message);
            Assert.DoesNotContain("x\ty", ex.Message);
            await writer.FinishAsync();
        }

        Assert.Equal("a\tb\nx\thas \"quote\"\n", stream.Text());
        stream.Position = 0;
        var rows = await DelimitedReader.ReadAsync(stream, DelimitedOptions.Tsv).ToListAsync();
        Assert.Equal("has \"quote\"", rows.Single()["b"]);
    }

    [Fact]
    public async Task A_byte_order_mark_and_crlf_are_read_and_blank_lines_are_not_records()
    {
        var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("a,b\r\n1,2\r\n\r\n3,4\r\n")).ToArray();
        var rows = await DelimitedReader.ReadAsync(new MemoryStream(bytes)).ToListAsync();
        Assert.Equal(["a", "b"], rows[0].Columns);
        Assert.Equal(2, rows.Count);
        Assert.Equal("3", rows[1]["a"]);
        Assert.Equal(2, rows[1].Number);
    }

    [Fact]
    public async Task Without_a_header_the_columns_are_given_or_numbered()
    {
        var given = await DelimitedReader.ReadAsync(Helpers.Bytes("1,2\n3,4\n"), new DelimitedOptions { HasHeader = false, Columns = ["x", "y"] }).ToListAsync();
        Assert.Equal(["x", "y"], given[0].Columns);
        Assert.Equal(2, given.Count);
        var numbered = await DelimitedReader.ReadAsync(Helpers.Bytes("1,2,3\n"), new DelimitedOptions { HasHeader = false }).ToListAsync();
        Assert.Equal(["column1", "column2", "column3"], numbered[0].Columns);
    }

    private const string Malformed = "id,name\n1,ok\n2,too,many\n3,\"never closed\n4,fine\n";

    [Fact]
    public async Task A_bad_record_is_refused_by_default_with_its_row_and_line()
    {
        var ex = await Assert.ThrowsAsync<BadRowException>(() => DelimitedReader.ReadAsync(Helpers.Bytes(Malformed), read: new ReadOptions { File = "orders.csv" }).ToListAsync());
        Assert.Equal(2, ex.Row.Row);
        Assert.Equal(3, ex.Row.Line);
        Assert.Contains("orders.csv", ex.Message);
        Assert.Contains("row 2 (line 3)", ex.Message);
        Assert.DoesNotContain("too,many", ex.Message);
        Assert.False(ex.IsTemporary);
    }

    [Fact]
    public async Task Skip_counts_the_bad_records_and_goes_on()
    {
        var bad = new BadRows();
        var rows = await DelimitedReader.ReadAsync(Helpers.Bytes(Malformed), read: new ReadOptions { OnBadRow = BadRowDecision.Skip, Bad = bad }).ToListAsync();
        Assert.Equal(["1"], rows.Select(r => (string)r["id"]!).ToArray());
        Assert.Equal(2, bad.Count);        // the extra field, and the quote never closed (the rest of the file is inside it)
        Assert.Empty(bad.Rows);
    }

    [Fact]
    public async Task Collect_keeps_position_reason_and_text()
    {
        var bad = new BadRows();
        await DelimitedReader.ReadAsync(Helpers.Bytes("id,name\n1,ok\n2,too,many\n3,fine\n"), read: new ReadOptions { OnBadRow = BadRowDecision.Collect, Bad = bad }).ToListAsync();
        var one = Assert.Single(bad.Rows);
        Assert.Equal(2, one.Row);
        Assert.Equal(3, one.Line);
        Assert.Contains("3 fields", one.Reason);
        Assert.Equal("2,too,many", one.Text);
    }

    [Fact]
    public async Task A_quote_in_the_middle_of_a_field_and_text_after_a_closing_quote_are_bad_records()
    {
        var bad = new BadRows();
        var rows = await DelimitedReader.ReadAsync(Helpers.Bytes("a,b\n1,x\"y\n2,\"q\"z\n3,ok\n"), read: new ReadOptions { OnBadRow = BadRowDecision.Collect, Bad = bad }).ToListAsync();
        Assert.Single(rows);
        Assert.Equal([2L, 3L], bad.Rows.Select(b => b.Line).ToArray());
        Assert.All(bad.Rows, b => Assert.True(b.Column > 0));
    }

    [Fact]
    public async Task The_collected_rows_are_limited_but_all_are_counted()
    {
        var bad = new BadRows(limit: 2);
        var text = "a,b\n" + string.Concat(Enumerable.Repeat("1,2,3\n", 5)) + "7,8\n";
        var rows = await DelimitedReader.ReadAsync(Helpers.Bytes(text), read: new ReadOptions { OnBadRow = BadRowDecision.Collect, Bad = bad }).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(5, bad.Count);
        Assert.Equal(2, bad.Rows.Count);
    }

    [Fact]
    public async Task A_header_that_cannot_be_read_fails_the_file_whatever_the_decision()
    {
        await Assert.ThrowsAsync<FileFormatException>(() => DelimitedReader.ReadAsync(Helpers.Bytes("\"a,b\n1,2\n"), read: new ReadOptions { OnBadRow = BadRowDecision.Skip }).ToListAsync());
    }

    [Fact]
    public async Task A_gigabyte_streams_in_bounded_memory()
    {
        // a stream that makes a 1 GiB file out of one repeated line and counts what was asked of it: the reader may hold only a buffer, so the bytes read stay
        // within a small distance of the bytes of the rows handed out, however large the file
        var line = Encoding.UTF8.GetBytes("0000012345,customer-0000012345,12.50,\"a description, with a comma, of about one hundred and twenty characters, so the file has fewer rows\"\n");
        var header = Encoding.UTF8.GetBytes("id,name,amount,description\n");
        long total = header.Length + ((1L << 30) - header.Length) / line.Length * line.Length;   // whole records, about 1 GiB
        var source = new CountingStream(header, line, total);
        long rows = 0, worst = 0;
        await foreach (var row in DelimitedReader.ReadAsync(source))
        {
            rows++;
            if ((rows & 0xFFFF) == 0) worst = Math.Max(worst, source.Produced - header.Length - rows * line.Length);
            Assert.Equal(4, row.Values.Count);
        }

        Assert.Equal((total - header.Length) / line.Length, rows);
        Assert.True(source.Produced >= total - line.Length);
        Assert.InRange(worst, 0, 512 * 1024);
    }

    private sealed class CountingStream(byte[] header, byte[] line, long total) : Stream
    {
        public long Produced { get; private set; }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = 0;
            while (n < buffer.Length && Produced < total)
            {
                var source = Produced < header.Length ? header.AsSpan((int)Produced) : line.AsSpan((int)((Produced - header.Length) % line.Length));
                var take = (int)Math.Min(Math.Min(source.Length, buffer.Length - n), total - Produced);
                source[..take].CopyTo(buffer[n..]);
                n += take;
                Produced += take;
            }

            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(Read(buffer.Span));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => total;
        public override long Position { get => Produced; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public class FixedWidthTests
{
    private static readonly FixedWidthOptions Layout = new()
    {
        Columns = [new("id", 0, 5, RightAlign: true), new("name", 5, 10), new("amount", 15, 8, RightAlign: true)],
    };

    [Fact]
    public async Task Fixed_width_round_trips_and_pads_to_the_layout()
    {
        var stream = new MemoryStream();
        await using (var writer = await FixedWidthWriter.CreateAsync(stream, Layout))
        {
            await writer.WriteAsync([1, "Ann", 12.5m]);
            await writer.WriteAsync([22, null, 3m]);
            await writer.FinishAsync();
        }

        Assert.Equal("    1Ann           12.5\n   22                 3\n", stream.Text());
        stream.Position = 0;
        var rows = await FixedWidthReader.ReadAsync(stream, Layout).ToListAsync();
        Assert.Equal(["1", "Ann", "12.5"], rows[0].Values.Cast<string>().ToArray());
        Assert.Null(rows[1]["name"]);
        Assert.Equal(2, rows[1].Line);
    }

    [Fact]
    public async Task A_short_line_is_a_bad_record_unless_short_lines_are_allowed()
    {
        var text = "    1Ann           12.5\n   22Bo\n    3Cy             9.9\n";
        var ex = await Assert.ThrowsAsync<BadRowException>(() => FixedWidthReader.ReadAsync(Helpers.Bytes(text), Layout, new ReadOptions { File = "pay.txt" }).ToListAsync());
        Assert.Equal(2, ex.Row.Line);
        Assert.Contains("pay.txt", ex.Message);
        var bad = new BadRows();
        var rows = await FixedWidthReader.ReadAsync(Helpers.Bytes(text), Layout, new ReadOptions { OnBadRow = BadRowDecision.Collect, Bad = bad }).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal("   22Bo", bad.Rows.Single().Text);
        var lenient = await FixedWidthReader.ReadAsync(Helpers.Bytes(text), Layout with { AllowShortLines = true }).ToListAsync();
        Assert.Equal(3, lenient.Count);
        Assert.Null(lenient[1]["amount"]);
    }

    [Fact]
    public async Task A_value_wider_than_its_column_is_refused_not_cut()
    {
        await using var writer = await FixedWidthWriter.CreateAsync(new MemoryStream(), Layout);
        var ex = await Assert.ThrowsAsync<FileFormatException>(async () => await writer.WriteAsync([1, "far too long a name", 1m]));
        Assert.Contains("column name", ex.Message);
        Assert.DoesNotContain("far too long", ex.Message);
    }
}

public class JsonAndXmlTests
{
    [Fact]
    public async Task Json_array_round_trips_with_types_and_nested_values()
    {
        var stream = new MemoryStream();
        await using (var writer = JsonArrayWriter.Create(stream, ["id", "name", "price", "ok", "note", "when"]))
        {
            await writer.WriteAsync([1L, "a \"b\"", 2.5m, true, null, new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc)]);
            await writer.WriteAsync([2L, "é", 3m, false, "x", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)]);
            await writer.FinishAsync();
        }

        stream.Position = 0;
        var rows = await JsonRows.ReadArrayAsync(stream).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(1L, rows[0]["id"]);
        Assert.Equal(2.5m, rows[0]["price"]);
        Assert.Equal(true, rows[0]["ok"]);
        Assert.Null(rows[0]["note"]);
        Assert.Equal("a \"b\"", rows[0]["name"]);
        Assert.Equal("2026-11-11T10:00:00Z", rows[0]["when"]);
        var nested = await JsonRows.ReadArrayAsync(Helpers.Bytes("[{\"a\":{\"b\":[1,2]},\"c\":1}]")).ToListAsync();
        Assert.Equal("{\"b\":[1,2]}", nested[0]["a"]);
    }

    [Fact]
    public async Task A_json_array_larger_than_the_buffer_reads_every_element_including_one_larger_than_the_buffer()
    {
        var stream = new MemoryStream();
        await using (var writer = JsonArrayWriter.Create(stream, ["id", "text"]))
        {
            for (var i = 0; i < 20_000; i++) await writer.WriteAsync([(long)i, "row " + i]);
            await writer.WriteAsync([20_000L, new string('x', 300_000)]);
            await writer.WriteAsync([20_001L, "last"]);
            await writer.FinishAsync();
        }

        stream.Position = 0;
        var ids = new List<long>();
        await foreach (var row in JsonRows.ReadArrayAsync(stream)) ids.Add((long)row["id"]!);
        Assert.Equal(Enumerable.Range(0, 20_002).Select(i => (long)i), ids);
    }

    [Fact]
    public async Task A_json_element_that_is_not_an_object_is_a_bad_record_and_the_read_goes_on()
    {
        var bad = new BadRows();
        var rows = await JsonRows.ReadArrayAsync(Helpers.Bytes("[{\"a\":1}, 5, {\"a\":3}]"), new ReadOptions { OnBadRow = BadRowDecision.Collect, Bad = bad }).ToListAsync();
        Assert.Equal([1L, 3L], rows.Select(r => (long)r["a"]!).ToArray());
        Assert.Equal(2, bad.Rows.Single().Row);
        await Assert.ThrowsAsync<BadRowException>(() => JsonRows.ReadArrayAsync(Helpers.Bytes("[{\"a\":1}, \"x\"]")).ToListAsync());
    }

    [Fact]
    public async Task A_json_syntax_error_is_reported_with_its_line_and_ends_the_read()
    {
        var bad = new BadRows();
        var rows = await JsonRows.ReadArrayAsync(Helpers.Bytes("[{\"a\":1},\n{\"a\":2},\n{\"a\": oops}]"), new ReadOptions { OnBadRow = BadRowDecision.Collect, Bad = bad }).ToListAsync();
        Assert.Equal(2, rows.Count);
        var one = Assert.Single(bad.Rows);
        Assert.Equal(3, one.Row);
        Assert.Equal(3, one.Line);
        var ex = await Assert.ThrowsAsync<BadRowException>(() => JsonRows.ReadArrayAsync(Helpers.Bytes("[{\"a\":1},\n{\"a\": oops}]"), new ReadOptions { File = "feed.json" }).ToListAsync());
        Assert.Contains("feed.json", ex.Message);
        Assert.Equal(2, ex.Row.Line);
    }

    [Fact]
    public async Task Json_lines_round_trip_and_resume_after_a_bad_line()
    {
        var stream = new MemoryStream();
        await using (var writer = JsonLinesWriter.Create(stream, ["id", "t"]))
        {
            await writer.WriteAsync([1L, "one\ntwo"]);
            await writer.WriteAsync([2L, "é"]);
            await writer.FinishAsync();
        }

        Assert.Equal(2, stream.Text().Count(c => c == '\n'));
        stream.Position = 0;
        Assert.Equal("one\ntwo", (await JsonRows.ReadLinesAsync(stream).ToListAsync())[0]["t"]);

        var bad = new BadRows();
        var rows = await JsonRows.ReadLinesAsync(Helpers.Bytes("{\"a\":1}\n{\"a\":\n\n[1]\n{\"a\":4}\n"), new ReadOptions { OnBadRow = BadRowDecision.Collect, Bad = bad }).ToListAsync();
        Assert.Equal([1L, 4L], rows.Select(r => (long)r["a"]!).ToArray());
        Assert.Equal([2L, 4L], bad.Rows.Select(b => b.Line).ToArray());   // the blank line is line 3 and not a record; the array is line 4
    }

    [Fact]
    public async Task Xml_round_trips_names_that_need_encoding_and_attributes_are_read()
    {
        var stream = new MemoryStream();
        var options = new XmlOptions { RecordElement = "row", Indented = true };
        await using (var writer = await XmlWriterOfRows.CreateAsync(stream, ["id", "first name", "note"], options))
        {
            await writer.WriteAsync([1, "Ann <&>", null]);
            await writer.WriteAsync([2, "Bo", "x"]);
            await writer.FinishAsync();
        }

        stream.Position = 0;
        var rows = await XmlRows.ReadAsync(stream, options).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal("Ann <&>", rows[0]["first name"]);
        Assert.False(rows[0].Has("note"));
        Assert.Equal("x", rows[1]["note"]);
        Assert.True(rows[1].Line > rows[0].Line);

        var withAttributes = await XmlRows.ReadAsync(Helpers.Bytes("<o><Order id=\"7\"><Lines><L>1</L></Lines><Total>5</Total></Order></o>"), new XmlOptions { RecordElement = "Order" }).ToListAsync();
        Assert.Equal("7", withAttributes[0]["@id"]);
        Assert.Equal("<Lines><L>1</L></Lines>", withAttributes[0]["Lines"]);
        Assert.Equal("5", withAttributes[0]["Total"]);
    }

    [Fact]
    public async Task Malformed_xml_is_reported_with_its_line_and_a_dtd_is_refused()
    {
        var bad = new BadRows();
        var rows = await XmlRows.ReadAsync(Helpers.Bytes("<r>\n<row><a>1</a></row>\n<row><a>2</b></row>\n</r>"), new XmlOptions { RecordElement = "row" }, new ReadOptions { OnBadRow = BadRowDecision.Collect, Bad = bad }).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(3, bad.Rows.Single().Line);
        Assert.Equal(2, bad.Rows.Single().Row);

        var dtd = "<!DOCTYPE r [<!ENTITY x \"boom\">]><r><row><a>&x;</a></row></r>";
        var refused = await Assert.ThrowsAsync<BadRowException>(() => XmlRows.ReadAsync(Helpers.Bytes(dtd), new XmlOptions { RecordElement = "row" }, new ReadOptions { File = "x.xml" }).ToListAsync());
        Assert.Contains("DTD", refused.Message);
    }
}
