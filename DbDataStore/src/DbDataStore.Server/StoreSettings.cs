namespace DbDataStore.Server;

/// <summary>The folder the <c>dbdatastore</c> command line works on (<c>--repo</c>): its models, their connections and the Gateways it publishes to. It was the Store server's directory until plan 33 SC23 deleted the server.</summary>
public static class StoreSettings
{
    /// <summary>The service's name in the suite's folders and variables (<see cref="DbDataKit.State.SuiteEnvironment"/>): <c>store</c>.</summary>
    public const string ServiceName = "store";

    /// <summary>What <c>--repo</c> defaults to: <c>DBDATAKIT_DATA_STORE</c> or the folder under <c>DBDATAKIT_DATA</c>, else a folder under the user's local application data.</summary>
    public static string DefaultDirectory(Func<string, string?>? environment = null) =>
        DbDataKit.State.SuiteEnvironment.ResolveDataFolder(ServiceName, environment, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dbdatastore", "server"));
}
