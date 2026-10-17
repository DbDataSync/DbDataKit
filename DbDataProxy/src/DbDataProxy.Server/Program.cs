using DbDataProxy.Server.Hosting;

if (await HealthCheck.TryHandleAsync(args) is { } healthExit) return healthExit;
return await Cli.RunAsync(args);
