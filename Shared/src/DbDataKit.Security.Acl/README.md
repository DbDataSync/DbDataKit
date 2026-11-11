# DbDataKit.Security.Acl

The single source of truth for file and directory ACLs, shared by `DbDataKit.Secrets.Windows` and `DbDataKit.Hosting.WindowsService`.

- `AclHelper` (static, cross-platform): `Restrict`, `EnsureRestrictedDirectory`, `WriteAllBytesRestricted`, `Verify` -> `AclReport`/`AclDrift`, `ResolveIdentity`. Windows: protected DACL with explicit FullControl for the identities; Unix: 0600/0700.
- `IAclService`: `Apply(directory, AclSpecification)`, `Read(path)` -> `ObservedAcl`, `DirectoryExists`, `EnumerateFiles`. The seam used by the service doctor and installer; fake it in tests. `FileSystemAclService` is the real one (Apply/Read are Windows-only; Read returns null elsewhere).
- Data model: `AccessRights`, `AclEntry(sid, rights)`, `AclSpecification(cutInheritance, entries)`, `ObservedAce`, `ObservedAcl`.

Moved from `DbDataKit.Secrets.Windows` (namespace change from `DbDataKit.Secrets.Windows` to `DbDataKit.Security.Acl`) and from Hosting (`IAclPolicy`/`WindowsAclPolicy`/`IFileSystemAclReader` removed).
