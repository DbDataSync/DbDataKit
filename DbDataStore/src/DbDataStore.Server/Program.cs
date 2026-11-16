using DbDataStore.Server;

// the runtime of a model's definition package (plan 33 SC24): a runner starts the program with --runner to run the model's deploy or contract task
if (args.Length > 0 && args[0] == StoreRunnerEntry.Flag) return await StoreRunnerEntry.RunAsync(args[1..]);
// the Store server was deleted (plan 33 SC23): every other command answers with where its feature went
return await StoreCli.RunAsync(args, Console.Out, Console.Error);
