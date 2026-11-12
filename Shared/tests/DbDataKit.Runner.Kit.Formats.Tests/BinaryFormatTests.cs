using System.IO.Compression;
using System.Text;
using ClosedXML.Excel;
using DbDataKit.Runner.Kit.Formats;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;

namespace DbDataKit.Runner.Kit.Formats.Tests;

public class CompressionTests
{
    [Fact]
    public async Task Gzip_round_trips_and_a_csv_reads_through_it()
    {
        var packed = new MemoryStream();
        await using (var gz = Compression.Gzip(packed))
        await using (var writer = await DelimitedWriter.CreateAsync(gz, ["a", "b"]))
        {
            await writer.WriteAsync(["1", "two"]);
            await writer.FinishAsync();
        }

        packed.Position = 0;
        Assert.True(Compression.LooksLikeGzip(packed));
        Assert.Equal(0, packed.Position);
        await using var unpacked = Compression.Gunzip(packed, "orders.csv.gz");
        var rows = await DelimitedReader.ReadAsync(unpacked).ToListAsync();
        Assert.Equal("two", rows.Single()["b"]);
    }

    [Fact]
    public async Task A_damaged_gzip_names_the_file_and_is_not_temporary()
    {
        var packed = new MemoryStream();
        await using (var gz = Compression.Gzip(packed)) await gz.WriteAsync(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("a line of text\n", 5000))));
        var bytes = packed.ToArray();
        var cut = new MemoryStream(bytes[..(bytes.Length / 2)]);
        var ex = await Assert.ThrowsAnyAsync<FileFormatException>(async () => await Compression.Gunzip(cut, "feed.gz").CopyToAsync(Stream.Null));
        Assert.Contains("feed.gz", ex.Message);
        Assert.False(ex.IsTemporary);
        var notGzip = await Assert.ThrowsAsync<CompressedFileException>(async () => await Compression.Gunzip(new MemoryStream("plain text, not gzip"u8.ToArray()), "x.gz").CopyToAsync(Stream.Null));
        Assert.Contains("x.gz", notGzip.Message);
    }

    [Fact]
    public async Task A_small_file_that_unpacks_to_more_than_the_limit_is_refused()
    {
        var packed = new MemoryStream();
        await using (var gz = Compression.Gzip(packed)) await gz.WriteAsync(new byte[8 * 1024 * 1024]);
        Assert.True(packed.Length < 100_000);
        packed.Position = 0;
        var ex = await Assert.ThrowsAsync<CompressedFileException>(async () => await Compression.Gunzip(packed, "bomb.gz", maxUnpackedBytes: 1024 * 1024).CopyToAsync(Stream.Null));
        Assert.Contains("bomb.gz", ex.Message);
        Assert.Contains("limit", ex.Message);
    }

    [Fact]
    public async Task Zip_lists_entries_reads_one_and_refuses_what_is_not_a_zip()
    {
        var file = new MemoryStream();
        using (var zip = Compression.CreateZip(file))
        {
            var one = zip.CreateEntry("data/orders.csv");
            await using (var s = one.Open()) await s.WriteAsync("id,name\n1,Ann\n2,Bo\n"u8.ToArray());
            var two = zip.CreateEntry("readme.txt");
            await using (var s = two.Open()) await s.WriteAsync("hello"u8.ToArray());
        }

        file.Position = 0;
        var entries = Compression.ZipEntries(file, "batch.zip");
        Assert.Equal(["data/orders.csv", "readme.txt"], entries.Select(e => e.Name).ToArray());
        Assert.Equal(19, entries[0].Length);
        await using (var entry = Compression.OpenZipEntry(file, "data/orders.csv", "batch.zip"))
            Assert.Equal(["Ann", "Bo"], (await DelimitedReader.ReadAsync(entry).ToListAsync()).Select(r => (string)r["name"]!).ToArray());
        Assert.Contains("batch.zip", Assert.Throws<CompressedFileException>(() => Compression.OpenZipEntry(file, "missing.csv", "batch.zip")).Message);
        Assert.Contains("nope.zip", Assert.Throws<CompressedFileException>(() => Compression.ZipEntries(new MemoryStream("not a zip at all"u8.ToArray()), "nope.zip")).Message);
    }
}

