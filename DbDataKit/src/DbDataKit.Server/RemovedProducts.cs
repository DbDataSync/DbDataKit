namespace DbDataKit.Server;

/// <summary>
/// The servers plan 33 SC23 deleted: the Build, Sync and Store servers. Their programs stay as command lines and Runner runtimes (<c>dbdatabuild</c>, <c>dbdatasync</c>, <c>dbdatastore</c>), but none of them is a server the console
/// manages any more. A clean break, never a silent one: an announcement from one is refused with the sentence, a call the relay would pass to one already paired is answered with it (410), and the Servers page shows it beside the server
/// (the console's web app has the same sentences, <c>removedProducts</c> in <c>ServerModule.tsx</c>), so the person forgets the server once its work has moved.
/// </summary>
public static class RemovedProducts
{
    public const string Build = "dbdatabuild";
    public const string Sync = "dbdatasync";
    public const string Store = "dbdatastore";

    /// <summary>Where the features of a deleted server went, or null for a product that was not deleted.</summary>
    public static string? Message(string? product) => product?.ToLowerInvariant() switch
    {
        Build => "The Build server was deleted (plan 33 SC23): a project is authored in a build project of the Workspace and published from there to a Runner and a Gateway; its refresh plans run on the Runner, and a plan is applied, a change acknowledged or seeds loaded as the manual Runner tasks apply_plan, ack and seed, with the logins bound on the Runner.",
        Sync => "The Sync server was deleted (plan 33 SC23): a replication is authored in a sync project of the Workspace and published from there to a Runner, which runs it (run now, pause and resume, bulk loads, verifications and the provision task are Runner tasks), with the logins bound on the Runner.",
        Store => "The Store server was deleted (plan 33 SC23): a model is authored in a store project of the Workspace, its tables are deployed from the Deploy tab there as Runner tasks, its rows are written on the Rows tab against a Gateway, and change requests are decided on the Approvals page.",
        _ => null,
    };
}
