using DbDataKit.Update.Service;

namespace DbDataKit.Upgrade;

/// <summary>
/// "Which version is current" for an install that is one program folder (what the zip and the install scripts make), as <see cref="ICurrentPointer"/> for <see cref="UpdateHelper"/>: setting a version moves the program
/// folder to <c>versions/&lt;running&gt;</c> of the upgrade area and the version's folder into its place, so the old version stays whole beside it and going back is the same move the other way. The upgrade area is a sibling of the
/// program folder (the move is a rename, so it must be on one volume). The service is stopped when this runs: a folder with a running program in it cannot be renamed on Windows.
/// </summary>
public sealed class ProgramFolderPointer(InstallLayout layout, string programDirectory, string runningVersion) : ICurrentPointer
{
    private string Marker => Path.Combine(layout.Root, "current.txt");

    public string? CurrentVersion() => File.Exists(Marker) ? File.ReadAllText(Marker).Trim() : runningVersion;

    public void Set(string version)
    {
        var current = CurrentVersion() ?? runningVersion;
        if (string.Equals(current, version, StringComparison.OrdinalIgnoreCase)) return;
        var source = layout.VersionDir(version);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"Version {version} is not in the upgrade area ({source}).");
        var park = layout.VersionDir(current);
        if (Directory.Exists(park)) Directory.Delete(park, true);
        Directory.CreateDirectory(layout.VersionsDir);
        Directory.Move(programDirectory, park);
        try { Directory.Move(source, programDirectory); }
        catch
        {
            Directory.Move(park, programDirectory); // the program folder is never left missing
            throw;
        }

        var temporary = Marker + ".tmp";
        File.WriteAllText(temporary, version);
        File.Move(temporary, Marker, true);
    }
}