public class ExcelTests
{
    [Fact]
    public async Task Excel_round_trips_with_the_types_the_cells_hold()
    {
        var stream = new MemoryStream();
        var when = new DateTime(2026, 11, 11, 9, 30, 0);
        await using (var writer = ExcelRowWriter.Create(stream, ["id", "name", "price", "ok", "when", "note"]))
        {
            await writer.WriteAsync([1L, "Ann", 2.5, true, when, null]);
            await writer.WriteAsync([2, "Bo", 3m, false, when.AddDays(1), "x"]);
            await writer.FinishAsync();
        }

        stream.Position = 0;
        var rows = await ExcelRows.ReadAsync(stream).ToListAsync();
        Assert.Equal(["id", "name", "price", "ok", "when", "note"], rows[0].Columns);
        Assert.Equal(1L, rows[0]["id"]);
        Assert.Equal("Ann", rows[0]["name"]);
        Assert.Equal(2.5, rows[0]["price"]);
        Assert.Equal(true, rows[0]["ok"]);
        Assert.Equal(when, rows[0]["when"]);
        Assert.Null(rows[0]["note"]);
        Assert.Equal(3L, rows[1]["price"]);
        Assert.Equal([2L, 3L], rows.Select(r => r.Line).ToArray());   // the header is row 1
    }

    [Fact]
    public async Task A_sheet_is_chosen_by_name_and_a_missing_one_names_the_file()
    {
        var stream = new MemoryStream();
        using (var workbook = new XLWorkbook())
        {
            workbook.AddWorksheet("First").Cell(1, 1).Value = "a";
            var second = workbook.AddWorksheet("Second");
            second.Cell(1, 1).Value = "x";
            second.Cell(2, 1).Value = "y";
            workbook.SaveAs(stream);
        }

        stream.Position = 0;
        Assert.Equal("y", (await ExcelRows.ReadAsync(stream, new ExcelOptions { Sheet = "Second" }).ToListAsync()).Single()["x"]);
        stream.Position = 0;
        var ex = await Assert.ThrowsAsync<FileFormatException>(() => ExcelRows.ReadAsync(stream, new ExcelOptions { Sheet = "Third" }, new ReadOptions { File = "book.xlsx" }).ToListAsync());
        Assert.Contains("book.xlsx", ex.Message);
        Assert.Contains("Third", ex.Message);
    }

    [Fact]
    public async Task An_error_cell_is_a_bad_record_and_a_file_that_is_not_a_workbook_or_is_too_large_is_refused()
    {
        var stream = new MemoryStream();
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.AddWorksheet("S");
            sheet.Cell(1, 1).Value = "a";
            sheet.Cell(1, 2).Value = "b";
            sheet.Cell(2, 1).Value = 1;
            sheet.Cell(2, 2).FormulaA1 = "=1/0";
            sheet.Cell(3, 1).Value = 2;
            sheet.Cell(3, 2).Value = 3;
            workbook.SaveAs(stream);
        }

        stream.Position = 0;
        var bad = new BadRows();
        var rows = await ExcelRows.ReadAsync(stream, read: new ReadOptions { OnBadRow = BadRowDecision.Collect, Bad = bad }).ToListAsync();
        Assert.Equal(2L, rows.Single()["a"]);
        Assert.Equal(1, bad.Rows.Single().Row);
        Assert.Equal(2, bad.Rows.Single().Line);
        Assert.Equal(2, bad.Rows.Single().Column);

        var notBook = await Assert.ThrowsAsync<FileFormatException>(() => ExcelRows.ReadAsync(new MemoryStream("a,b\n1,2\n"u8.ToArray()), read: new ReadOptions { File = "x.xlsx" }).ToListAsync());
        Assert.Contains("x.xlsx", notBook.Message);
        stream.Position = 0;
        var tooBig = await Assert.ThrowsAsync<FileFormatException>(() => ExcelRows.ReadAsync(stream, new ExcelOptions { MaxFileBytes = 100 }, new ReadOptions { File = "big.xlsx" }).ToListAsync());
        Assert.Contains("big.xlsx", tooBig.Message);
    }
}

public class ParquetTests
{
    private static readonly ParquetColumn[] Columns =
    [
        new("id", typeof(long), false), new("name", typeof(string)), new("score", typeof(double)), new("amount", typeof(decimal)),
        new("active", typeof(bool)), new("seen", typeof(DateTime)), new("qty", typeof(int)), new("blob", typeof(byte[])),
    ];

