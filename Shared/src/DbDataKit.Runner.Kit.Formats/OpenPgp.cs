using System.Runtime.ExceptionServices;
using System.Text;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace DbDataKit.Runner.Kit.Formats;

/// <summary>
/// A file that cannot be decrypted: the key does not fit, or the file is not an OpenPGP message, or it was changed after it was encrypted. A permanent failure that names the file: trying again with the same key gives the same answer, and the runner does not retry it.
/// The message never holds the key, the passphrase or any text of the file.
/// </summary>
public sealed class EncryptedFileException(string message, string? file = null, Exception? inner = null) : FileFormatException(message, file, inner);

/// <summary>What decrypts a file: a passphrase (a file encrypted with one) or a private key (a file encrypted to its public key). Take the values from secrets (<c>dbdatakit:&lt;service&gt;:&lt;kind&gt;:&lt;name&gt;</c>), never from the job's code.</summary>
public sealed class PgpDecryptionKey
{
    private PgpDecryptionKey(string? passphrase, string? privateKey, string? keyPassphrase)
    {
        Passphrase = passphrase;
        PrivateKey = privateKey;
        KeyPassphrase = keyPassphrase;
    }

    internal string? Passphrase { get; }

    internal string? PrivateKey { get; }

    internal string? KeyPassphrase { get; }

    public static PgpDecryptionKey FromPassphrase(string passphrase) => new(passphrase, null, null);

    /// <summary>An OpenPGP secret key (ASCII armored or binary, as text or Base64 for binary) and the passphrase that unlocks it (null or empty for none).</summary>
    public static PgpDecryptionKey FromPrivateKey(string armoredKey, string? keyPassphrase = null) => new(null, armoredKey, keyPassphrase);

    public override string ToString() => "PgpDecryptionKey";
}

/// <summary>What encrypts a file: a passphrase, or the public key of whoever may read it.</summary>
public sealed class PgpEncryptionTarget
{
    private PgpEncryptionTarget(string? passphrase, string? publicKey)
    {
        Passphrase = passphrase;
        PublicKey = publicKey;
    }

    internal string? Passphrase { get; }

    internal string? PublicKey { get; }

    public static PgpEncryptionTarget FromPassphrase(string passphrase) => new(passphrase, null);

    /// <summary>An OpenPGP public key, ASCII armored.</summary>
    public static PgpEncryptionTarget FromPublicKey(string armoredKey) => new(null, armoredKey);

    public override string ToString() => "PgpEncryptionTarget";
}

/// <summary>
/// OpenPGP encryption and decryption of a file as a stream (plan 27a SD14; BouncyCastle, which is already pinned for the suite's other cryptography). The file's content is never held whole. Decryption checks the message's integrity
/// packet at the end: a file changed after it was encrypted is an <see cref="EncryptedFileException"/> raised when the last byte is read, so a job writes the plain text to a step's stream, which is committed only when the body returns.
/// </summary>
public static class OpenPgp
{
    private const int Chunk = 1 << 16;

