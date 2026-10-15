namespace DbData.Hosting.WindowsService;

/// <summary>Who an ACL rule is about. Resolved to a SID by an <see cref="IPrincipalResolver"/>.</summary>
public enum LayoutPrincipal
{
    ServiceAccount,
    Administrators,
    System,
    Users,
}

[Flags]
public enum AccessRights
{
    None = 0,
    Read = 1,
    Execute = 2,
    Write = 4,
    Delete = 8,
    ReadExecute = Read | Execute,
    Modify = Read | Execute | Write | Delete,
    Full = Modify | 16,
}

public enum LayoutRole
{
    InstallRoot,
    Versions,
    Current,
    DataRoot,
    Config,
    Data,
    Secrets,
    Certs,
    Logs,
    Updates,
}

/// <summary>Allow-rule for one principal.</summary>
public sealed record AclRule(LayoutPrincipal Principal, AccessRights Rights);

/// <summary>The intended ACL of one directory, as data.</summary>
public sealed record AclSpec(bool CutInheritance, IReadOnlyList<AclRule> Rules);

public sealed record LayoutEntry(LayoutRole Role, string Path, AclSpec? Acl, string Purpose)
{
    /// <summary>The <c>current</c> junction has no ACL of its own.</summary>
    public bool IsLink => Role == LayoutRole.Current;

    /// <summary>Holds key material or state; nobody beyond the expected principals may read or write.</summary>
    public bool IsSensitive => Role is LayoutRole.Secrets or LayoutRole.Certs or LayoutRole.Data;

    /// <summary>Holds executables; nobody but administrators may write.</summary>
    public bool IsBinary => Role is LayoutRole.InstallRoot or LayoutRole.Versions;
}

/// <summary>
/// The standard on-disk layout (Plan 03 section 3.2) with intended ACLs as data:
/// <code>
/// %ProgramFiles%\P\versions\ver | current (junction)
/// %ProgramData%\P\config | data | secrets | certs | logs | updates
/// </code>
/// This model is the single source for installers (apply) and <see cref="ServiceDoctor"/> (verify).
/// </summary>
public sealed class DirectoryLayout
{
    private DirectoryLayout(string product, IReadOnlyList<LayoutEntry> entries)
    {
        Product = product;
        Entries = entries;
    }

    public string Product { get; }

    public IReadOnlyList<LayoutEntry> Entries { get; }

    public LayoutEntry this[LayoutRole role] => Entries.First(e => e.Role == role);

    public static DirectoryLayout ForProduct(string product, string programFiles, string programData)
    {
        if (string.IsNullOrWhiteSpace(product) || product.IndexOfAny(['\\', '/', ':']) >= 0)
        {
            throw new ArgumentException("Invalid product name.", nameof(product));
        }

        var root = Path.Combine(programFiles, product);
        var data = Path.Combine(programData, product);

        static AclSpec Locked(params AclRule[] rules) => new(true, rules);
        var admin = new AclRule(LayoutPrincipal.Administrators, AccessRights.Full);
        var system = new AclRule(LayoutPrincipal.System, AccessRights.Full);
        AclRule Svc(AccessRights r) => new(LayoutPrincipal.ServiceAccount, r);

        List<LayoutEntry> entries =
        [
            new(LayoutRole.InstallRoot, root, new AclSpec(false, [admin, system, Svc(AccessRights.ReadExecute)]), "Product install root; binaries are not writable by the service."),
            new(LayoutRole.Versions, Path.Combine(root, "versions"), new AclSpec(false, [admin, system, Svc(AccessRights.ReadExecute)]), "One folder per installed version."),
            new(LayoutRole.Current, Path.Combine(root, "current"), null, "Junction to the active version (Plan 08)."),
            new(LayoutRole.DataRoot, data, Locked(admin, system, Svc(AccessRights.ReadExecute)), "Machine data root; Users have no access."),
            new(LayoutRole.Config, Path.Combine(data, "config"), Locked(admin, system, Svc(AccessRights.ReadExecute)), "Service reads, Administrators write."),
            new(LayoutRole.Data, Path.Combine(data, "data"), Locked(admin, system, Svc(AccessRights.Modify)), "Service state."),
            new(LayoutRole.Secrets, Path.Combine(data, "secrets"), Locked(admin, system, Svc(AccessRights.Modify)), "Secret files."),
            new(LayoutRole.Certs, Path.Combine(data, "certs"), Locked(admin, system, Svc(AccessRights.Modify)), "PFX and key material."),
            new(LayoutRole.Logs, Path.Combine(data, "logs"), Locked(new AclRule(LayoutPrincipal.Administrators, AccessRights.ReadExecute), system, Svc(AccessRights.Modify)), "Service writes, Administrators read."),
            new(LayoutRole.Updates, Path.Combine(data, "updates"), Locked(admin, system, Svc(AccessRights.Modify)), "Update staging."),
        ];
        return new DirectoryLayout(product, entries);
    }
}

/// <summary>Maps layout principals to SIDs.</summary>
public interface IPrincipalResolver
{
    string ResolveSid(LayoutPrincipal principal);
}

public sealed class WellKnownPrincipalResolver(string serviceAccountSid) : IPrincipalResolver
{
    public const string AdministratorsSid = "S-1-5-32-544";
    public const string SystemSid = "S-1-5-18";
    public const string UsersSid = "S-1-5-32-545";
    public const string EveryoneSid = "S-1-1-0";
    public const string AuthenticatedUsersSid = "S-1-5-11";
    public const string CreatorOwnerSid = "S-1-3-0";
    public const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    public string ResolveSid(LayoutPrincipal principal) => principal switch
    {
        LayoutPrincipal.ServiceAccount => serviceAccountSid,
        LayoutPrincipal.Administrators => AdministratorsSid,
        LayoutPrincipal.System => SystemSid,
        LayoutPrincipal.Users => UsersSid,
        _ => throw new ArgumentOutOfRangeException(nameof(principal)),
    };

    /// <summary>SIDs that stand for "ordinary users" and must never reach sensitive data.</summary>
    public static bool IsBroadGroup(string sid) =>
        sid is UsersSid or EveryoneSid or AuthenticatedUsersSid;
}

/// <summary>An ACE as read back from disk.</summary>
public sealed record ObservedAce(string Sid, AccessRights Rights, bool Allow, bool Inherited);

public sealed record ObservedAcl(bool InheritanceProtected, IReadOnlyList<ObservedAce> Aces);

/// <summary>Read-only view of the file system used by the doctor; fakeable.</summary>
public interface IFileSystemAclReader
{
    bool DirectoryExists(string path);

    IEnumerable<string> EnumerateFiles(string directory);

    ObservedAcl? ReadAcl(string path);
}

/// <summary>
/// Applies a <see cref="LayoutEntry"/>'s ACL. NOTE: a parallel ACL helper exists in DbData.Secrets.Windows
/// (Plan 02); reconcile the two at merge. This one is deliberately small and dependency free.
/// </summary>
public interface IAclPolicy
{
    void Apply(LayoutEntry entry, IPrincipalResolver principals);
}
