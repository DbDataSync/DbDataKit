using System.Buffers.Binary;

namespace DbDataKit.Hosting.Local;

/// <summary>Messages on the channel: a four-byte big-endian length, then that many bytes (the products send UTF-8 JSON). A length beyond the limit is refused before anything is read.</summary>
public static class LocalChannelFraming
{
    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> message, int maxBytes = 1024 * 1024, CancellationToken ct = default)
    {
        if (message.Length > maxBytes) throw new InvalidDataException($"A message of {message.Length} bytes is more than the {maxBytes} the channel carries.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, message.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(message, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The next message, or null when the other side has closed cleanly between messages.</summary>
    public static async Task<byte[]?> ReadAsync(Stream stream, int maxBytes = 1024 * 1024, CancellationToken ct = default)
    {
        var header = new byte[4];
        var first = await ReadFullyAsync(stream, header, ct).ConfigureAwait(false);
        if (first == 0) return null;
        if (first < 4) throw new EndOfStreamException("The channel closed in the middle of a message header.");
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 0 || length > maxBytes) throw new InvalidDataException($"A message announces {length} bytes; the channel carries at most {maxBytes}.");
        var body = new byte[length];
        if (await ReadFullyAsync(stream, body, ct).ConfigureAwait(false) < length) throw new EndOfStreamException("The channel closed in the middle of a message.");
        return body;
    }

    private static async Task<int> ReadFullyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) break;
            read += n;
        }
        return read;
    }
}
