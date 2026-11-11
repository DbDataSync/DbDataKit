namespace DbDataKit.Secrets.Store;

/// <summary>Where the store gets its data keys. Each key has an id, written beside every value it encrypts, so an old key can be kept until the last value has moved to a new one.</summary>
public interface IKeySource
{
    /// <summary>The key new values are encrypted with.</summary>
    string ActiveKeyId { get; }

    /// <summary>The 256-bit key with this id; the caller zeroes it when done. Throws when there is no such key.</summary>
    byte[] GetKey(string id);

    /// <summary>The ids of every key this source holds.</summary>
    IReadOnlyList<string> KeyIds();
}
