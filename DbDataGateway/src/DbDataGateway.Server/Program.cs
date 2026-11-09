using DbDataGateway.Server.Hosting;

if (await DbDataKit.Upgrade.UpgradeHelperHost.TryRunAsync(args) is { } helperExit) return helperExit;
return await Cli.RunAsync(args);
