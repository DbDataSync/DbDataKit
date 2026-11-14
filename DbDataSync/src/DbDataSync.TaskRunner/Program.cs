using DbDataSync.TaskRunner.Runner;

// "--runner": started by a runner as the task of a package (plan 28c E03). Since plan 28c E22 the Runner is Sync's only scheduler: there is no worker of the Sync server any more.
if (args is ["--runner", ..])
    return await RunnerEntry.RunAsync(args[1..]);

Console.Error.WriteLine("The Sync engine runs only as a task of a Runner (`--runner`, started by the Runner for a published replication set); the worker the Sync server used to start is gone (plan 28c E22).");
return 2;
