using DbDataBuild.State;
using DbDataKit.Lineage;

namespace DbDataBuild.Execution;

/// <summary>
/// The identity a copy of Build's tracking is kept under (plan 28c E16, plan 18 section 4): the tracking store as lineage names a dataset (engine, server, database and the schema of the tracking tables, worked out from the address of the login, so two
/// spellings of one server are one identity and a development copy at another server is another) and the data connection whose records they are, because one store holds any number of connections' records. Nothing secret is in it: only the address keys
/// of the login are read (<see cref="LoginSettings.AddressOnlyDefinition"/>).
/// </summary>
public static class BuildTrackingIdentity
{
    public const string Prefix = "buildtracking:";

    public static string For(LoginSettings trackingLogin, TrackingScope scope)
    {
        ArgumentNullException.ThrowIfNull(trackingLogin);
        var dataset = LineageIdentity.Dataset(trackingLogin.AddressOnlyDefinition(), scope.SchemaName, "tracking");
        return $"{Prefix}{dataset}@{scope.Connection}";
    }
}
