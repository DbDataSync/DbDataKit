using DbDataRunner.Server;

if (await DbDataKit.Upgrade.UpgradeHelperHost.TryRunAsync(args) is { } helperExit) return helperExit;
return await RunnerCli.RunAsync(args);
