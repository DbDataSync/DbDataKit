using DbDataKit.Runner;
using DbDataKit.Runner.Api;
using System.Runtime.CompilerServices;
using Xunit;

// One collection for the whole assembly: the tests run one at a time. They start real servers, real task host processes and (the group tests) several runners that must keep a 30 s lease with their heartbeats.
// In parallel on a two-core CI runner they starve one another: a server went 35 s silent against its lease, and a run was taken over and recorded twice.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

namespace DbDataRunner.Tests;

/// <summary>
/// The tests run real servers and task hosts in the test process, and some of the code under test waits on a database with a blocking call. On a two-core CI runner the thread pool adds threads one at a time
/// and a server's heartbeat can go silent for longer than its lease, which is the test machine's starvation, not a defect of the runner. A generous minimum number of threads keeps the timers on time.
/// </summary>
internal static class TestProcess
{
    [ModuleInitializer]
    internal static void RaiseThreadPoolMinimum() => ThreadPool.SetMinThreads(64, 64);
}
