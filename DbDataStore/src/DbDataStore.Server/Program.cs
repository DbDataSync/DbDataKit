using DbDataStore.Server;

if (await DbDataKit.Upgrade.UpgradeHelperHost.TryRunAsync(args) is { } helperExit) return helperExit;
// the runtime of a model's definition package (plan 33 SC24): a runner starts the program with --runner to run the model's deploy task
if (args.Length > 0 && args[0] == StoreRunnerEntry.Flag) return await StoreRunnerEntry.RunAsync(args[1..]);
return await StoreCli.RunAsync(args);
