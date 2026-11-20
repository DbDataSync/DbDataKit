using DbDataKit.Security.Acl;

namespace DbDataKit.Hosting.WindowsService;

public enum ServiceCheckSeverity
{
    Pass,
    Warn,
    Fail,
}

public sealed record ServiceCheckItem(string Id, ServiceCheckSeverity Severity, string Message);

public sealed record ServiceCheckReport(IReadOnlyList<ServiceCheckItem> Checks)
{
    public bool Ok => Checks.All(c => c.Severity != ServiceCheckSeverity.Fail);

    public int Failures => Checks.Count(c => c.Severity == ServiceCheckSeverity.Fail);

    public int Warnings => Checks.Count(c => c.Severity == ServiceCheckSeverity.Warn);
}

/// <summary>
/// <c>service check</c>: verifies the installed service and directory ACLs against the spec and layout.
/// Everything goes through injected interfaces, so it runs on any OS against fakes.
/// </summary>
public sealed class ServiceCheck(IServiceControlManager scm, IAclService fs, IPrincipalResolver principals)
{
    public ServiceCheckReport Run(ServiceInstallSpec spec, DirectoryLayout layout, IEnumerable<ServiceCheckItem>? extraChecks = null)
    {
        var checks = new List<ServiceCheckItem>();
        CheckService(spec, checks);
        foreach (var entry in layout.Entries.Where(e => !e.IsLink && e.Acl is not null))
        {
            CheckDirectory(entry, checks);
        }

        if (extraChecks is not null) checks.AddRange(extraChecks);
        return new ServiceCheckReport(checks);
    }

    private void CheckService(ServiceInstallSpec spec, List<ServiceCheckItem> checks)
    {
        var cfg = scm.GetConfig(spec.Name);
        if (cfg is null)
        {
            checks.Add(new("service.installed", ServiceCheckSeverity.Fail, $"Service '{spec.Name}' is not installed."));
            return;
        }

        checks.Add(new("service.installed", ServiceCheckSeverity.Pass, $"Service '{spec.Name}' is installed."));

        var asSystem = cfg.AccountName is null
            || cfg.AccountName.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)
            || cfg.AccountName.Equals(@"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase);
        if (asSystem)
        {
            checks.Add(new("service.account", spec.AllowLocalSystem ? ServiceCheckSeverity.Warn : ServiceCheckSeverity.Fail, "Service runs as LocalSystem."));
        }
        else if (!ServiceProvisioner.SameAccount(cfg.AccountName, spec.EffectiveAccount))
        {
            checks.Add(new("service.account", ServiceCheckSeverity.Warn, $"Account is '{cfg.AccountName}', expected '{spec.EffectiveAccount.Name}'."));
        }
        else
        {
            checks.Add(new("service.account", ServiceCheckSeverity.Pass, $"Runs as {cfg.AccountName}."));
        }

        var path = cfg.BinaryPathName.Trim();
        var unquotedWithSpace = !path.StartsWith('"') && path.Contains(' ');
        checks.Add(unquotedWithSpace
            ? new("service.binary-path", ServiceCheckSeverity.Fail, "Binary path contains spaces and is not quoted (unquoted service path).")
            : string.Equals(path, spec.CommandLine, StringComparison.Ordinal)
                ? new("service.binary-path", ServiceCheckSeverity.Pass, "Command line matches.")
                : new("service.binary-path", ServiceCheckSeverity.Warn, $"Command line differs from expected ({spec.CommandLine})."));

        checks.Add(cfg.StartMode == spec.StartMode
            ? new("service.start-mode", ServiceCheckSeverity.Pass, $"Start mode {cfg.StartMode}.")
            : new("service.start-mode", ServiceCheckSeverity.Warn, $"Start mode {cfg.StartMode}, expected {spec.StartMode}."));

        checks.Add(cfg.FailureRestartActions > 0
            ? new("service.recovery", ServiceCheckSeverity.Pass, $"{cfg.FailureRestartActions} restart action(s) on failure.")
            : new("service.recovery", ServiceCheckSeverity.Warn, "No restart-on-failure actions configured."));

        checks.Add(cfg.SidType == spec.SidType
            ? new("service.sid-type", ServiceCheckSeverity.Pass, $"SID type {cfg.SidType}.")
            : new("service.sid-type", ServiceCheckSeverity.Warn, $"SID type {cfg.SidType}, expected {spec.SidType}."));
    }