    [Fact]
    public async Task Parquet_round_trips_typed_values_nulls_and_several_row_groups_and_reads_a_stream_that_cannot_seek()
    {
        var stream = new MemoryStream();
        var seen = new DateTime(2026, 11, 11, 8, 0, 0, DateTimeKind.Utc);
        var expected = new List<object?[]>();
        await using (var writer = await ParquetRowWriter.CreateAsync(stream, Columns, rowGroupSize: 3))
        {
            for (var i = 1; i <= 8; i++)
            {
                object?[] row = [(long)i, i % 3 == 0 ? null : "name " + i, i * 1.5, i * 2.25m, i % 2 == 0, seen.AddMinutes(i), i % 4 == 0 ? null : (int?)i, new byte[] { (byte)i }];
                expected.Add(row);
                await writer.WriteAsync(row);
            }

            await writer.FinishAsync();
            Assert.Equal(8, writer.Written);
        }

        var bytes = stream.ToArray();
        var rows = await ParquetRows.ReadAsync(new MemoryStream(bytes)).ToListAsync();
        Assert.Equal(8, rows.Count);
        Assert.Equal(Columns.Select(c => c.Name), rows[0].Columns);
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(expected[i][0], rows[i]["id"]);
            Assert.Equal(expected[i][1], rows[i]["name"]);
            Assert.Equal(expected[i][2], rows[i]["score"]);
            Assert.Equal(expected[i][3], rows[i]["amount"]);
            Assert.Equal(expected[i][4], rows[i]["active"]);
            Assert.Equal(((DateTime)expected[i][5]!).Ticks, ((DateTime)rows[i]["seen"]!).Ticks);
            Assert.Equal(expected[i][6], rows[i]["qty"]);
            Assert.Equal((byte[])expected[i][7]!, (byte[])rows[i]["blob"]!);
        }

