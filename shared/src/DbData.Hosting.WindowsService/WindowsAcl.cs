using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DbData.Hosting.WindowsService;

[SupportedOSPlatform("windows")]
public sealed class WindowsAclPolicy : IAclPolicy
{
    public void Apply(LayoutEntry entry, IPrincipalResolver principals)
    {
        if (entry.Acl is null) return;
        var dir = new DirectoryInfo(entry.Path);
        dir.Create();

        var security = dir.GetAccessControl();
        security.SetAccessRuleProtection(entry.Acl.CutInheritance, preserveInheritance: false);
        if (entry.Acl.CutInheritance)
        {
            foreach (FileSystemAccessRule existing in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            {
                security.RemoveAccessRuleSpecific(existing);
            }
        }

        foreach (var rule in entry.Acl.Rules)
        {
            var sid = new SecurityIdentifier(principals.ResolveSid(rule.Principal));
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                ToFileSystemRights(rule.Rights),
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        dir.SetAccessControl(security);
    }

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
}

[SupportedOSPlatform("windows")]
public sealed class WindowsFileSystemAclReader : IFileSystemAclReader
{
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IEnumerable<string> EnumerateFiles(string directory) =>
        Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories) : [];

    public ObservedAcl? ReadAcl(string path)
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
