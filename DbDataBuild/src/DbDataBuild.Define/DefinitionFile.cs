using System.Security.Cryptography;
using System.Text;
using DbDataBuild.Core;

namespace DbDataBuild.Define;

/// <summary>
/// The only way `define` writes: a definition (.yml) file, checked against what was read, via a temp file and an atomic rename.
/// It refuses any other path, so a .sql body cannot be written through it (DESIGN.md 6.5).
/// </summary>
public static class DefinitionFile
{
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static string? HashIfExists(string path) => File.Exists(path) ? Hash(File.ReadAllBytes(path)) : null;

    /// <summary>
    /// Writes <paramref name="newText"/> only if the file still has <paramref name="expectedHash"/> (null: it must not exist yet).
    /// Returns a DDB-421 diagnostic instead of overwriting a file that changed after it was read.
    /// </summary>
    public static Diagnostic? WriteIfUnchanged(string path, string displayPath, string? expectedHash, string newText)
    {
        if (!path.EndsWith(".yml", StringComparison.Ordinal))
            throw new InvalidOperationException($"define writes definition files only; refusing to write `{displayPath}`.");

        Diagnostic Changed() => new(DiagnosticCatalog.DefinitionFileChanged, new(displayPath, 0, 0),
            expectedHash == null ? $"`{displayPath}` appeared while define was running." : $"`{displayPath}` changed after define read it.");

        if (HashIfExists(path) != expectedHash) return Changed();

        var temp = path + ".ddb-" + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(newText);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            // a last check immediately before the rename; the rename itself is atomic
            if (HashIfExists(path) != expectedHash) return Changed();
            File.Move(temp, path, overwrite: expectedHash != null);
            return null;
        }
        catch (IOException) when (expectedHash == null && File.Exists(path))
        {
            return Changed();
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
