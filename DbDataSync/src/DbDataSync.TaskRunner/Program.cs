using DbDataSync.TaskRunner;
using DbDataSync.TaskRunner.Runner;

// "--runner": started by a runner as the task of a package (plan 28c E03); otherwise the worker the API spawns
return args is ["--runner", ..] ? await RunnerEntry.RunAsync(args[1..]) : await WorkerEntry.RunAsync(args);
