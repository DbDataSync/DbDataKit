namespace DbDataKit.Security.Acl;

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

/// <summary>Allow-rule for one SID.</summary>
public sealed record AclEntry(string Sid, AccessRights Rights);

/// <summary>The intended ACL of one directory (rules inherit to children).</summary>
public sealed record AclSpecification(bool CutInheritance, IReadOnlyList<AclEntry> Entries);

/// <summary>An ACE as read back from disk.</summary>
public sealed record ObservedAce(string Sid, AccessRights Rights, bool Allow, bool Inherited);

public sealed record ObservedAcl(bool InheritanceProtected, IReadOnlyList<ObservedAce> Aces);
