namespace DbDataSync.Api.Tests;

/// <summary>
/// <see cref="TestApiFactory"/> for <see cref="RunWatermarkTimeTests"/>, which dates pages of run history this server kept. It used to remove the server's own
/// scheduler tick, which could enqueue a competing row under the test; since plan 28c E22 the server has no scheduler at all (the Runner is Sync's only one), so
/// nothing is removed.
/// </summary>
public sealed class RunWatermarkApiFactory : TestApiFactory;
