using DbDataKit.Serving.Conformance;

namespace DbDataKit.Serving.Tests;

/// <summary>The conformance suite (plan 19, N03) over the fake host offered as core services, with no write path: the baseline every other host is held to.</summary>
public class FakeHostConformanceTests : ServingServicesConformanceTests
{
    protected override Task<ServingTarget> CreateAsync() => Task.FromResult(Target(withWrites: false));

    internal static ServingTarget Target(bool withWrites)
    {
        var reader = new ServingCredentials.Password("reader", "reader-pw");
        return new ServingTarget
        {
            Services = new HostServices(new FakeServingHost(withWrites)),
            Full = new ServingCredentials.Password("admin", "admin-pw"),
            Wrong = new ServingCredentials.Password("admin", "wrong"),
            Source = "main",
            Open = new ServingRequest.Sql("main", "public.items"),
            OpenRows = 5,
            VisibleTable = "items",
            Missing = new ServingRequest.Sql("main", "public.nothing"),
            UnknownSource = new ServingRequest.Sql("other", "public.items"),
            Refused = new ServingRefusal(reader, new ServingRequest.Sql("main", "secret.salaries")),
            Limited = new ServingLimited(reader, new ServingRequest.Sql("main", "public.items"), FakeServingHost.ReaderRowLimit),
            Restricted = reader,
            HiddenTable = "salaries",
            Write = withWrites ? new ServingRequest.Sql("main", "update items") : null,
            RefusedWrite = withWrites ? new ServingRefusal(reader, new ServingRequest.Sql("main", "update items")) : null,
            SourceReadOnly = !withWrites,
            PrepareRefuses = false,
            FactsRefuseUnknownSource = false,
        };
    }
}

/// <summary>The same suite over the fake host with a write path.</summary>
public class FakeHostWithWritesConformanceTests : ServingServicesConformanceTests
{
    protected override Task<ServingTarget> CreateAsync() => Task.FromResult(FakeHostConformanceTests.Target(withWrites: true));
}
