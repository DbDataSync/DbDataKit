namespace DbDataKit.Runner;

/// <summary>
/// The names of the Runner's built-in package <c>dbdatakit-metadata</c>: its tasks <c>discover</c> and <c>describe</c> read the shape of a database through a slot of another package (the project's), with the login the channel binds to
/// that slot, and answer shape only. The engine, the management API and the package's own jobs share these names.
/// Design: docs/authoring-metadata.md#the-built-in-package-dbdatakit-metadata
/// </summary>
public static class MetadataPackage
{
    /// <summary>The package's name on every runner.</summary>
    public const string Name = "dbdatakit-metadata";

    /// <summary>The package's assembly, which the task host loads.</summary>
    public const string Assembly = "DbDataKit.Runner.Metadata.dll";

    /// <summary>The task that reads databases, schemas, tables, columns and keys.</summary>
    public const string Discover = "discover";

    /// <summary>The task that reads the columns of a query without running it.</summary>
    public const string Describe = "describe";

    /// <summary>The one slot each task declares. A run of the package never uses a binding of its own: the slot is resolved through the binding of <see cref="PackageInput"/>'s slot <see cref="SlotInput"/> in the run's channel.</summary>
    public const string Slot = "connection";

    /// <summary>The input naming the package whose slot is read (the project's package).</summary>
    public const string PackageInput = "package";

    /// <summary>The input naming that package's slot.</summary>
    public const string SlotInput = "slot";

    /// <summary>The input naming the login role the task asks for: <c>read</c> when the bound connection has a read login, else <c>default</c>.</summary>
    public const string RoleInput = "role";

    /// <summary>The names of the tasks.</summary>
    public static IReadOnlyList<string> Tasks { get; } = [Discover, Describe];

    /// <summary>True for a task of the package.</summary>
    public static bool IsTask(string task) => task is Discover or Describe;
}
