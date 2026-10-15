using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DbData.Security.Acl;

/// <summary>
/// The one seam over file system ACLs: apply a spec, read an ACL back, and list files. Fakeable, so
/// installers and doctors run on any OS against in-memory implementations.
/// </summary>
public interface IAclService
{
    bool DirectoryExists(string path);

    IEnumerable<string> EnumerateFiles(string directory);

    /// <summary>Reads the ACL of a file or directory; null when missing (or when ACLs are not supported on this OS).</summary>
    ObservedAcl? Read(string path);

    /// <summary>Creates the directory if needed and applies the spec (Windows only).</summary>
    void Apply(string directory, AclSpecification spec);
}

/// <summary>Real implementation. <see cref="Apply"/> and <see cref="Read"/> are Windows-only.</summary>
public sealed class FileSystemAclService : IAclService
{
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IEnumerable<string> EnumerateFiles(string directory) =>
        Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories) : [];

    public ObservedAcl? Read(string path) => OperatingSystem.IsWindows() ? ReadWindows(path) : null;

    public void Apply(string directory, AclSpecification spec)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Applying ACL specs is Windows-only; use AclHelper on Unix.");
        ApplyWindows(directory, spec);
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyWindows(string path, AclSpecification spec)
    {
        var dir = new DirectoryInfo(path);
        dir.Create();

        var security = dir.GetAccessControl();
        security.SetAccessRuleProtection(spec.CutInheritance, preserveInheritance: false);
        if (spec.CutInheritance)
        {
            foreach (FileSystemAccessRule existing in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            {
                security.RemoveAccessRuleSpecific(existing);
            }
        }

        foreach (var entry in spec.Entries)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(entry.Sid),
                ToFileSystemRights(entry.Rights),
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        dir.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static ObservedAcl? ReadWindows(string path)
    {
        FileSystemSecurity security;
        if (Directory.Exists(path)) security = new DirectoryInfo(path).GetAccessControl();
        else if (File.Exists(path)) security = new FileInfo(path).GetAccessControl();
        else return null;

        var aces = new List<ObservedAce>();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            aces.Add(new ObservedAce(rule.IdentityReference.Value, FromFileSystemRights(rule.FileSystemRights),
                rule.AccessControlType == AccessControlType.Allow, rule.IsInherited));
        }

        return new ObservedAcl(security.AreAccessRulesProtected, aces);
    }

    [SupportedOSPlatform("windows")]
    internal static FileSystemRights ToFileSystemRights(AccessRights r)
    {
        if (r == AccessRights.None) return 0;
        if ((r & AccessRights.Full) == AccessRights.Full) return FileSystemRights.FullControl;
        FileSystemRights f = FileSystemRights.Synchronize;
        if (r.HasFlag(AccessRights.Read)) f |= FileSystemRights.Read;
        if (r.HasFlag(AccessRights.Execute)) f |= FileSystemRights.ExecuteFile;
        if (r.HasFlag(AccessRights.Write)) f |= FileSystemRights.Write;
        if (r.HasFlag(AccessRights.Delete)) f |= FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles;
        return f;
    }

    [SupportedOSPlatform("windows")]
    internal static AccessRights FromFileSystemRights(FileSystemRights r)
    {
        var a = AccessRights.None;
        if ((r & (FileSystemRights.ReadData | FileSystemRights.ListDirectory)) != 0) a |= AccessRights.Read;
        if ((r & FileSystemRights.ExecuteFile) != 0) a |= AccessRights.Execute;
        if ((r & (FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories)) != 0) a |= AccessRights.Write;
        if ((r & (FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles)) != 0) a |= AccessRights.Delete;
        return a;
    }
}