        Assert.Null(rows[2]["name"]);
        Assert.Null(rows[3]["qty"]);
        var viaForwardOnly = await ParquetRows.ReadAsync(new ForwardOnly(new MemoryStream(bytes))).ToListAsync();
        Assert.Equal(8, viaForwardOnly.Count);
    }

    [Fact]
    public async Task A_value_of_the_wrong_type_or_a_null_in_a_required_column_is_refused_naming_the_column_and_a_file_that_is_not_parquet_is_refused()
    {
        await using var writer = await ParquetRowWriter.CreateAsync(new MemoryStream(), [new ParquetColumn("id", typeof(long), false), new ParquetColumn("name", typeof(string))]);
        var wrongType = await Assert.ThrowsAsync<FileFormatException>(async () => await writer.WriteAsync([1, "x"]));
        Assert.Contains("column id", wrongType.Message);
        var nullInRequired = await Assert.ThrowsAsync<FileFormatException>(async () => await writer.WriteAsync([null, "x"]));
        Assert.Contains("id", nullInRequired.Message);

        var ex = await Assert.ThrowsAsync<FileFormatException>(() => ParquetRows.ReadAsync(new MemoryStream("a,b\n1,2\n"u8.ToArray()), new ReadOptions { File = "data.parquet" }).ToListAsync());
        Assert.Contains("data.parquet", ex.Message);
    }

    private sealed class ForwardOnly(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public class OpenPgpTests
{
    private const string Plain = "id,name\n1,Ann\n2,Bo\n";

    private static async Task<byte[]> EncryptAsync(PgpEncryptionTarget target, string text = Plain, bool armor = false)
    {
        var output = new MemoryStream();
        await OpenPgp.EncryptAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), output, target, "data.csv", armor);
        return output.ToArray();
    }

    private static async Task<string> DecryptAsync(byte[] encrypted, PgpDecryptionKey key, string file = "orders.csv.pgp")
    {
        var plain = new MemoryStream();
        await OpenPgp.DecryptAsync(new MemoryStream(encrypted), plain, key, file);
        return Encoding.UTF8.GetString(plain.ToArray());
    }

    [Fact]
    public async Task A_passphrase_encrypted_file_round_trips_binary_or_armored_and_a_csv_reads_from_it()
    {
        var binary = await EncryptAsync(PgpEncryptionTarget.FromPassphrase("correct horse"));
        Assert.DoesNotContain("Ann", Encoding.Latin1.GetString(binary));
        Assert.Equal(Plain, await DecryptAsync(binary, PgpDecryptionKey.FromPassphrase("correct horse")));
        var armored = await EncryptAsync(PgpEncryptionTarget.FromPassphrase("correct horse"), armor: true);
        Assert.StartsWith("-----BEGIN PGP MESSAGE-----", Encoding.ASCII.GetString(armored));
        Assert.Equal(Plain, await DecryptAsync(armored, PgpDecryptionKey.FromPassphrase("correct horse")));
    }

    [Fact]
    public async Task A_wrong_passphrase_is_a_permanent_failure_that_names_the_file_and_never_the_key()
    {
        var encrypted = await EncryptAsync(PgpEncryptionTarget.FromPassphrase("right-one"));
        var ex = await Assert.ThrowsAsync<EncryptedFileException>(() => DecryptAsync(encrypted, PgpDecryptionKey.FromPassphrase("wrong-one"), "payroll.csv.pgp"));
        Assert.Contains("payroll.csv.pgp", ex.Message);
        Assert.Equal("payroll.csv.pgp", ex.File);
        Assert.False(ex.IsTemporary);
        Assert.DoesNotContain("wrong-one", ex.Message + ex.InnerException);
        Assert.DoesNotContain("right-one", ex.Message + ex.InnerException);
        var notPgp = await Assert.ThrowsAsync<EncryptedFileException>(() => DecryptAsync("plain text"u8.ToArray(), PgpDecryptionKey.FromPassphrase("x"), "plain.pgp"));
        Assert.Contains("plain.pgp", notPgp.Message);
    }

    [Fact]
    public async Task A_file_changed_after_encryption_is_refused()
    {
        var encrypted = await EncryptAsync(PgpEncryptionTarget.FromPassphrase("pw"), string.Concat(Enumerable.Repeat("a line of the file\n", 2000)));
        encrypted[encrypted.Length / 2] ^= 0x55;
        await Assert.ThrowsAsync<EncryptedFileException>(() => DecryptAsync(encrypted, PgpDecryptionKey.FromPassphrase("pw"), "changed.pgp"));
    }

    [Fact]
    public async Task A_file_encrypted_to_a_public_key_needs_its_private_key_and_its_passphrase()
    {
        var (publicKey, privateKey) = MakeKeys("key-pass");
        var (_, otherPrivate) = MakeKeys("other");
        var encrypted = await EncryptAsync(PgpEncryptionTarget.FromPublicKey(publicKey));
        Assert.Equal(Plain, await DecryptAsync(encrypted, PgpDecryptionKey.FromPrivateKey(privateKey, "key-pass")));
        var other = await Assert.ThrowsAsync<EncryptedFileException>(() => DecryptAsync(encrypted, PgpDecryptionKey.FromPrivateKey(otherPrivate, "other"), "k.pgp"));
        Assert.Contains("k.pgp", other.Message);
        var wrongPass = await Assert.ThrowsAsync<EncryptedFileException>(() => DecryptAsync(encrypted, PgpDecryptionKey.FromPrivateKey(privateKey, "nope"), "k.pgp"));
        Assert.DoesNotContain("nope", wrongPass.Message);
        await Assert.ThrowsAsync<EncryptedFileException>(() => DecryptAsync(encrypted, PgpDecryptionKey.FromPassphrase("pw"), "k.pgp"));
    }

    [Fact]
    public async Task A_failure_of_the_source_is_not_taken_for_a_wrong_key()
    {
        var encrypted = await EncryptAsync(PgpEncryptionTarget.FromPassphrase("pw"), string.Concat(Enumerable.Repeat("a line of the file\n", 2000)));
        var broken = new FailingAfter(encrypted, 200);
        var ex = await Record.ExceptionAsync(() => OpenPgp.DecryptAsync(broken, new MemoryStream(), PgpDecryptionKey.FromPassphrase("pw"), "net.pgp"));
        Assert.IsType<IOException>(ex);
    }

    private static (string Public, string Private) MakeKeys(string passphrase)
    {
        var random = new SecureRandom();
        var generator = new RsaKeyPairGenerator();
        generator.Init(new RsaKeyGenerationParameters(BigInteger.ValueOf(0x10001), random, 2048, 12));
        var pair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, generator.GenerateKeyPair(), DateTime.UtcNow);
        var rings = new PgpKeyRingGenerator(PgpSignature.PositiveCertification, pair, "test@example.test", SymmetricKeyAlgorithmTag.Aes256, passphrase.ToCharArray(), true, null, null, random);
        return (Armor(rings.GeneratePublicKeyRing().Encode), Armor(rings.GenerateSecretKeyRing().Encode));

        static string Armor(Action<Stream> encode)
        {
            var ms = new MemoryStream();
            using (var armored = new ArmoredOutputStream(ms)) encode(armored);
            return Encoding.ASCII.GetString(ms.ToArray());
        }
    }

    private sealed class FailingAfter(byte[] data, int fail) : Stream
    {
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= fail) throw new IOException("the connection was reset");
            var n = Math.Min(Math.Min(count, fail - _pos), data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
