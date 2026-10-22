namespace DbDataKit.Secrets.Store;

/// <summary>A key file or the directory it is in can be read by someone it should not be (the way ssh refuses a private key): the server must not start on it.</summary>
public sealed class KeyPermissionException(string path, string problem, string fix)
    : Exception($"The secret key {(Directory.Exists(path) ? "directory" : "file")} '{path}' {problem}. {fix}")
{
    public string Path { get; } = path;
}
