using Xunit;
using System.Security.Principal;

namespace DbDataKit.Security.Acl.Tests;

public class AclServiceTests
{
    [Fact]
    public void Rights_round_trip_through_file_system_rights()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        foreach (var r in new[] { AccessRights.Read, AccessRights.ReadExecute, AccessRights.Modify, AccessRights.Full })
        {
            var back = FileSystemAclService.FromFileSystemRights(FileSystemAclService.ToFileSystemRights(r));
            Assert.True((back & r) == r || r == AccessRights.Full, $"{r} -> {back}");
        }
    }

    [Fact]
    public void Apply_then_Read_round_trips_on_a_temp_directory()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var me = WindowsIdentity.GetCurrent().User!.Value;
        var dir = Path.Combine(Path.GetTempPath(), "dbdata-aclsvc-" + Guid.NewGuid().ToString("N"));
        try
        {
            var svc = new FileSystemAclService();
            svc.Apply(dir, new AclSpecification(true, [new AclEntry(me, AccessRights.Modify), new AclEntry("S-1-5-18", AccessRights.Full)]));
            Assert.True(svc.DirectoryExists(dir));
            var acl = svc.Read(dir);
            Assert.NotNull(acl);
            Assert.True(acl!.InheritanceProtected);
            Assert.Contains(acl.Aces, a => a.Sid == me && (a.Rights & AccessRights.Write) != 0);
            Assert.Contains(acl.Aces, a => a.Sid == "S-1-5-18");
            Assert.DoesNotContain(acl.Aces, a => a.Sid == "S-1-1-0");

            // The same directory verifies cleanly through the static helper.
            var report = AclHelper.Verify(dir, [me, "S-1-5-18"]);
            Assert.True(report.InSync, string.Join("; ", report.Drift.Select(d => d.Description)));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Read_of_a_missing_path_is_null_and_enumeration_is_empty()
    {
        var svc = new FileSystemAclService();
        var missing = Path.Combine(Path.GetTempPath(), "dbdata-aclsvc-missing-" + Guid.NewGuid().ToString("N"));
        Assert.Null(svc.Read(missing));
        Assert.False(svc.DirectoryExists(missing));
        Assert.Empty(svc.EnumerateFiles(missing));
    }

    [Fact]
    public void Apply_off_Windows_is_not_supported()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "non-Windows only");
        Assert.Throws<PlatformNotSupportedException>(() => new FileSystemAclService().Apply("/tmp/x", new AclSpecification(true, [])));
    }
}
