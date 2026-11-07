namespace DbDataKit.State.Snapshots;

/// <summary>Adds the hash of every row, modulo 2^256 (see <see cref="SnapshotTable.RowsDigest"/>).</summary>
internal sealed class RowsDigest
{
    private readonly byte[] _sum = new byte[32];

    public void Add(ReadOnlySpan<byte> hash)
    {
        var carry = 0;
        for (var i = 31; i >= 0; i--)
        {
            var value = _sum[i] + hash[i] + carry;
            _sum[i] = (byte)value;
            carry = value >> 8;
        }
    }

    public string Hex() => Convert.ToHexStringLower(_sum);
}
