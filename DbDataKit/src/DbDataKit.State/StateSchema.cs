namespace DbDataKit.State;

/// <summary>
/// A product's schema: numbered templates, each applied once, in order, the first time a database is opened at a lower version. A product lists its own tables and
/// includes the shared fragments (<see cref="IdentitySchema"/>, <see cref="NotificationSchema"/>) where it wants them. Never reorder or edit an entry that has shipped;
/// append.
/// </summary>
public sealed class StateSchema(IReadOnlyList<string> templates)
{
    public int Count => templates.Count;

    /// <summary>Each script's statements, rendered for one engine and split so they can be executed one at a time (some engines refuse several DDL statements in one command).</summary>
    public IReadOnlyList<IReadOnlyList<string>> ScriptsFor(StateDialect dialect) =>
        [.. templates.Select(template => StateTemplates.SplitStatements(StateTemplates.Render(template, dialect)))];
}