    public static async Task EncryptAsync(Stream plain, Stream output, PgpEncryptionTarget target, string fileName = "data", bool armor = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var generator = new PgpEncryptedDataGenerator(SymmetricKeyAlgorithmTag.Aes256, withIntegrityPacket: true);
        if (target.Passphrase is { } passphrase) generator.AddMethod(passphrase.ToCharArray(), HashAlgorithmTag.Sha256);
        else generator.AddMethod(FindEncryptionKey(target.PublicKey!));

        ArmoredOutputStream? armored = armor ? new ArmoredOutputStream(output) : null;
        Stream sink = armored ?? output;
        await using (var encrypted = new BcStreamAdapter(generator.Open(sink, new byte[Chunk])))
        {
            var literal = new PgpLiteralDataGenerator();
            await using var data = new BcStreamAdapter(literal.Open(encrypted.Inner, PgpLiteralData.Binary, fileName, DateTime.UtcNow, new byte[Chunk]));
            await plain.CopyToAsync(data, Chunk, ct).ConfigureAwait(false);
        }

        armored?.Close();
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Decrypts <paramref name="encrypted"/> into <paramref name="plain"/>. <paramref name="file"/> is the file's name for the failure message.</summary>
    public static async Task DecryptAsync(Stream encrypted, Stream plain, PgpDecryptionKey key, string? file = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var source = new TrackingStream(encrypted);
        try
        {
            using var decoder = Decoder(source);
            var factory = new PgpObjectFactory(decoder);
            PgpEncryptedDataList? list = null;
            while (list is null && factory.NextPgpObject() is { } next) list = next as PgpEncryptedDataList;
            if (list is null) throw new EncryptedFileException($"{Compression.Name(file)} is not an encrypted OpenPGP message.", file);

            var (data, clear) = Open(list, key, file);
            using (clear)
            {
                await CopyLiteralAsync(clear, plain, file, ct).ConfigureAwait(false);
            }

            if (data.IsIntegrityProtected() && !data.Verify())
                throw new EncryptedFileException($"{Compression.Name(file)} was changed after it was encrypted (its integrity check fails).", file);
        }
        catch (Exception ex) when (ex is not EncryptedFileException and not OperationCanceledException)
        {
            if (source.Failure is { } real) ExceptionDispatchInfo.Capture(real).Throw();
            throw new EncryptedFileException($"{Compression.Name(file)} cannot be decrypted with the key given: the key does not fit, or the file is damaged or is not an OpenPGP message.", file, ex);
        }
    }

    /// <summary>The stream of packets: the input itself for a binary message, an armor decoder for a text one. BouncyCastle's own helper needs a seekable stream; a file from a share or a payload is not, so the first byte is read here and put back.</summary>
    private static Stream Decoder(Stream source)
    {
        var first = source.ReadByte();
        if (first < 0) throw new EncryptedFileException("The encrypted file is empty.");
        var whole = new PrefixedStream((byte)first, source);
        return (first & 0x80) != 0 ? whole : new ArmoredInputStream(whole);
    }

    private static (PgpEncryptedData Data, Stream Clear) Open(PgpEncryptedDataList list, PgpDecryptionKey key, string? file)
    {
        if (key.Passphrase is { } passphrase)
        {
            var pbe = list.GetEncryptedDataObjects().OfType<PgpPbeEncryptedData>().FirstOrDefault()
                ?? throw new EncryptedFileException($"{Compression.Name(file)} is not encrypted with a passphrase; it needs a private key.", file);
            return (pbe, pbe.GetDataStream(passphrase.ToCharArray()));
        }

        var secrets = LoadSecretKeys(key.PrivateKey!);
        var unlock = (key.KeyPassphrase ?? string.Empty).ToCharArray();
        foreach (var data in list.GetEncryptedDataObjects().OfType<PgpPublicKeyEncryptedData>())
        {
            var secret = secrets.GetSecretKey(data.KeyId);
            if (secret is null) continue;
            return (data, data.GetDataStream(secret.ExtractPrivateKey(unlock)));
        }

        throw new EncryptedFileException($"{Compression.Name(file)} is not encrypted to the private key given, or needs a passphrase.", file);
    }

    private static async Task CopyLiteralAsync(Stream clear, Stream plain, string? file, CancellationToken ct)
    {
        var factory = new PgpObjectFactory(clear);
        for (var message = factory.NextPgpObject(); message is not null; message = factory.NextPgpObject())
        {
            switch (message)
            {
                case PgpCompressedData compressed:
                    await CopyLiteralAsync(compressed.GetDataStream(), plain, file, ct).ConfigureAwait(false);
                    return;
                case PgpLiteralData literal:
                    await literal.GetInputStream().CopyToAsync(plain, Chunk, ct).ConfigureAwait(false);
                    return;
            }
        }

        throw new EncryptedFileException($"{Compression.Name(file)} holds no data after decryption.", file);
    }

    private static PgpPublicKey FindEncryptionKey(string armored)
    {
        using var input = PgpUtilities.GetDecoderStream(new MemoryStream(Encoding.UTF8.GetBytes(armored)));
        var bundle = new PgpPublicKeyRingBundle(input);
        foreach (PgpPublicKeyRing ring in bundle.GetKeyRings())
            foreach (PgpPublicKey k in ring.GetPublicKeys())
                if (k.IsEncryptionKey && !k.IsRevoked()) return k;
        throw new ArgumentException("The public key given has no key that can encrypt.", nameof(armored));
    }

    private static PgpSecretKeyRingBundle LoadSecretKeys(string armored)
    {
        using var input = PgpUtilities.GetDecoderStream(new MemoryStream(Encoding.UTF8.GetBytes(armored)));
        return new PgpSecretKeyRingBundle(input);
    }

    private sealed class PrefixedStream(byte first, Stream rest) : Stream
    {
        private bool _gaveFirst;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0) return 0;
            if (_gaveFirst) return rest.Read(buffer, offset, count);
            _gaveFirst = true;
            buffer[offset] = first;
            return 1;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Remembers an exception of the underlying stream, so a failure of the source (a network error: temporary) is told from a failure of the decryption (permanent).</summary>
    private sealed class TrackingStream(Stream inner) : Stream
    {
        public Exception? Failure { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            try { return inner.Read(buffer, offset, count); }
            catch (Exception ex) { Failure = ex; throw; }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Lets <c>await using</c> close one of BouncyCastle's writing streams, which must be closed in order to finish its packet.</summary>
    private sealed class BcStreamAdapter(Stream inner) : Stream, IAsyncDisposable
    {
        public Stream Inner => inner;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