    private void CheckDirectory(LayoutEntry entry, List<ServiceCheckItem> checks)
    {
        var id = $"dir.{entry.Role}";
        var acl = fs.DirectoryExists(entry.Path) ? fs.Read(entry.Path) : null;
        if (acl is null)
        {
            checks.Add(new(id, ServiceCheckSeverity.Fail, $"{entry.Path} is missing."));
            return;
        }

        var spec = entry.Acl!;
        var problems = new List<(ServiceCheckSeverity, string)>();

        if (spec.CutInheritance && !acl.InheritanceProtected)
        {
            problems.Add((ServiceCheckSeverity.Fail, "inheritance is not cut (inherits parent permissions)"));
        }

        var expectedSids = spec.Rules.Select(r => principals.ResolveSid(r.Principal)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        expectedSids.Add(WellKnownPrincipalResolver.CreatorOwnerSid);
        expectedSids.Add(WellKnownPrincipalResolver.TrustedInstallerSid);

        foreach (var rule in spec.Rules)
        {
            var sid = principals.ResolveSid(rule.Principal);
            var granted = acl.Aces.Where(a => a.Allow && a.Sid.Equals(sid, StringComparison.OrdinalIgnoreCase))
                .Aggregate(AccessRights.None, (acc, a) => acc | a.Rights);
            // Administrators on a "read" log folder may legitimately have more; only missing rights are drift.
            if ((granted & rule.Rights) != rule.Rights)
            {
                problems.Add((ServiceCheckSeverity.Fail, $"{rule.Principal} lacks {rule.Rights & ~granted}"));
            }
        }

        var serviceSid = principals.ResolveSid(LayoutPrincipal.ServiceAccount);
        foreach (var ace in acl.Aces.Where(a => a.Allow && a.Rights != AccessRights.None))
        {
            var broad = WellKnownPrincipalResolver.IsBroadGroup(ace.Sid);
            var expected = expectedSids.Contains(ace.Sid);
            var canWrite = (ace.Rights & (AccessRights.Write | AccessRights.Delete)) != 0;
            var spec_ = spec.Rules.FirstOrDefault(r => principals.ResolveSid(r.Principal).Equals(ace.Sid, StringComparison.OrdinalIgnoreCase));

            if (entry.IsBinary && canWrite && !ace.Sid.Equals(WellKnownPrincipalResolver.AdministratorsSid, StringComparison.OrdinalIgnoreCase)
                && !ace.Sid.Equals(WellKnownPrincipalResolver.SystemSid, StringComparison.OrdinalIgnoreCase)
                && !ace.Sid.Equals(WellKnownPrincipalResolver.TrustedInstallerSid, StringComparison.OrdinalIgnoreCase)
                && !ace.Sid.Equals(WellKnownPrincipalResolver.CreatorOwnerSid, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add((ServiceCheckSeverity.Fail, $"binaries are writable by {Describe(ace.Sid, serviceSid)}"));
            }
            else if (!expected && (entry.IsSensitive || broad))
            {
                var worst = entry.IsSensitive || broad ? ServiceCheckSeverity.Fail : ServiceCheckSeverity.Warn;
                if (entry.IsBinary && !canWrite) continue; // default Users read/execute on Program Files is fine
                problems.Add((worst, $"{Describe(ace.Sid, serviceSid)} has unexpected access ({ace.Rights})"));
            }
            else if (!expected)
            {
                problems.Add((ServiceCheckSeverity.Warn, $"unexpected principal {ace.Sid} ({ace.Rights})"));
            }
            else if (spec_ is not null && ExceedsWrite(spec_, ace, entry))
            {
                problems.Add((ServiceCheckSeverity.Warn, $"{spec_.Principal} has more write access than intended"));
            }
        }

        // Key and secret files must not be readable by broad groups, whatever the directory says.
        if (entry.Role is LayoutRole.Secrets or LayoutRole.Certs)
        {
            foreach (var file in fs.EnumerateFiles(entry.Path))
            {
                var fileAcl = fs.Read(file);
                if (fileAcl?.Aces.Any(a => a.Allow && WellKnownPrincipalResolver.IsBroadGroup(a.Sid) && (a.Rights & AccessRights.Read) != 0) == true)
                {
                    problems.Add((ServiceCheckSeverity.Fail, $"{Path.GetFileName(file)} is readable by Users/Everyone"));
                }
            }
        }

        if (problems.Count == 0)
        {
            checks.Add(new(id, ServiceCheckSeverity.Pass, $"{entry.Path} ACL matches."));
            return;
        }

        var severity = problems.Max(p => p.Item1);
        checks.Add(new(id, severity, $"{entry.Path}: {string.Join("; ", problems.Select(p => p.Item2).Distinct())}."));
    }

    private static bool ExceedsWrite(AclRule intended, ObservedAce ace, LayoutEntry entry) =>
        (intended.Rights & AccessRights.Write) == 0 && (ace.Rights & AccessRights.Write) != 0
        && intended.Principal == LayoutPrincipal.ServiceAccount && !entry.IsBinary;

    private static string Describe(string sid, string serviceSid) => sid switch
    {
        WellKnownPrincipalResolver.UsersSid => "Users",
        WellKnownPrincipalResolver.EveryoneSid => "Everyone",
        WellKnownPrincipalResolver.AuthenticatedUsersSid => "Authenticated Users",
        _ when sid.Equals(serviceSid, StringComparison.OrdinalIgnoreCase) => "the service account",
        _ => sid,
    };
}
