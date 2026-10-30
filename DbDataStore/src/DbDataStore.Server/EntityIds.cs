namespace DbDataStore.Server;

/// <summary>
/// The ids of entities (plan 25 section 1, 4): UUID version 7, whose first 48 bits are the time in milliseconds, so rows inserted later sort later and an index on the id stays compact. The serving instance that takes a write
/// makes the id with no coordination: 74 bits are random, so two instances of a serving group do not collide in practice. A draft keeps the id it was given when it is submitted as a change request (plan 21), so
/// nothing renumbers.
/// </summary>
public static class EntityIds
{
    public static Guid New(TimeProvider? clock = null) => Guid.CreateVersion7((clock ?? TimeProvider.System).GetUtcNow());

    /// <summary>The time an id was made, to the millisecond; null for an id that is not version 7.</summary>
    public static DateTimeOffset? MadeAt(Guid id)
    {
        if (id.Version != 7) return null;
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes, bigEndian: true, out _);
        var milliseconds = ((long)bytes[0] << 40) | ((long)bytes[1] << 32) | ((long)bytes[2] << 24) | ((long)bytes[3] << 16) | ((long)bytes[4] << 8) | bytes[5];
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }
}
