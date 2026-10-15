namespace DbData.Hosting.WindowsService.Tests;

public class DoctorTests
{
    private const string ServiceSid = "S-1-5-80-111-222-333-444-555";

    private sealed class FakeFs : IFileSystemAclReader
    {
        public Dictionary<string, ObservedAcl> Dirs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ObservedAcl> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool DirectoryExists(string path) => Dirs.ContainsKey(path);

        public IEnumerable<string> EnumerateFiles(string directory) =>
            Files.Keys.Where(f => f.StartsWith(directory, StringComparison.OrdinalIgnoreCase));

        public ObservedAcl? ReadAcl(string path) =>
            Dirs.TryGetValue(path, out var d) ? d : Files.GetValueOrDefault(path);
    }

    private static readonly DirectoryLayout Layout = DirectoryLayout.ForProduct("P", @"C:\PF", @"C:\PD");
    private static readonly WellKnownPrincipalResolver Resolver = new(ServiceSid);

    private static ServiceInstallSpec Spec() => new() { Name = "P", BinaryPath = @"C:\PF\P\current\P.exe", Version = "1" };

    /// <summary>Builds the ACL the layout intends, so tests start clean and then corrupt one thing.</summary>
    private static FakeFs Healthy()
    {
        var fs = new FakeFs();
        foreach (var e in Layout.Entries.Where(e => e.Acl is not null))
        {
            fs.Dirs[e.Path] = new ObservedAcl(e.Acl!.CutInheritance,
                e.Acl.Rules.Select(r => new ObservedAce(Resolver.ResolveSid(r.Principal), r.Rights, true, false)).ToList());
        }

        // Program Files inherits and legitimately grants Users read/execute.
        foreach (var role in new[] { LayoutRole.InstallRoot, LayoutRole.Versions })
        {
            var acl = fs.Dirs[Layout[role].Path];
            fs.Dirs[Layout[role].Path] = acl with { Aces = [.. acl.Aces, new ObservedAce(WellKnownPrincipalResolver.UsersSid, AccessRights.ReadExecute, true, true)] };
        }

        return fs;
    }

    private static InMemoryServiceControlManager InstalledScm()
    {
        var scm = new InMemoryServiceControlManager();
        new ServiceProvisioner(scm).Install(Spec());
        return scm;
    }

    private static DoctorReport Run(InMemoryServiceControlManager scm, FakeFs fs) =>
        new ServiceDoctor(scm, fs, Resolver).Run(Spec(), Layout);

    [Fact]
    public void Healthy_system_passes()
    {
        var report = Run(InstalledScm(), Healthy());
        Assert.True(report.Ok, string.Join("\n", report.Checks.Where(c => c.Severity != DoctorSeverity.Pass).Select(c => $"{c.Id}: {c.Message}")));
        Assert.Equal(0, report.Warnings);
    }

    [Fact]
    public void Missing_service_fails()
    {
        var report = Run(new InMemoryServiceControlManager(), Healthy());
        Assert.Contains(report.Checks, c => c.Id == "service.installed" && c.Severity == DoctorSeverity.Fail);
    }

    [Fact]
    public void LocalSystem_service_fails()
    {
        var scm = new InMemoryServiceControlManager();
        var spec = Spec() with { Account = ServiceAccount.LocalSystem, AllowLocalSystem = false };
        scm.Create(spec, null);
        var report = new ServiceDoctor(scm, Healthy(), Resolver).Run(Spec(), Layout);
        Assert.Contains(report.Checks, c => c.Id == "service.account" && c.Severity == DoctorSeverity.Fail);
    }

    [Fact]
    public void Users_on_secrets_fails()
    {
        var fs = Healthy();
        var path = Layout[LayoutRole.Secrets].Path;
        fs.Dirs[path] = fs.Dirs[path] with { Aces = [.. fs.Dirs[path].Aces, new ObservedAce(WellKnownPrincipalResolver.UsersSid, AccessRights.Read, true, true)] };
        var report = Run(InstalledScm(), fs);
        Assert.False(report.Ok);
        Assert.Contains(report.Checks, c => c.Id == "dir.Secrets" && c.Severity == DoctorSeverity.Fail && c.Message.Contains("Users"));
    }

    [Fact]
    public void Uncut_inheritance_fails()
    {
        var fs = Healthy();
        var path = Layout[LayoutRole.Certs].Path;
        fs.Dirs[path] = fs.Dirs[path] with { InheritanceProtected = false };
        Assert.Contains(Run(InstalledScm(), fs).Checks, c => c.Id == "dir.Certs" && c.Severity == DoctorSeverity.Fail);
    }

    [Fact]
    public void Writable_binaries_fail()
    {
        var fs = Healthy();
        var path = Layout[LayoutRole.Versions].Path;
        fs.Dirs[path] = fs.Dirs[path] with { Aces = [.. fs.Dirs[path].Aces, new ObservedAce(ServiceSid, AccessRights.Modify, true, false)] };
        Assert.Contains(Run(InstalledScm(), fs).Checks, c => c.Id == "dir.Versions" && c.Severity == DoctorSeverity.Fail && c.Message.Contains("writable"));
    }

    [Fact]
    public void World_readable_key_file_fails_even_with_good_directory()
    {
        var fs = Healthy();
        var file = Path.Combine(Layout[LayoutRole.Certs].Path, "server.pfx");
        fs.Files[file] = new ObservedAcl(false, [new ObservedAce(WellKnownPrincipalResolver.EveryoneSid, AccessRights.Read, true, false)]);
        Assert.Contains(Run(InstalledScm(), fs).Checks, c => c.Id == "dir.Certs" && c.Severity == DoctorSeverity.Fail && c.Message.Contains("server.pfx"));
    }

    [Fact]
    public void Missing_directory_fails()
    {
        var fs = Healthy();
        fs.Dirs.Remove(Layout[LayoutRole.Logs].Path);
        Assert.Contains(Run(InstalledScm(), fs).Checks, c => c.Id == "dir.Logs" && c.Severity == DoctorSeverity.Fail);
    }

    [Fact]
    public void Service_account_missing_required_rights_fails()
    {
        var fs = Healthy();
        var path = Layout[LayoutRole.Data].Path;
        fs.Dirs[path] = fs.Dirs[path] with { Aces = fs.Dirs[path].Aces.Where(a => a.Sid != ServiceSid).ToList() };
        Assert.Contains(Run(InstalledScm(), fs).Checks, c => c.Id == "dir.Data" && c.Severity == DoctorSeverity.Fail);
    }

    [Fact]
    public void Layout_intends_users_never_appear_and_secrets_are_locked()
    {
        foreach (var e in Layout.Entries.Where(e => e.Acl is not null))
        {
            Assert.DoesNotContain(e.Acl!.Rules, r => r.Principal == LayoutPrincipal.Users);
        }

        Assert.All(new[] { LayoutRole.Secrets, LayoutRole.Certs, LayoutRole.Data, LayoutRole.Config, LayoutRole.Logs, LayoutRole.Updates },
            r => Assert.True(Layout[r].Acl!.CutInheritance));
        Assert.True(Layout[LayoutRole.Current].IsLink);
    }
}
