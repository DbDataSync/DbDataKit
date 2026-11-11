namespace DbDataKit.Secrets.Store;

/// <summary>The store could not do what was asked: the file is not a store, a value does not decrypt (changed, moved to another name, or written with another key), the lock was not got. The message never holds a value.</summary>
public sealed class SecretStoreException(string message, Exception? inner = null) : Exception(message, inner);
