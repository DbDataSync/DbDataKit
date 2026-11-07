using System.Runtime.CompilerServices;

namespace DbDataRunner.Tests;

/// <summary>
/// The tests run real servers and task hosts in the test process, several at once, and some of the code under test waits on a database with a blocking call. On a two-core CI runner the thread pool then adds
/// threads one at a time and a server's heartbeat can go silent for longer than its lease (a Windows or Linux runner went 35 s silent against a 30 s lease), which is the test machine's starvation, not a defect of
/// the runner. A generous minimum number of threads keeps the timers on time.
/// </summary>
internal static class TestProcess
{
    [ModuleInitializer]
    internal static void RaiseThreadPoolMinimum() => ThreadPool.SetMinThreads(64, 64);
}
