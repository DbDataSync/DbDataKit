namespace DbDataKit.Secrets;

/// <summary>
/// A secret held in memory. <see cref="ToString"/> never reveals it; <see cref="Reveal"/> is the one
/// explicit way out. The backing buffer is zeroed on <see cref="Dispose"/>.
/// </summary>
public sealed class SecretValue : IDisposable
{
    private readonly char[] _chars;
    private bool _disposed;

    public const string Mask = "***";

    public SecretValue(ReadOnlySpan<char> value) => _chars = value.ToArray();

    public static SecretValue From(string value) => new(value.AsSpan());

    public int Length => _disposed ? 0 : _chars.Length;

    public bool IsEmpty => Length == 0;

    /// <summary>The secret as a span over the internal buffer. Do not store it; it is zeroed on dispose.</summary>
    public ReadOnlySpan<char> Reveal()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _chars;
    }

    /// <summary>Convenience copy as a string. The copy is outside this type's zeroing guarantee.</summary>
    public string RevealString() => new(Reveal());

    public override string ToString() => Mask;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Array.Clear(_chars);
    }
}
