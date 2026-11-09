using DbDataStore.Server;

if (await DbDataKit.Upgrade.UpgradeHelperHost.TryRunAsync(args) is { } helperExit) return helperExit;
return await StoreCli.RunAsync(args);
