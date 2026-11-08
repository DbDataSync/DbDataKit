using DbDataKit.State;

namespace DbDataKit.Views.Hosting;

/// <summary>Where the module records who opened or rendered what (the install's audit, plan 29 section 3.4). An entry that cannot be written stops the answer: nothing is given that was not recorded.</summary>
public interface IViewsAudit
{
    void Record(AuditEvent entry);
}

/// <summary>The group's audit in its state database.</summary>
public sealed class StateViewsAudit(AuditLog log) : IViewsAudit
{
    public void Record(AuditEvent entry) => log.Record(entry);
}

/// <summary>The verbs the module records.</summary>
public static class ViewsAuditVerbs
{
    public const string List = "view.list";
    public const string Open = "view.open";
    public const string Render = "view.render";
    public const string Export = "view.export";
    public const string Print = "view.print";
}
